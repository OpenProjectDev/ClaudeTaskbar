using System.Text.Json;

namespace ClaudeTaskbar;

/// <summary>
/// Resolves a real, persisted "when did this project last actually see activity" timestamp
/// from Claude Code's own transcript storage — used to fix a real limitation found live:
/// an early version tracked some sessions' idle-since clock only in this app's own memory,
/// which meant a genuinely long-idle session looked freshly-idle again after every app
/// restart. This reads the transcript's real timestamps instead, which survive restarts
/// since they're Claude Code's own on-disk data, not this app's.
///
/// Project-folder encoding verified empirically against real data, not guessed: compared
/// several real `~/.claude/projects/&lt;encoded&gt;` folder names against the real `cwd` field
/// inside their own transcripts (e.g. cwd `C:\src\MyProject` ↔ folder `C--src-MyProject`;
/// cwd `...\Taskbar Claude` ↔ folder `...-Taskbar-Claude`, confirming a space also becomes
/// `-`, not just `\` and `:`). Simple rule: every non-alphanumeric character becomes `-`,
/// one-for-one (no collapsing of consecutive separators — `C:\src` produces `C--src`, two
/// dashes for two separator characters, matching the real folder names exactly).
/// </summary>
public static class LastActivityReader
{
    private static readonly string ProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    /// <summary>The most recent real timestamp found in this project's own transcript
    /// storage for the given cwd — across all of Claude Code's session transcripts for
    /// that project, not just one — or null if none can be found (never guessed/invented;
    /// caller falls back to its own weaker signal in that case).</summary>
    public static DateTime? GetLastActivityUtc(string cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;

        var dir = Path.Combine(ProjectsRoot, Encode(cwd));
        if (!Directory.Exists(dir)) return null;

        string[] files;
        try { files = Directory.GetFiles(dir, "*.jsonl"); }
        catch { return null; }
        if (files.Length == 0) return null;

        // Cheap first pass: file mtime (metadata only) to find the most recently touched
        // transcript, rather than opening every session file for this project.
        string? newest = null;
        var newestTime = DateTime.MinValue;
        foreach (var f in files)
        {
            DateTime mtime;
            try { mtime = File.GetLastWriteTimeUtc(f); }
            catch { continue; }
            if (mtime <= newestTime) continue;
            newestTime = mtime;
            newest = f;
        }

        return newest != null ? GetLatestTimestampInFile(newest) : null;
    }

