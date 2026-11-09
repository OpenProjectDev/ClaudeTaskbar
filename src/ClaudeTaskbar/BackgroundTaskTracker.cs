namespace ClaudeTaskbar;

/// <summary>
/// Counts a session's background shell commands that are still running.
///
/// BUG found live, 2026-10-19 (a false green: no agent or subagent was running, but the
/// session was still waiting on something and later sprang back to working). In one
/// project, Claude started a background Bash command (a loop waiting for a deploy) and
/// ended its turn ("I'll confirm once it's live"), so the Stop hook set it idle. When the
/// command finished, its task-notification started a new turn and the circle went orange
/// again.
///
/// Signal, verified live the same day: a background command writes its output to
/// `%TEMP%\claude\&lt;encoded project&gt;\&lt;sessionId&gt;\tasks\&lt;taskId&gt;.output`, and the file
/// stays open for writing while the command runs. Opening it with FileShare.Read (refusing
/// to share with writers) fails with a sharing violation exactly while it is held. A test
/// background command was "held" while running; the finished deploy loop's file and every
/// other finished file were "free". This uses no timers: the OS reports whether the process
/// still has the file open.
///
/// Subagent task files (ids starting with "a") are left to SubagentTracker. A foreground
/// command's file is also held, but only during a turn that is already "working".
/// Known trade-off: a command that never ends, such as a dev server left running, keeps the
/// session yellow ("background") for as long as it runs. The tooltip says why.
/// </summary>
public static class BackgroundTaskTracker
{
    public static int CountRunning(string? transcriptPath, string sessionId)
    {
        if (string.IsNullOrEmpty(transcriptPath)) return 0;
        try
        {
            var dir = FindTasksDir(sessionId);
            if (dir == null) return 0;

            var running = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*.output"))
            {
                if (Path.GetFileName(file).StartsWith('a')) continue;
                if (IsHeldForWriting(file)) running++;
            }
            return running;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"background tasks: failed for {sessionId[..8]}: {ex.GetType().Name}: {ex.Message}");
            return 0;
        }
    }

    private static readonly Dictionary<string, string> TasksDirs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The session's tasks folder, `%TEMP%\claude\&lt;project folder&gt;\&lt;sessionId&gt;\tasks`,
    /// found by the session id rather than computed. BUG found live, 2026-11-09 (a running
    /// background test showed no yellow): Claude Code renamed the project folder here from
    /// the encoded project path (the transcript folder's name) to a short hash, such as
    /// `b9f9d413f6befb30`, so the computed path no longer existed. Session ids are unique, so
    /// searching the few project folders finds it under either layout. A found folder is
    /// remembered; a missing one is searched again next time (it appears with the session's
    /// first background command).</summary>
    private static string? FindTasksDir(string sessionId)
    {
        if (TasksDirs.TryGetValue(sessionId, out var known) && Directory.Exists(known)) return known;
        var root = Path.Combine(Path.GetTempPath(), "claude");
        if (!Directory.Exists(root)) return null;
        foreach (var project in Directory.EnumerateDirectories(root))
        {
            var dir = Path.Combine(project, sessionId, "tasks");
            if (!Directory.Exists(dir)) continue;
            TasksDirs[sessionId] = dir;
            return dir;
        }
        return null;
    }

    private static bool IsHeldForWriting(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return false;
        }
        catch (IOException)
        {
            return true; // sharing violation: a process has it open for writing
        }
        catch
        {
            return false; // gone or inaccessible: not evidence of running work
        }
    }
}
