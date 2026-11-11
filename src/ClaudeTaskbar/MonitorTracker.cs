using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudeTaskbar;

/// <summary>
/// Running Monitor tasks of a session, from its transcript. A Monitor is a background watch
/// Claude starts (for example "until the emulator has booted"); Claude ends its turn and is
/// woken by the Monitor's notification, so the session isn't finished while one runs.
///
/// BUG found live, 2026-11-11 (a session said "Waiting for the emulator" and showed green
/// until the watch fired and it went back to orange): a Monitor's output file is not held
/// open while it runs, so <see cref="BackgroundTaskTracker"/>'s held-file check missed it.
///
/// Signal, from Claude Code's own transcript records (checked over 60 recent transcripts):
/// - Start: a tool result "Monitor started (task &lt;id&gt;, expires in &lt;n&gt;m …)".
/// - End: a &lt;task-notification&gt; for that task id carrying a &lt;status&gt; (completed, failed,
///   stopped, killed; "… stream ended"). Events while it runs ("Monitor event: …") carry no
///   status, so they don't end it.
/// - Safety cap, labelled: Claude Code says when the watch expires. Past that time plus a
///   minute it stops counting even if no end record was seen, so a lost record can't keep a
///   circle yellow for good.
///
/// The transcript is read incrementally, as LastActivityReader does: only new complete lines.
/// </summary>
public static class MonitorTracker
{
    private static readonly TimeSpan ExpiryGrace = TimeSpan.FromMinutes(1);

    private static readonly Regex Started = new(@"Monitor started \(task (\w+), expires in (\d+)m", RegexOptions.Compiled);
    private static readonly Regex NotificationId = new(@"<task-id>(\w+)</task-id>", RegexOptions.Compiled);
    private static readonly Regex NotificationStatus = new(@"<status>\w+</status>", RegexOptions.Compiled);

    private sealed class Scan
    {
        public long Offset;
        public readonly Dictionary<string, DateTime> Expires = new(StringComparer.Ordinal);
    }

    private static readonly Dictionary<string, Scan> Scans = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ids of the Monitors started in this transcript and not yet ended.</summary>
    public static IEnumerable<string> Running(string transcriptPath) => Running(transcriptPath, DateTime.UtcNow);

    /// <summary>As of <paramref name="now"/>: for replaying a transcript in a test.</summary>
    public static IEnumerable<string> Running(string transcriptPath, DateTime now)
    {
        var scan = Update(transcriptPath);
        if (scan == null) return Array.Empty<string>();
        return scan.Expires.Where(e => now < e.Value + ExpiryGrace).Select(e => e.Key).ToList();
    }

    private static Scan? Update(string path)
    {
        try
        {
            if (!Scans.TryGetValue(path, out var scan)) Scans[path] = scan = new Scan();
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < scan.Offset) // rewritten: start over
            {
                scan.Offset = 0;
                scan.Expires.Clear();
            }
            if (fs.Length == scan.Offset) return scan;

            // Raw bytes in 1 MB chunks; a line is decoded only when it holds one of the two
            // markers, which almost no line does. Decoding every character took 7.6 s on a
            // 493 MB transcript, on the thread that also serves the taskbar's input.
            fs.Seek(scan.Offset, SeekOrigin.Begin);
            var chunk = new byte[1 << 20];
            var pending = new MemoryStream(); // a line that spans chunk boundaries
            var consumed = scan.Offset;
            int n;
            while ((n = fs.Read(chunk, 0, chunk.Length)) > 0)
            {
                var from = 0;
                while (true)
                {
                    var nl = Array.IndexOf(chunk, (byte)'\n', from, n - from);
                    if (nl < 0) break;
                    ReadOnlySpan<byte> line;
                    if (pending.Length > 0)
                    {
                        pending.Write(chunk, from, nl - from);
                        line = pending.ToArray();
                        pending.SetLength(0);
                    }
                    else line = new ReadOnlySpan<byte>(chunk, from, nl - from);
                    consumed += line.Length + 1;
                    from = nl + 1;

                    var start = line.IndexOf(StartMarker) >= 0;
                    var end = line.IndexOf(EndMarker) >= 0;
                    if (!start && !end) continue;
                    var text = System.Text.Encoding.UTF8.GetString(line);
                    if (start) OnStart(text, scan);
                    if (end) OnNotification(text, scan);
                }
                pending.Write(chunk, from, n - from);
            }
            scan.Offset = consumed;
            return scan;
        }
        catch (Exception)
        {
            return null; // gone or locked: no claim either way
        }
    }

    private static readonly byte[] StartMarker = "Monitor started (task"u8.ToArray();
    private static readonly byte[] EndMarker = "<task-notification>"u8.ToArray();

    /// <summary>A user record whose tool result begins with the Monitor's start line. Only
    /// the result's own text counts, not a record that merely quotes it.</summary>
    private static void OnStart(string line, Scan scan)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != "user") return;
            if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
                || !msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
            foreach (var item in content.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("type", out var it) || it.GetString() != "tool_result") continue;
                var text = ResultText(item);
                if (text == null || !text.StartsWith("Monitor started (task", StringComparison.Ordinal)) continue;
                var m = Started.Match(text);
                if (!m.Success) continue;
                var at = root.TryGetProperty("timestamp", out var ts) && ts.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : DateTime.UtcNow;
                scan.Expires[m.Groups[1].Value] = at + TimeSpan.FromMinutes(int.Parse(m.Groups[2].Value));
            }
        }
        catch (JsonException) { }
    }

    /// <summary>A tool result's text: a string, or the first text block of a list.</summary>
    private static string? ResultText(JsonElement item)
    {
        if (!item.TryGetProperty("content", out var c)) return null;
        if (c.ValueKind == JsonValueKind.String) return c.GetString();
        if (c.ValueKind != JsonValueKind.Array) return null;
        foreach (var block in c.EnumerateArray())
            if (block.ValueKind == JsonValueKind.Object && block.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String)
                return tx.GetString();
        return null;
    }

    private static void OnNotification(string line, Scan scan)
    {
        // Both the queue-operation and the user record carry it; either one ends the task.
        foreach (var block in line.Split("<task-notification>").Skip(1))
        {
            var id = NotificationId.Match(block);
            if (id.Success && NotificationStatus.IsMatch(block)) scan.Expires.Remove(id.Groups[1].Value);
        }
    }
}
