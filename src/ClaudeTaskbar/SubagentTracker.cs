using System.Text.Json;

namespace ClaudeTaskbar;

/// <summary>
/// Counts a session's subagents that are still running, from Claude Code's own files.
///
/// BUG found live, 2026-09-29 (a circle showed green, then blue, while a subagent was still
/// working in that session; a subagent still working means the work is not done yet).
/// Subagents run in the background by default, so the main turn can end (Stop hook →
/// "idle") while one keeps working. In the session examined: agent launched 07:32:09, turn
/// ended 07:34:44, agent finished 07:38:31, so the circle said "done" for 4
/// minutes of real work.
///
/// Verified live on that session:
/// - Each subagent writes `&lt;project&gt;/&lt;sessionId&gt;/subagents/agent-&lt;id&gt;.jsonl`,
///   created at launch and written until it finishes. Its last write was 07:38:31.18.
/// - When it finishes, the parent transcript gets a `&lt;task-notification&gt;` whose
///   `&lt;task-id&gt;` is that same id, with status completed, failed or killed. This one was
///   recorded 07:38:31.43.
/// - A notification can say the agent "may resume on its own... the same task-id notifies
///   again", so a notification alone isn't final.
///
/// Rule: an agent is running if its transcript was written after its latest notification (or
/// it has none), and within the last 30 minutes. The age limit is there so an agent killed
/// without a notification (VS Code or PC restart) can't keep a circle orange for ever. A long
/// quiet command inside an agent is covered, because a Bash call times out at 10 minutes by
/// default.
///
/// The parent transcript can be tens of MB, so notifications are collected incrementally,
/// scanning only the newly appended tail, the same way LastActivityReader does.
/// </summary>
public static class SubagentTracker
{
    private static readonly TimeSpan DeadAfter = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan NotificationSlack = TimeSpan.FromSeconds(2);

    // transcript path -> (bytes scanned, task id -> latest notification time)
    private static readonly Dictionary<string, (long Offset, Dictionary<string, DateTime> Notified)> Scans = new(StringComparer.OrdinalIgnoreCase);

    public static int CountRunning(string? transcriptPath, string sessionId)
    {
        if (string.IsNullOrEmpty(transcriptPath)) return 0;
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(transcriptPath)!, sessionId, "subagents");
            if (!Directory.Exists(dir)) return 0;

            var now = DateTime.UtcNow;
            Dictionary<string, DateTime>? notified = null;
            var running = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "agent-*.jsonl"))
            {
                var lastWrite = File.GetLastWriteTimeUtc(file);
                if (now - lastWrite > DeadAfter) continue;

                notified ??= ScanNotifications(transcriptPath);
                var id = Path.GetFileNameWithoutExtension(file)["agent-".Length..];
                if (!notified.TryGetValue(id, out var at) || lastWrite > at + NotificationSlack) running++;
            }
            return running;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"subagents: failed for {sessionId}: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private static Dictionary<string, DateTime> ScanNotifications(string transcriptPath)
    {
        Scans.TryGetValue(transcriptPath, out var scan);
        var notified = scan.Notified ?? new Dictionary<string, DateTime>(StringComparer.Ordinal);

        using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var offset = scan.Offset <= fs.Length ? scan.Offset : 0; // shrank (rewritten) → start over
        if (offset == 0) notified.Clear();
        fs.Seek(offset, SeekOrigin.Begin);

        using var reader = new StreamReader(fs);
        var consumed = offset;
        while (reader.ReadLine() is { } line)
        {
            // A line still being written has no newline yet; stop and pick it up next time.
            if (reader.EndOfStream && !EndsWithNewline(fs)) break;
            consumed += System.Text.Encoding.UTF8.GetByteCount(line) + 1;
            if (!line.Contains("<task-id>", StringComparison.Ordinal)) continue;
            Record(line, notified);
        }

        Scans[transcriptPath] = (consumed, notified);
        return notified;
    }

    private static bool EndsWithNewline(FileStream fs)
    {
        if (fs.Length == 0) return true;
        var pos = fs.Position;
        fs.Seek(-1, SeekOrigin.End);
        var last = fs.ReadByte();
        fs.Position = pos;
        return last == '\n';
    }

    private static void Record(string line, Dictionary<string, DateTime> notified)
    {
        DateTime? ts = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("timestamp", out var t) && t.TryGetDateTime(out var dt)) ts = dt.ToUniversalTime();
        }
        catch
        {
            return; // malformed line — skip
        }
        if (ts == null) return;

        // Raw line text, so the tags appear JSON-escaped only in their content, never in
        // the tag names themselves.
        var start = 0;
        while ((start = line.IndexOf("<task-id>", start, StringComparison.Ordinal)) >= 0)
        {
            start += "<task-id>".Length;
            var end = line.IndexOf("</task-id>", start, StringComparison.Ordinal);
            if (end < 0) break;
            var id = line[start..end];
            if (!notified.TryGetValue(id, out var prev) || ts > prev) notified[id] = ts.Value;
            start = end;
        }
    }
}
