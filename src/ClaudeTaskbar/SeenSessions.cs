using System.Text.Json;

namespace ClaudeTaskbar;

/// <summary>
/// Sessions the user has marked as seen by double-clicking their finished (green, blue or
/// yellow) circle (2026-09-29): the circle turns grey like an empty desktop, and
/// double-clicking it again or it meeting the conditions to be red or orange reverses
/// that. SessionStore clears a mark as soon as the session needs attention again:
/// orange, red, or purple (a question; that one is added so greying can't hide a waiting
/// question).
///
/// Saved to %LOCALAPPDATA%\ClaudeTaskbar\seen.json so marks survive app restarts and redeploys.
/// </summary>
public static class SeenSessions
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClaudeTaskbar", "seen.json");

    private static HashSet<string>? _ids;

    private static HashSet<string> Ids
    {
        get
        {
            if (_ids != null) return _ids;
            try
            {
                _ids = File.Exists(FilePath)
                    ? new HashSet<string>(JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath)) ?? [], StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                DiagLog.Write($"seen: could not read {FilePath}: {ex.GetType().Name}: {ex.Message} — starting with no marks");
                _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            return _ids;
        }
    }

    public static bool Contains(string sessionId) => Ids.Contains(sessionId);

    public static void Set(string sessionId, bool seen)
    {
        if (seen ? !Ids.Add(sessionId) : !Ids.Remove(sessionId)) return; // no change
        try
        {
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Ids.ToArray()));
        }
        catch (Exception ex)
        {
            DiagLog.Write($"seen: could not save {FilePath}: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
