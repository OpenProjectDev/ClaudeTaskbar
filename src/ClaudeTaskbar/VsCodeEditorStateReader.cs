using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClaudeTaskbar;

/// <summary>
/// Reads VS Code's own per-workspace `state.vscdb` (SQLite) for the Claude Code sessions
/// open as editor tabs in one VS Code window: which sessions are open, their real
/// left-to-right tab order, and which one is focused. This is now the primary source for
/// "which sessions exist" (see SessionStore) as well as capsule order/focus —
/// `GetForegroundWindow()` can only see the window, and every session sharing one window
/// has the same HWND.
///
/// Found by a research subagent (2026-09-27), verified against VS Code's own source
/// (`editorGroupModel.ts`) and live against real workspaces before use. Undocumented,
/// internal storage format — same risk class as this project's virtual-desktop COM calls:
/// every read wrapped in try/catch, any failure returns null and callers fall back.
///
/// VS Code only flushes this file on idle (~60s) or shutdown, so it can lag the real UI by
/// about a minute — accepted, since tabs are rarely reordered.
/// Sessions opened in the Claude sidebar rather than as an editor tab don't appear here.
/// </summary>
public static class VsCodeEditorStateReader
{
    // TitlesBySessionId: each Claude tab's own title as VS Code shows it (the chat's name,
    // e.g. "Fix login bug") — the editor entry's "title" field, confirmed live 2026-09-28.
    public sealed record TabInfo(List<string> OrderedSessionIds, string? FocusedSessionId, Dictionary<string, string> TitlesBySessionId);

    // hashDir -> (db mtime when read, result). Re-read only when VS Code actually rewrites
    // the file, not every 400ms poll.
    private static readonly Dictionary<string, (DateTime Mtime, TabInfo? Info)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static TabInfo? TryGetTabInfoForFolder(string workspaceFolderPath)
    {
        var ws = VsCodeWorkspaces.ForFolder(workspaceFolderPath);
        return ws == null ? null : TryGetTabInfo(ws);
    }

    public static TabInfo? TryGetTabInfo(VsCodeWorkspaces.Workspace workspace)
    {
        var dbPath = Path.Combine(workspace.HashDir, "state.vscdb");
        DateTime mtime;
        try
        {
            if (!File.Exists(dbPath)) return null;
            mtime = File.GetLastWriteTimeUtc(dbPath);
        }
        catch
        {
            return null;
        }

        if (Cache.TryGetValue(workspace.HashDir, out var cached) && cached.Mtime == mtime) return cached.Info;

        var info = Read(dbPath);
        if (info == null)
        {
            // BUG found live, 2026-09-28 (a window's two Claude tabs still showed as one
            // circle): a failed read used to be cached against the db's mtime like a real
            // result. VS Code rewrites the file only every ~60s or less often, so one
            // transient failure (e.g. db locked mid-write) left that window with no tab list
            // until its next write, and every session known only from the tab list
            // vanished. A fresh --dump pass of the same pipeline showed the capsule
            // correctly while the long-running app didn't. Failures are now never cached:
            // retry on the next poll, and until then keep the last real tab list read from
            // this same file (real data, at most one flush old), not nothing.
            return cached.Info;
        }
        Cache[workspace.HashDir] = (mtime, info);
        return info;
    }

    private static TabInfo? Read(string dbPath)
    {
        try
        {
            // Pooling off: Microsoft.Data.Sqlite pools by default, which would keep a native
            // handle open on VS Code's file between our (rare, mtime-triggered) reads.
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;Pooling=False;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT value FROM ItemTable WHERE key = 'memento/workbench.parts.editor'";
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
            {
                DiagLog.Write($"vscdb: no editor-part row in {dbPath}");
                return null;
            }

            using var doc = JsonDocument.Parse(reader.GetString(0));
            if (!doc.RootElement.TryGetProperty("editorpart.state", out var editorpartState))
            {
                DiagLog.Write($"vscdb: no editorpart.state in {dbPath}");
                return null;
            }

            var activeGroupOrdinal = editorpartState.TryGetProperty("activeGroup", out var ag) ? ag.GetInt32() : -1;
            var grid = editorpartState.GetProperty("serializedGrid");

            var orderedIds = new List<string>();
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? focusedId = null;

            // The grid is a split tree: "branch" nodes hold child nodes in "data", "leaf"
            // nodes hold one editor group in "data". Depth-first walk = visual order across
            // split groups too. Shape confirmed against real raw JSON, not assumed.
            void Walk(JsonElement node)
            {
                var type = node.GetProperty("type").GetString();
                var data = node.GetProperty("data");
                if (type == "leaf")
                {
                    var groupOrdinal = data.TryGetProperty("id", out var gid) ? gid.GetInt32() : -1;
                    if (!data.TryGetProperty("editors", out var editors)) return;

                    // One entry per tab INCLUDING non-Claude tabs (null), because mru holds
                    // indices into this full list — confirmed live: a group of
                    // [main.dart, <Claude tab>, <Claude tab>] had mru [1,2,0].
                    var groupTabs = editors.EnumerateArray().Select(ExtractClaudeTab).ToList();
                    var groupSessionIds = groupTabs.Select(t => t?.SessionId).ToList();
                    orderedIds.AddRange(groupSessionIds.Where(id => id != null)!);
                    foreach (var t in groupTabs)
                    {
                        if (t is { Title: { Length: > 0 } title }) titles[t.Value.SessionId] = title;
                    }

                    // A group's mru[0] is its focused tab; only the overall activeGroup's
                    // focused tab has real UI focus.
                    if (groupOrdinal == activeGroupOrdinal
                        && data.TryGetProperty("mru", out var mru)
                        && mru.GetArrayLength() > 0)
                    {
                        var focusedIndex = mru[0].GetInt32();
                        if (focusedIndex >= 0 && focusedIndex < groupSessionIds.Count)
                        {
                            focusedId = groupSessionIds[focusedIndex];
                        }
                    }
                }
                else if (type == "branch")
                {
                    foreach (var child in data.EnumerateArray()) Walk(child);
                }
            }

            Walk(grid.GetProperty("root"));
            return new TabInfo(orderedIds, focusedId, titles);
        }
        catch (Exception ex)
        {
            // format changed, db locked mid-write, etc. — never guessed, but recorded
            DiagLog.Write($"vscdb: read failed for {dbPath}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>A Claude tab's session id and title, or null for any other tab. Two levels
    /// of stringified JSON: the tab's "value" is a JSON string with "title", and its "state"
    /// field is another JSON string holding "sessionID" — all confirmed live.</summary>
    private static (string SessionId, string? Title)? ExtractClaudeTab(JsonElement editorEntry)
    {
        try
        {
            if (!editorEntry.TryGetProperty("value", out var val) || val.ValueKind != JsonValueKind.String) return null;
            using var valDoc = JsonDocument.Parse(val.GetString()!);
            var v = valDoc.RootElement;
            if (!v.TryGetProperty("providedId", out var pid) || pid.GetString() != "claudeVSCodePanel") return null;
            if (!v.TryGetProperty("state", out var st) || st.ValueKind != JsonValueKind.String) return null;
            using var stDoc = JsonDocument.Parse(st.GetString()!);
            var id = stDoc.RootElement.TryGetProperty("sessionID", out var sid) ? sid.GetString() : null;
            if (id == null) return null;
            var title = v.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            return (id, title);
        }
        catch
        {
            return null;
        }
    }
}
