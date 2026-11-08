namespace ClaudeTaskbar.Models;

/// <summary>
/// One circle's worth of state. Mirrors the JSON written by
/// %USERPROFILE%\.claude\hooks\claude-taskbar-hook.ps1 to
/// %LOCALAPPDATA%\ClaudeTaskbar\sessions\&lt;session_id&gt;.json (the hook -> file -> app
/// contract: the hook writes one JSON file per session via temp file + move, and this app
/// watches the folder; field list in the hook script's header).
/// </summary>
public sealed class SessionState
{
    public required string SessionId { get; init; }
    public required string Cwd { get; init; }
    // start | working | idle | question | error, plus "background", set only by SessionStore
    // (no agent or subagent is running, but background commands are). Settable: SessionStore corrects a stale
    // "question" to "working" when the transcript shows the session moved on after the
    // question hook fired (answering a question/approving a permission fires no hook).
    public required string State { get; set; }
    public DateTime UpdatedAtUtc { get; set; } // settable: SessionStore replaces a resumed session's "start" time with its real last activity
    public int ActiveSubagents { get; init; }
    public string? TranscriptPath { get; init; } // real per-session token usage — see TranscriptTokenReader

    // Set post-construction by SessionStore.ReadAllWithSeeding to the folder of the open
    // VS Code window this session was resolved to (the canonical workspace root) — display
    // should use the workspace root, not whatever subdirectory a shell command happened to
    // cd into. Null when no open window was matched (falls back to raw Cwd in ProjectName
    // below).
    public string? CanonicalCwd { get; set; }

    // The VS Code window SessionStore resolved this session to (by tab list, else by
    // workspace folder). Null if no open window was matched; callers then fall back to
    // title matching on ProjectName.
    public IntPtr? WindowHandle { get; set; }

    // True if VS Code's own saved tab list shows this session as an editor tab (see
    // VsCodeEditorStateReader). Clicking a circle only deep-links to the tab when this is set
    // — see ClaudeTabLink.
    public bool IsOpenAsTab { get; set; }

    // The Claude tab's own title as VS Code shows it (the chat's name), from the same saved
    // tab list. Null if the session isn't a tab or VS Code hasn't saved a title yet.
    public string? TabTitle { get; set; }

    // Subagents still running, from Claude Code's own subagent transcripts (see
    // SubagentTracker). While above 0, an otherwise idle session counts as working.
    public int RunningSubagents { get; set; }

    // Background shell commands still running (see BackgroundTaskTracker). With no subagent
    // running, they make an otherwise idle session "background" (yellow).
    public int RunningBackgroundCommands { get; set; }

    // How long an idle/finished session stays Green before shifting to Blue ("done a
    // while ago, presumably not checked yet"). Reliable signal: time since the state last
    // changed (UpdatedAtUtc) — there's no real "has the user looked at this" signal
    // available, so this is the closest honest proxy, not literal read-tracking.
    private static readonly TimeSpan IdleStaleAfter = TimeSpan.FromMinutes(10); // was 3; raised to 10 on 2026-09-29 after use

    /// <summary>
    /// Color mapping (revised after live feedback): grey (no
    /// session) is the caller's responsibility when the session set is empty, not
    /// represented here. "start" (session exists, no prompt yet) maps to the same visual
    /// as fresh "idle". Blue and Purple were reassigned from the original design: Blue now
    /// means "finished a while ago, not yet checked" (freeing Green for "just finished"),
    /// Purple takes over "has a question" (freeing Blue for the above).
    /// </summary>
    public CircleColor Color => IsSeen && BaseColor is CircleColor.Green or CircleColor.Blue or CircleColor.Yellow ? CircleColor.Grey : BaseColor;

    /// <summary>The colour from state alone, ignoring the "seen" mark.</summary>
    public CircleColor BaseColor => State switch
    {
        "working" => CircleColor.Orange,
        // 2026-11-01: a separate colour for chats that are done but still have background
        // commands running. Running subagents stay Orange; see SessionStore.
        "background" => CircleColor.Yellow,
        "question" => CircleColor.Purple,
        "error" => CircleColor.Red,
        "idle" or "start" => (DateTime.UtcNow - UpdatedAtUtc) > IdleStaleAfter ? CircleColor.Blue : CircleColor.Green,
        _ => CircleColor.Grey, // unknown state string — never invent a color, show neutral
    };

    // The user double-clicked this finished session's circle to mark it seen: it shows grey
    // like an empty desktop until it needs attention again. See SeenSessions.
    public bool IsSeen { get; set; }

    /// <summary>Last path segment of the canonical workspace-root cwd (falls back to the
    /// raw per-event Cwd if no open window set CanonicalCwd) — good enough as a project
    /// label without pulling in a full path-shortening scheme. Using the raw Cwd directly
    /// used to show a build subdirectory's name instead of the actual project name
    /// whenever a shell command had cd'd around; see CanonicalCwd above.</summary>
    public string ProjectName
    {
        get
        {
            var display = string.IsNullOrEmpty(CanonicalCwd) ? Cwd : CanonicalCwd;
            return string.IsNullOrEmpty(display)
                ? "(unknown project)"
                : display.TrimEnd('\\', '/').Split('\\', '/').LastOrDefault(display) ?? display;
        }
    }

    public string StateLabel => State switch
    {
        "working" => "Working",
        "background" => "Background commands running",
        "question" => "Waiting for input",
        "error" => "Errored",
        "start" when BaseColor == CircleColor.Blue => "Idle for a while", // e.g. resumed when VS Code reopened
        "start" => "Started, idle",
        "idle" when BaseColor == CircleColor.Blue => "Idle for a while",
        "idle" => "Idle",
        _ => State,
    };
}

public enum CircleColor { Grey, Orange, Yellow, Green, Blue, Purple, Red }
