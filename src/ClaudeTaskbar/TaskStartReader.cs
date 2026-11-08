using System.Text.Json;

namespace ClaudeTaskbar;

/// <summary>
/// Reads an optional per-session start-time file (&lt;session_id&gt;.json in the folder named
/// by Dir), written by a separate tool if present, not by this project. Used read-only for
/// the hover-detail "how long has this session been running" figure. Format (verified 2026-09-27):
/// {"sessionId":"...","startedAt":&lt;epoch ms&gt;}. Best-effort: when the file is absent or
/// unreadable, which is the normal case without that tool, no duration is shown.
/// </summary>
public static class TaskStartReader
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "hooks", "claude-notifier-task-start");

    public static TimeSpan? GetRunningDuration(string sessionId)
    {
        var file = Path.Combine(Dir, $"{sessionId}.json");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var startedAtMs = doc.RootElement.GetProperty("startedAt").GetInt64();
            var startedAt = DateTimeOffset.FromUnixTimeMilliseconds(startedAtMs);
            var elapsed = DateTimeOffset.UtcNow - startedAt;
            return elapsed > TimeSpan.Zero ? elapsed : null;
        }
        catch
        {
            return null;
        }
    }
}
