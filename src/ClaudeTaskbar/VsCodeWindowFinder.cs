using System.Text;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

/// <summary>
/// Maps a session's project name to its actual OS window handle — needed for virtual
/// desktop sort/click-to-switch. The process ids available for a session turned out to
/// be VS Code's internal per-window utility/NodeService helper processes,
/// not the window-owning process (confirmed live: all traced back to one single shared
/// parent Code.exe, which is the process that actually owns every visible VS Code window —
/// multiple workspace windows live in one process). Matching by pid is a dead end.
///
/// Matching by window TITLE works instead: VS Code's title bar reliably includes the
/// workspace folder name (e.g. "... - MyProject - Visual Studio Code"), confirmed live
/// against real windows. Not perfectly unique (two different workspaces could share a
/// folder name) but a reasonable, practical heuristic — same standard as a human visually
/// picking the right window.
///
/// BUG found live, 2026-09-27 (the circle order kept moving around):
/// EnumWindows also enumerates this app's OWN two windows (the circles window and the
/// border mask). Calling GetWindowText/GetClassName on OUR OWN window from OUR OWN process
/// sends a real, synchronous, re-entrant WM_GETTEXT/WM_GETTEXT-adjacent message into our
/// own WndProc while we're already mid-handler (this call happens from inside the
/// WM_TIMER handler) — confirmed live this returns glitchy, garbled results (e.g. a
/// corrupted near-copy of whatever real VS Code title was enumerated just before it, once
/// literally "Xix login bug - MyProject - Visual Studio Code" for a real "Fix login bug -
/// MyProject - Visual Studio Code" — a one-character-corrupted echo of the real title, for
/// a window whose real, stable title is just "Claude Taskbar"). That glitched text
/// intermittently satisfied the exact-segment match for whatever project name was being looked up at that moment, and — since it's
/// not a real, virtual-desktop-registered window — its desktop lookup always failed,
/// visibly yanking that circle to the end of the sort order for one tick. Fixed at the
/// root: skip any window belonging to this app's own process outright, rather than
/// patching around the symptom.
/// </summary>
public static class VsCodeWindowFinder
{
    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>All currently visible top-level windows whose title ends with
    /// "Visual Studio Code" — cheap to call, re-enumerates fresh each time rather than
    /// caching, since windows open/close/rename constantly. Never includes this app's own
    /// windows (see the class-level remarks).</summary>
    public static List<(IntPtr Hwnd, string Title)> FindAllWindows()
    {
        var result = new List<(IntPtr, string)>();
        Win32.EnumWindows((h, _) =>
        {
            Win32.GetWindowThreadProcessId(h, out var winPid);
            if (winPid == OwnProcessId) return true; // never our own overlay windows

            if (!Win32.IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            Win32.GetWindowText(h, sb, 512);
            var title = sb.ToString();
            if (title.EndsWith("Visual Studio Code", StringComparison.Ordinal))
            {
                result.Add((h, title));
            }
            return true; // keep enumerating
        }, IntPtr.Zero);
        return result;
    }

    /// <summary>Best-matching window for a given project name (see
    /// SessionState.ProjectName) — the title segment between " - " markers that equals it
    /// exactly. Null if no real VS Code window title matches at all (project name
    /// unknown/renamed/window closed).</summary>
    public static IntPtr? FindWindowForProject(string projectName)
    {
        if (string.IsNullOrEmpty(projectName)) return null;

        foreach (var (hwnd, title) in FindAllWindows())
        {
            var segments = title.Split(" - ", StringSplitOptions.TrimEntries);
            if (segments.Any(seg => string.Equals(seg, projectName, StringComparison.OrdinalIgnoreCase)))
            {
                return hwnd;
            }
        }

        return null;
    }
}
