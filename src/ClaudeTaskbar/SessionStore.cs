using System.Text.Json;
using ClaudeTaskbar.Models;

namespace ClaudeTaskbar;

/// <summary>
/// Builds the current snapshot of Claude Code sessions from two sources:
///
/// 1. Hook state files — %LOCALAPPDATA%\ClaudeTaskbar\sessions\&lt;session_id&gt;.json,
///    written by claude-taskbar-hook.ps1. The source of truth for a session's STATE
///    (working / idle / question / error), since only hooks know those transitions.
/// 2. VS Code's own tab list per open window (VsCodeEditorStateReader) — the source of
///    truth for which sessions EXIST. Hooks alone can't answer that: a session that hasn't
///    fired a hook since its state file was lost (pruned, or never created) is otherwise
///    invisible, even while it's actively working.
///
/// History, 2026-09-28 (the app was showing very wrong data): existence used to come from
/// a list of active sessions written by a separate VS Code extension. When that extension
/// was uninstalled, the list went permanently empty, and PruneStale — which used it to
/// decide whether a session was still open — started deleting every session idle 5+
/// minutes, including ones actively working mid-turn (a long turn fires no hooks between
/// UserPromptSubmit and Stop). With no seed left to recreate them, whole desktops went grey
/// while their sessions were running. That extension dependency is gone entirely now; see
/// ReadAllWithSeeding.
///
/// Re-reads the whole directory on any change rather than tracking individual file events
/// — the file count is always small, and this avoids races with the hook script's own
/// temp-file-then-move write pattern.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private readonly string _dir;
    private readonly FileSystemWatcher _watcher;

    // How recently a transcript must have been written for a session with no hook state to
    // be shown as Working rather than Idle.
    private static readonly TimeSpan WorkingIfTranscriptWithin = TimeSpan.FromSeconds(30);

    // Grace period before a session whose window can't be found and whose transcript has
    // gone quiet is treated as genuinely closed — covers a transient window-lookup miss.
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    // When no real last-activity time can be found at all, show the session as long-idle
    // (Blue) rather than inventing "just now" (Green). There is deliberately no in-memory
    // "first seen idle" fallback: missing data should look missing, so real issues show.
    private static readonly TimeSpan UnknownIdleSentinelAge = TimeSpan.FromDays(1);

    public event Action? Changed;

    public SessionStore()
    {
        _dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeTaskbar", "sessions");
        Directory.CreateDirectory(_dir);

        _watcher = new FileSystemWatcher(_dir, "*.json")
        {
            NotifyFilter = NotifyFilters.CreationTime | NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents = true,
        };
        _watcher.Created += (_, _) => Changed?.Invoke();
        _watcher.Deleted += (_, _) => Changed?.Invoke();
        _watcher.Changed += (_, _) => Changed?.Invoke();
        _watcher.Renamed += (_, _) => Changed?.Invoke();
    }

    public List<SessionState> ReadAll()
    {
        var result = new List<SessionState>();
        string[] files;
        try { files = Directory.GetFiles(_dir, "*.json"); }
        catch { return result; }

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var sessionId = root.GetProperty("sessionId").GetString();
                var cwd = root.TryGetProperty("cwd", out var cwdEl) ? cwdEl.GetString() : null;
                var state = root.GetProperty("state").GetString();
                var updatedAt = root.TryGetProperty("updatedAt", out var updEl) && updEl.TryGetDateTime(out var dt)
                    ? dt.ToUniversalTime()
                    : DateTime.UtcNow - UnknownIdleSentinelAge; // missing → looks old, never "just now"
                var activeSubagents = root.TryGetProperty("activeSubagents", out var subEl) && subEl.TryGetInt32(out var n)
                    ? n
                    : 0;
                var transcriptPath = root.TryGetProperty("transcriptPath", out var tpEl) ? tpEl.GetString() : null;

                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(state)) continue;

                result.Add(new SessionState
                {
                    SessionId = sessionId,
                    Cwd = cwd ?? "",
                    State = state,
                    UpdatedAtUtc = updatedAt,
                    ActiveSubagents = activeSubagents,
                    TranscriptPath = transcriptPath,
                });
            }
            catch
            {
                // Partially-written or otherwise unreadable file — skip it this pass,
                // it'll be picked up correctly once stable. Never guess its content.
            }
        }
        return result;
    }

    /// <summary>For --dump: the open windows as this class resolves them.</summary>
    public static string DescribeOpenWindows()
    {
        var sb = new System.Text.StringBuilder();
        foreach (var w in FindOpenWindows())
        {
            sb.AppendLine($"  hwnd={w.Hwnd} folder={w.Workspace.FolderPath} hash={Path.GetFileName(w.Workspace.HashDir)} dbWrite={w.Workspace.DbLastWriteUtc:O}");
            sb.AppendLine(w.Tabs == null
                ? "    tabs=NULL"
                : $"    tabs=[{string.Join(", ", w.Tabs.OrderedSessionIds)}] focused={w.Tabs.FocusedSessionId}");
        }
        return sb.ToString();
    }

    private sealed record OpenWindow(IntPtr Hwnd, VsCodeWorkspaces.Workspace Workspace, VsCodeEditorStateReader.TabInfo? Tabs);

    /// <summary>
    /// Every session worth showing: hook-state sessions (with their workspace root resolved
    /// and stale states corrected), plus any session VS Code itself lists as an open tab
    /// that has no hook state file (shown from its transcript alone).
    /// </summary>
    public List<SessionState> ReadAllWithSeeding()
    {
        var open = FindOpenWindows();
        var sessions = ReadAll();

        foreach (var s in sessions)
        {
            ResolveWorkspaceRoot(s, open);
            CorrectStaleQuestion(s);
            CorrectResumedStart(s);
            ApplyRunningSubagents(s);
            ApplySeen(s);
        }

        sessions = PruneStale(sessions, open);

        // Sessions VS Code has open as tabs but that have no hook state file.
        var known = new HashSet<string>(sessions.Select(s => s.SessionId), StringComparer.OrdinalIgnoreCase);
        foreach (var w in open)
        {
            if (w.Tabs == null) continue;
            foreach (var sessionId in w.Tabs.OrderedSessionIds)
            {
                if (!known.Add(sessionId)) continue;
                var s = FromTranscriptOnly(sessionId, w.Workspace.FolderPath);
                ApplyRunningSubagents(s);
                ApplySeen(s);
                s.WindowHandle = w.Hwnd;
                s.IsOpenAsTab = true;
                if (w.Tabs.TitlesBySessionId.TryGetValue(sessionId, out var title)) s.TabTitle = title;
                sessions.Add(s);
            }
        }

        // BUG found live, 2026-09-30 (a desktop whose VS Code window had been closed still
        // showed a green circle for that project): closing a
        // VS Code window doesn't reliably fire SessionEnd, so the session's hook file lingers
        // until PruneStale's 5-minute window. Meanwhile TaskbarWindow's sticky desktop cache
        // kept placing it on its old desktop, and it even doubled up into a capsule when
        // another window was opened there. A session is only shown while an open VS Code
        // window is resolved for it. The file itself is still left to PruneStale.
        //
        // BUG found live, 2026-10-17 (a desktop with 2 tabs showed 5 circles): Claude Code
        // extension 2.1.301 (updated 2026-10-16) starts extra background sessions when a
        // window opens. One window got three within a minute, each firing SessionStart, never
        // prompted, with no transcript and not a tab. They resolved to that open window by
        // folder and showed as capsule members. A session VS Code
        // doesn't list as a tab is now shown only if it has a transcript, i.e. something was
        // said in it. A brand-new real tab appears once VS Code saves its tab list (~60s).
        return sessions.Where(s => s.WindowHandle.HasValue && (s.IsOpenAsTab || (HasTranscript(s) && !ClosedAsTab(s, open)))).ToList();
    }

    /// <summary>Grace a session that isn't (yet) in its window's saved tab list gets, for a
    /// new tab VS Code hasn't saved yet.</summary>
    private static readonly TimeSpan NewTabGrace = TimeSpan.FromMinutes(2);

    /// <summary>True when the session's window has saved its tab list since the session's
    /// last sign of life, and the session isn't in it: VS Code itself recorded that the tab
    /// is gone.
    ///
    /// BUG found live, 2026-11-09 (a closed tab's circle stayed in the OpenDisk capsule,
    /// "Idle for a while", for hours): closing a Claude tab fires no SessionEnd, and a
    /// session with a transcript was shown while its window was open, to cover a new tab
    /// before VS Code saves its tab list. Here the session's last sign was its resume at
    /// 05:19 UTC and VS Code saved the window's tabs at 06:09 without it. Last sign of life
    /// is the latest of the hook state's time, the transcript's conversation activity and
    /// the transcript file's own write time; the tab list's save time is state.vscdb's.
    /// VS Code only rewrites that file when editor state changes, so a session active after
    /// the last save keeps showing, and a brand-new one gets <see cref="NewTabGrace"/>.</summary>
    private static bool ClosedAsTab(SessionState s, List<OpenWindow> open)
    {
        var window = open.FirstOrDefault(w => w.Hwnd == s.WindowHandle);
        if (window?.Tabs == null) return false; // no tab list to go by: show it
        var lastSign = s.UpdatedAtUtc;
        if (LastActivityReader.GetLastActivityUtcForTranscript(s.TranscriptPath) is { } act && act > lastSign) lastSign = act;
        try
        {
            var written = File.GetLastWriteTimeUtc(s.TranscriptPath!);
            if (written > lastSign) lastSign = written;
        }
        catch { /* gone: the other two signs stand */ }
        if (DateTime.UtcNow - lastSign < NewTabGrace) return false;
        return window.Workspace.DbLastWriteUtc > lastSign;
    }

    /// <summary>Every visible VS Code window mapped to its real workspace (exact folder,
    /// newest storage entry — see VsCodeWorkspaces) and that workspace's tab list.</summary>
    private static List<OpenWindow> FindOpenWindows()
    {
        var result = new List<OpenWindow>();
        foreach (var (hwnd, title) in VsCodeWindowFinder.FindAllWindows())
        {
            var ws = VsCodeWorkspaces.ForWindowTitle(title);
            if (ws == null || result.Any(r => r.Workspace.HashDir == ws.HashDir)) continue;
            result.Add(new OpenWindow(hwnd, ws, VsCodeEditorStateReader.TryGetTabInfo(ws)));
        }
        return result;
    }

    /// <summary>A hook reports whatever cwd the session's shell last cd'd into (a session
    /// opened on C:\src\MyProject can report `C:\src\MyProject\src\App`), not the
    /// workspace root VS Code opened — so window/title matching on the raw cwd fails. Resolve
    /// the real root: first by which open window lists this session as a tab, else by the
    /// window whose folder its transcript belongs to, else (no transcript yet) by the deepest
    /// open workspace containing the cwd.
    ///
    /// BUG found live, 2026-11-08 (a ghost circle that opened nothing when clicked): a
    /// project's window was closed without SessionEnd firing, and its session, cwd under
    /// C:\src\ProjectA, matched another open window on the parent folder C:\src by cwd, since
    /// that folder contains every project. So it showed as a member of that window's capsule,
    /// and PruneStale kept it forever because "its" window was open. Claude Code keeps a
    /// transcript in the projects folder named after the folder the session was started in,
    /// whatever the shell later cd's into (cwd C:\src\MyProject\src\App, transcript under
    /// C--src-MyProject), so that folder names the one window it can belong to.</summary>
    private static void ResolveWorkspaceRoot(SessionState s, List<OpenWindow> open)
    {
        var byTab = open.FirstOrDefault(w => w.Tabs?.OrderedSessionIds.Contains(s.SessionId, StringComparer.OrdinalIgnoreCase) == true);
        var window = byTab ?? (HasTranscript(s)
            ? open.FirstOrDefault(w => SamePath(LastActivityReader.TranscriptPathFor(w.Workspace.FolderPath, s.SessionId), s.TranscriptPath!))
            : open
                .Where(w => IsUnderOrEqual(s.Cwd, w.Workspace.FolderPath))
                .OrderByDescending(w => w.Workspace.FolderPath.Length)
                .FirstOrDefault());
        s.IsOpenAsTab = byTab != null;
        if (byTab?.Tabs?.TitlesBySessionId.TryGetValue(s.SessionId, out var title) == true) s.TabTitle = title;
        if (window == null) return;
        s.CanonicalCwd = window.Workspace.FolderPath;
        s.WindowHandle = window.Hwnd;
    }

    /// <summary>The question hook fires when Claude asks something, but answering it (or
    /// approving a permission) fires no hook — the session just carries on working. If the
    /// transcript has real activity after the question was recorded, it's working again.</summary>
    private static void CorrectStaleQuestion(SessionState s)
    {
        if (s.State != "question") return;
        var last = LastActivityReader.GetLastActivityUtcForTranscript(s.TranscriptPath);
        if (last.HasValue && last.Value > s.UpdatedAtUtc.AddSeconds(3)) s.State = "working";
    }

    /// <summary>BUG found live, 2026-09-28 (after a restart and reopening VS Code every
    /// circle was green again, then turned blue one by one): reopening VS Code resumes every
    /// restored session, which fires SessionStart. The hook records that as "start" with the
    /// current time, so every session looked freshly idle (Green) and turned Blue 3 minutes
    /// after its own resume. The time was real, but it marked the resume, not the user's
    /// last work. Resuming writes no timestamped transcript records (checked live: a
    /// session resumed at 09:25 had its last transcript activity at 09:03). So for a "start"
    /// session with any transcript activity, idle time counts from that activity. A
    /// brand-new session has none and keeps its start time.</summary>
    private static void CorrectResumedStart(SessionState s)
    {
        if (s.State != "start") return;
        var last = LastActivityReader.GetLastActivityUtcForTranscript(s.TranscriptPath);
        if (last.HasValue && last.Value < s.UpdatedAtUtc) s.UpdatedAtUtc = last.Value;
    }

    /// <summary>2026-09-29: a subagent still working means the work is not done yet. A
    /// session whose turn ended but that still has running background subagents is
    /// working: colour, label and the finished sound all wait for them (see
    /// SubagentTracker). Question and error states win, because they need the user either
    /// way.</summary>
    private static void ApplyRunningSubagents(SessionState s)
    {
        s.RunningSubagents = SubagentTracker.CountRunning(s.TranscriptPath, s.SessionId);
        // 2026-10-19: background shell commands count the same way: the turn ended, but
        // Claude is waiting on them and wakes up when they finish. See BackgroundTaskTracker.
        s.RunningBackgroundCommands = BackgroundTaskTracker.CountRunning(s.TranscriptPath, s.SessionId);
        // 2026-11-01: running subagents give orange; with no agent or subagent running but a
        // background command still going, yellow. A running subagent is an agent at work, so
        // working. Only background commands alone give "background".
        if (s.State is not ("idle" or "start")) return;
        if (s.RunningSubagents > 0) s.State = "working";
        else if (s.RunningBackgroundCommands > 0) s.State = "background";
    }

    /// <summary>A double-click "seen" mark (SeenSessions) lasts only while the session stays
    /// finished. The moment it's working again (including via running subagents), errored or
    /// asking a question, the mark is dropped for good: meeting the conditions to be red or
    /// orange reverses it. Question/purple was added so greying
    /// can't hide a waiting question.</summary>
    private static void ApplySeen(SessionState s)
    {
        if (!SeenSessions.Contains(s.SessionId)) return;
        if (s.State is "working" or "error" or "question")
        {
            SeenSessions.Set(s.SessionId, false);
            return;
        }
        s.IsSeen = true;
    }

    /// <summary>A session VS Code shows as an open tab but with no hook state file: Working
    /// if its transcript was written in the last few seconds, otherwise Idle since its
    /// transcript's real last activity (or long-idle if even that can't be found).</summary>
    private static SessionState FromTranscriptOnly(string sessionId, string workspaceRoot)
    {
        var transcript = LastActivityReader.TranscriptPathFor(workspaceRoot, sessionId);
        var last = LastActivityReader.GetLastActivityUtcForTranscript(transcript);
        var working = last.HasValue && DateTime.UtcNow - last.Value < WorkingIfTranscriptWithin;
        return new SessionState
        {
            SessionId = sessionId,
            Cwd = workspaceRoot,
            CanonicalCwd = workspaceRoot,
            State = working ? "working" : "idle",
            UpdatedAtUtc = last ?? DateTime.UtcNow - UnknownIdleSentinelAge,
            TranscriptPath = File.Exists(transcript) ? transcript : null,
        };
    }

    /// <summary>
    /// Deletes the state file for a session that looks genuinely gone: not listed as an open
    /// tab, no open window for its workspace, transcript quiet, and its own state not
    /// updated, all for StaleAfter. Normal closes are handled by the SessionEnd hook; this is
    /// only the fallback for when that doesn't fire. Every liveness signal must agree before
    /// deleting — a wrongly deleted file is exactly the bug this class's history describes.
    /// </summary>
    private List<SessionState> PruneStale(List<SessionState> sessions, List<OpenWindow> open)
    {
        var kept = new List<SessionState>(sessions.Count);
        foreach (var s in sessions)
        {
            var listedAsTab = open.Any(w => w.Tabs?.OrderedSessionIds.Contains(s.SessionId, StringComparer.OrdinalIgnoreCase) == true);
            var windowOpen = s.CanonicalCwd != null
                && open.Any(w => string.Equals(w.Workspace.FolderPath, s.CanonicalCwd, StringComparison.OrdinalIgnoreCase));
            var last = LastActivityReader.GetLastActivityUtcForTranscript(s.TranscriptPath);
            var transcriptRecent = last.HasValue && DateTime.UtcNow - last.Value < StaleAfter;
            var stateRecent = DateTime.UtcNow - s.UpdatedAtUtc < StaleAfter;

            // Never-used session (see the 2.1.301 note in ReadAllWithSeeding): not a tab, no
            // transcript, and quiet for StaleAfter. Its window being open is irrelevant.
            var neverUsed = !listedAsTab && !HasTranscript(s) && !stateRecent;
            if (neverUsed || (!listedAsTab && !windowOpen && !transcriptRecent && !stateRecent))
            {
                try { File.Delete(Path.Combine(_dir, $"{s.SessionId}.json")); } catch { /* best effort */ }
                continue;
            }
            kept.Add(s);
        }
        return kept;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static bool HasTranscript(SessionState s) =>
        !string.IsNullOrEmpty(s.TranscriptPath) && File.Exists(s.TranscriptPath);

    /// <summary>True if childCwd is workspaceCwd itself, or a subdirectory of it.
    /// Case-insensitive (Windows paths), trailing-slash-insensitive.</summary>
    private static bool IsUnderOrEqual(string childCwd, string workspaceCwd)
    {
        if (string.IsNullOrEmpty(childCwd) || string.IsNullOrEmpty(workspaceCwd)) return false;
        var child = childCwd.TrimEnd('\\', '/');
        var parent = workspaceCwd.TrimEnd('\\', '/');
        if (string.Equals(child, parent, StringComparison.OrdinalIgnoreCase)) return true;
        return child.StartsWith(parent + "\\", StringComparison.OrdinalIgnoreCase)
            || child.StartsWith(parent + "/", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _watcher.Dispose();
}
