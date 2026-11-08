using System.Text.RegularExpressions;

namespace ClaudeTaskbar;

/// <summary>
/// Finds VS Code's own internal window number for a Claude session, so the tab deep link can
/// be routed to that exact window with `windowId=N` instead of to "whichever window VS Code
/// last saw focused".
///
/// Why (2026-10-18): sending the link right after focusing the window raced VS Code's own
/// focus tracking. Across a desktop switch the link reached the previously focused window,
/// which started empty "Claude Code" tabs. A timing fix was rejected: a hardcoded timer can
/// fail, routing by window id is reliable.
///
/// Verified live, 2026-10-18 (VS Code 1.139, Claude Code extension 2.1.301):
/// - VS Code's URL router sends a link whose query has `windowId=N` to window N (see
///   src/vs/platform/url/common/urlIpc.ts). Test: with project A's window focused, opening
///   `…/open?session=&lt;project B's session&gt;&amp;windowId=6` brought project B's window
///   forward on its own tab, and no window started a new session.
/// - Each window logs to `%APPDATA%\Code\logs\&lt;session&gt;\window&lt;N&gt;\`, with the same N.
///   Its Claude extension log (`exthost\Anthropic.claude-code\Claude VSCode.log`) records
///   every session it starts: "Spawning Claude with SDK query function - cwd: &lt;workspace&gt;,
///   … resume: &lt;sessionId&gt;". Each of the four open windows' logs mentioned exactly its own
///   session ids.
/// - Short `Code.exe --open-url` runs (one per click) create their own log folders with no
///   window subfolders. The live VS Code instance's folder is the newest one that has
///   window folders.
///
/// Lookup: the highest N whose log resumed this session id (after a window is closed and the
/// workspace reopened, the newer window wins). Failing that, the highest N whose log spawned
/// Claude in this workspace (a brand-new session that was never resumed). Null if neither is
/// found; the caller then falls back to waiting for focus. Undocumented, internal formats, so
/// every failure returns null.
/// </summary>
public static class VsCodeWindowIds
{
    private static readonly string LogsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "logs");

    public static int? Find(string sessionId, string? workspaceFolder)
    {
        try
        {
            var session = Directory.EnumerateDirectories(LogsRoot)
                .Where(d => Directory.EnumerateDirectories(d, "window*").Any())
                .OrderByDescending(Directory.GetCreationTimeUtc)
                .FirstOrDefault();
            if (session == null) return null;

            var windows = Directory.EnumerateDirectories(session, "window*")
                .Select(d => (Dir: d, N: int.TryParse(Path.GetFileName(d)["window".Length..], out var n) ? n : -1))
                .Where(w => w.N > 0)
                .OrderByDescending(w => w.N)
                .Select(w => (w.N, Log: ReadClaudeLog(w.Dir)))
                .Where(w => w.Log != null)
                .ToList();

            var byResume = windows.FirstOrDefault(w => w.Log!.Contains("resume: " + sessionId, StringComparison.OrdinalIgnoreCase));
            if (byResume.Log != null) return byResume.N;

            if (!string.IsNullOrEmpty(workspaceFolder))
            {
                var target = VsCodeWorkspaces.Normalize(workspaceFolder);
                foreach (var w in windows)
                {
                    foreach (Match m in SpawnCwd.Matches(w.Log!))
                    {
                        if (string.Equals(VsCodeWorkspaces.Normalize(m.Groups[1].Value), target, StringComparison.OrdinalIgnoreCase)) return w.N;
                    }
                }
            }
            return null;
        }
        catch (Exception ex)
        {
            DiagLog.Write($"window id: lookup failed for {sessionId[..8]}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static readonly Regex SpawnCwd = new(@"Spawning Claude with SDK query function - cwd: ([^,\r\n]+),", RegexOptions.Compiled);

    private static string? ReadClaudeLog(string windowDir)
    {
        var path = Path.Combine(windowDir, "exthost", "Anthropic.claude-code", "Claude VSCode.log");
        if (!File.Exists(path)) return null;
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
