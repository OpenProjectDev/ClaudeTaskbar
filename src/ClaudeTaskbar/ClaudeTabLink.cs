using System.Diagnostics;

namespace ClaudeTaskbar;

/// <summary>
/// Focuses one specific Claude Code tab, via Anthropic's documented deep link
/// `vscode://anthropic.claude-code/open?session=&lt;id&gt;` (code.claude.com/docs/en/vs-code,
/// "Launch a VS Code tab from other tools"). Researched 2026-09-28 against the installed
/// extension (2.1.283):
/// - If the session's tab is open in the receiving window, the extension reveals it rather
///   than opening a copy.
/// - VS Code delivers the link to its most recently focused window, so the caller must have
///   focused the session's own window first, and only calls this if that succeeded.
///   (Or pass windowId, below, which routes it regardless of focus.)
/// - Never send `prompt=`: for an already-open session the extension rejects it with a
///   message.
/// - The first use shows VS Code's "Allow 'Claude Code' extension to open this URI?" prompt.
///   Ticking "Do not ask again" there once stops it.
/// </summary>
public static class ClaudeTabLink
{
    /// <param name="windowId">VS Code's internal window number (VsCodeWindowIds). When given,
    /// VS Code's URL router delivers the link to exactly that window, whatever has focus.</param>
    public static void Open(string sessionId, int? windowId = null)
    {
        // The extension rejects ids containing / \ .. or NUL. Ours are always UUIDs, so accept
        // nothing else rather than build a URI from unexpected input.
        if (!Guid.TryParse(sessionId, out var id)) return;
        try
        {
            Process.Start(new ProcessStartInfo($"vscode://anthropic.claude-code/open?session={id:D}" + (windowId is { } w ? $"&windowId={w}" : "")) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            DiagLog.Write($"tab link: failed to open session {id:D}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
