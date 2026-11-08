namespace ClaudeTaskbar;

/// <summary>
/// Index of VS Code's own per-workspace storage folders
/// (`%APPDATA%\Code\User\workspaceStorage\&lt;hash&gt;\workspace.json` → the folder that
/// workspace opened), used to map an open VS Code window to its real workspace root and
/// its `state.vscdb` (see VsCodeEditorStateReader).
///
/// Matching is by EXACT decoded folder path, never substring. Found live, 2026-09-28: a
/// long-used machine has many stale entries whose paths contain a live workspace's path as
/// a substring or share its leaf name — e.g. `C:\old\MyProject` (an old location)
/// alongside the real `C:\src\MyProject`, and `...\MyProject\templates` under the live
/// `...\MyProject`. Substring matching picked the wrong one depending on directory
/// enumeration order.
/// </summary>
public static class VsCodeWorkspaces
{
    public sealed record Workspace(string FolderPath, string HashDir)
    {
        public string LeafName => Path.GetFileName(FolderPath.TrimEnd('\\', '/'));
        public DateTime DbLastWriteUtc
        {
            get
            {
                try { return File.GetLastWriteTimeUtc(Path.Combine(HashDir, "state.vscdb")); }
                catch { return DateTime.MinValue; }
            }
        }
    }

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User", "workspaceStorage");

    private static readonly TimeSpan IndexTtl = TimeSpan.FromSeconds(30);
    private static List<Workspace> _index = new();
    private static DateTime _indexedAtUtc = DateTime.MinValue;

    public static IReadOnlyList<Workspace> All()
    {
        if (DateTime.UtcNow - _indexedAtUtc < IndexTtl) return _index;

        var result = new List<Workspace>();
        try
        {
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var folder = ReadFolder(Path.Combine(dir, "workspace.json"));
                if (folder != null) result.Add(new Workspace(folder, dir));
            }
        }
        catch
        {
            // storage root missing/unreadable — empty index, callers fall back
        }

        _index = result;
        _indexedAtUtc = DateTime.UtcNow;
        return _index;
    }

    /// <summary>The workspace whose folder is exactly <paramref name="folderPath"/>. If
    /// several storage entries point at the same folder, the one VS Code wrote most
    /// recently wins.</summary>
    public static Workspace? ForFolder(string folderPath)
    {
        var target = Normalize(folderPath);
        return All()
            .Where(w => string.Equals(Normalize(w.FolderPath), target, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(w => w.DbLastWriteUtc)
            .FirstOrDefault();
    }

    /// <summary>The workspace an open VS Code window is showing, from its title (VS Code
    /// titles contain the workspace folder's leaf name as one " - "-separated segment — see
    /// VsCodeWindowFinder). Several entries can share a leaf name (stale moved/old
    /// projects); the one whose state.vscdb VS Code wrote most recently is the live one.</summary>
    public static Workspace? ForWindowTitle(string title)
    {
        // VS Code's default title is `activeEditor - rootName [- profile] - Visual Studio
        // Code`, and the active editor of a Claude tab is the chat's own name, which can be
        // anything — including the leaf name of some other (stale) workspace. So segments
        // are tried from the END: the first one, walking backwards from just before the
        // app name, that names any workspace is the root. Matching any segment and taking
        // the newest db could let a tab title beat the real workspace.
        var segments = title.Split(" - ", StringSplitOptions.TrimEntries);
        var all = All();
        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var match = all
                .Where(w => string.Equals(segments[i], w.LeafName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => w.DbLastWriteUtc)
                .FirstOrDefault();
            if (match != null) return match;
        }
        return null;
    }

    public static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');

    /// <summary>workspace.json's "folder" is a file URI, e.g.
    /// "file:///c%3A/src/Foo%20Bar" → "c:\src\Foo Bar". Multi-root ".code-workspace"
    /// entries (a "workspace" key instead of "folder") are skipped — they have no single
    /// root to map to.</summary>
    private static string? ReadFolder(string workspaceJsonPath)
    {
        try
        {
            if (!File.Exists(workspaceJsonPath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(workspaceJsonPath));
            if (!doc.RootElement.TryGetProperty("folder", out var f)) return null;
            var uri = f.GetString();
            const string prefix = "file:///";
            if (uri == null || !uri.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            return Normalize(Uri.UnescapeDataString(uri[prefix.Length..]));
        }
        catch
        {
            return null;
        }
    }
}