    /// <summary>Same latest-real-timestamp lookup, but for one specific transcript file
    /// (used for real sessions where we already know the exact transcript_path from the
    /// hook payload, rather than guessing which file in the project folder is "the"
    /// session).</summary>
    public static DateTime? GetLastActivityUtcForTranscript(string? transcriptPath)
    {
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath)) return null;
        return GetLatestTimestampInFile(transcriptPath);
    }

    /// <summary>Where Claude Code keeps a session's transcript, given the workspace root it
    /// was started in and its session id — `~/.claude/projects/&lt;Encode(root)&gt;/&lt;id&gt;.jsonl`.
    /// Confirmed against several real sessions' own hook-reported transcript_path.</summary>
    public static string TranscriptPathFor(string workspaceRoot, string sessionId) =>
        Path.Combine(ProjectsRoot, Encode(workspaceRoot.TrimEnd('\\', '/')), sessionId + ".jsonl");

    // path -> (bytes already scanned up to the last complete line, latest timestamp so far).
    // Transcripts reach tens of MB and are appended to constantly while a session works;
    // re-parsing the whole file every 400ms poll isn't viable, so only the newly appended
    // tail is scanned each time.
    private static readonly Dictionary<string, (long Offset, DateTime? Latest)> ScanCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// BUG (found live, 2026-09-27, when one circle didn't turn Blue immediately like the
    /// other two): taking literally the transcript's LAST
    /// line and requiring it to carry a `timestamp` field was wrong — a transcript's
    /// trailing lines are often small metadata records (`"type":"bridge-session"`,
    /// `"last-prompt"`, `"cost-state"`, etc.) written after the final real message, and
    /// none of those carry a `timestamp` field at all. Confirmed live by inspecting a real
    /// transcript's raw tail bytes. Taking "the last line" silently found no timestamp for
    /// that one project and fell all the way back to the weaker in-memory heuristic, while
    /// the other two projects' transcripts happened to end on a real message line and
    /// worked immediately. Fixed by scanning every line and keeping the MAXIMUM timestamp
    /// found anywhere in the file, rather than trusting line order/position at all.
    /// </summary>
    private static DateTime? GetLatestTimestampInFile(string path)
    {
        try
        {
            ScanCache.TryGetValue(path, out var cached);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // File shrank (rewritten/truncated) — start over rather than trust the cache.
            var offset = cached.Offset <= fs.Length ? cached.Offset : 0;
            var latest = cached.Offset <= fs.Length ? cached.Latest : null;
            if (offset == fs.Length) return latest;

            // Streamed in fixed chunks, never the whole file at once: the first scan of a
            // tens-of-MB transcript used to allocate it all as one byte[] (and again as a
            // string), which is how the app sat at 323 MB right after launch. Only complete
            // lines are consumed — a line still being written is picked up next call.
            fs.Seek(offset, SeekOrigin.Begin);
            var chunk = new byte[1 << 20];
            var pending = new MemoryStream(); // bytes of a line that spans chunk boundaries
            var consumed = offset;
            int n;
            while ((n = fs.Read(chunk, 0, chunk.Length)) > 0)
            {
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (chunk[i] != (byte)'\n') continue;
                    ReadOnlyMemory<byte> line;
                    if (pending.Length > 0)
                    {
                        pending.Write(chunk, start, i - start);
                        line = pending.ToArray();
                        pending.SetLength(0);
                    }
                    else
                    {
                        line = new ReadOnlyMemory<byte>(chunk, start, i - start);
                    }
                    consumed += line.Length + 1;
                    start = i + 1;

                    var ts = TimestampOf(line);
                    if (ts.HasValue && (latest == null || ts > latest)) latest = ts;
                }
                pending.Write(chunk, start, n - start);
            }

            ScanCache[path] = (consumed, latest);
            return latest;
        }
        catch
        {
            return null; // locked/deleted mid-read — never guess
        }
    }

    private static readonly byte[] TimestampKey = "\"timestamp\""u8.ToArray();

    /// <summary>A conversation record's top-level "timestamp", or null. Lines without the
    /// key anywhere (most metadata records) are skipped before paying for a JSON parse.
    ///
    /// Only "user" and "assistant" records count as activity. BUG found live, 2026-11-08
    /// (a pending question showed orange for 17 minutes, so the developer missed it):
    /// a background subagent finished 30 s after Claude asked a question, and Claude Code
    /// wrote a timestamped "queue-operation" record for its notification. The stale-question
    /// check read that as "answered, working again". Hook-error "attachment" records and
    /// "system" summaries are written the same way, by events rather than by the
    /// conversation moving on. Answering a question or approving a permission writes a
    /// "user" tool_result record, which still counts.</summary>
    private static DateTime? TimestampOf(ReadOnlyMemory<byte> line)
    {
        if (line.Length < 2 || line.Span.IndexOf(TimestampKey) < 0) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String
                || typeEl.GetString() is not ("user" or "assistant")) return null;
            if (!doc.RootElement.TryGetProperty("timestamp", out var tsEl)) return null;
            return tsEl.TryGetDateTime(out var dt) ? dt.ToUniversalTime() : null;
        }
        catch
        {
            return null; // one malformed line — skip it, keep scanning the rest
        }
    }

    private static string Encode(string cwd)
    {
        var chars = new char[cwd.Length];
        for (var i = 0; i < cwd.Length; i++)
        {
            chars[i] = char.IsLetterOrDigit(cwd[i]) ? cwd[i] : '-';
        }
        return new string(chars);
    }
}
