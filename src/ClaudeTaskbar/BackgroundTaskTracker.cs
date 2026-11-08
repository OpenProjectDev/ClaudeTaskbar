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
            // The tasks folder uses the same encoded-project name as the transcript's folder.
            var project = Path.GetFileName(Path.GetDirectoryName(transcriptPath));
            if (string.IsNullOrEmpty(project)) return 0;
            var dir = Path.Combine(Path.GetTempPath(), "claude", project, sessionId, "tasks");
            if (!Directory.Exists(dir)) return 0;

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
