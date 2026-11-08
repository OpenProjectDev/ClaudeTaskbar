namespace ClaudeTaskbar;

/// <summary>
/// Append-only diagnostics log at %LOCALAPPDATA%\ClaudeTaskbar\diag.log: every sound played
/// (SessionSoundNotifier), plus failures that
/// would otherwise be swallowed by the try/catch around every undocumented-format read. No
/// masking fallbacks: a failed read must be visible somewhere, not just silently degrade
/// the display. The same message is written at most once a minute so a
/// persistent failure on the 400ms poll can't flood the file, and the file is reset once
/// it passes 1 MB.
/// </summary>
public static class DiagLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeTaskbar", "diag.log");

    private static readonly Dictionary<string, DateTime> LastWritten = new();

    public static void Write(string message)
    {
        try
        {
            if (LastWritten.TryGetValue(message, out var at) && DateTime.UtcNow - at < TimeSpan.FromMinutes(1)) return;
            LastWritten[message] = DateTime.UtcNow;

            var info = new FileInfo(LogPath);
            if (info.Exists && info.Length > 1_000_000) info.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never take the app down
        }
    }
}
