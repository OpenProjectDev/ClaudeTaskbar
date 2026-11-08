using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeTaskbar;

/// <summary>
/// Installs, checks and removes the Claude Code hooks this app depends on: the hook script
/// (embedded in this exe, copied to &lt;claude dir&gt;\hooks\) and one entry per event in
/// &lt;claude dir&gt;\settings.json. The user's other settings and hooks are never touched: our
/// entries are recognised by the script's file name in their command. Claude Code's file
/// watcher picks up direct edits to hooks in settings files, so no restart is needed.
/// </summary>
public sealed class HookInstaller
{
    public const string ScriptName = "claude-taskbar-hook.ps1";

    /// <summary>Every hook entry the app needs: event, matcher (null = none), state passed
    /// to the script. Why the matchers are what they are: Notification without a matcher also
    /// fires for idle prompts, completions, auth and quota events, all of which turned circles
    /// purple. SessionStart leaves out `compact`, so a compaction mid-turn doesn't flip a
    /// working session to "start". PostToolUseFailure is not used: it fires on routine tool
    /// failures and nothing clears it, so circles stuck red; red comes only from
    /// StopFailure.</summary>
    public static readonly (string Event, string? Matcher, string State)[] Entries =
    {
        ("UserPromptSubmit", null, "working"),
        ("Stop", null, "idle"),
        ("StopFailure", null, "error"),
        ("Notification", "permission_prompt|elicitation_dialog|elicitation_url_dialog|agent_needs_input", "question"),
        ("PreToolUse", "AskUserQuestion", "question"),
        ("PreToolUse", "Task|Agent", "subagent_start"),
        ("SubagentStop", null, "subagent_stop"),
        ("SessionStart", "startup|resume|clear|fork", "start"),
        ("SessionEnd", null, "end"),
        ("PreCompact", "manual", "working"),
        ("PostCompact", "manual", "idle"),
    };

    public enum State { NotInstalled, Partial, Outdated, Installed }

    public sealed record Status(State State, int EntriesPresent, bool ScriptPresent, bool ScriptCurrent, string? Error);

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        // Keep quotes and apostrophes in PowerShell commands readable instead of '.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string ClaudeDir { get; }
    public string SettingsPath => Path.Combine(ClaudeDir, "settings.json");
    public string ScriptPath => Path.Combine(ClaudeDir, "hooks", ScriptName);

    public HookInstaller(string? claudeDir = null)
    {
        ClaudeDir = claudeDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
    }

    public string Command(string state) =>
        $"powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{ScriptPath}\" -State {state}";

    public Status GetStatus()
    {
        JsonObject? settings;
        try { settings = ReadSettings(); }
        catch (Exception ex) { return new Status(State.NotInstalled, 0, File.Exists(ScriptPath), false, $"settings.json can't be read: {ex.Message}"); }

        var present = settings == null ? 0 : Entries.Count(e => FindEntry(settings, e.Event, e.Matcher, e.State));
        var scriptPresent = File.Exists(ScriptPath);
        var scriptCurrent = scriptPresent && Normalize(File.ReadAllText(ScriptPath)) == Normalize(EmbeddedScript());

        var state = present == 0 && !scriptPresent ? State.NotInstalled
            : present < Entries.Length || !scriptPresent ? State.Partial
            : !scriptCurrent ? State.Outdated
            : State.Installed;
        return new Status(state, present, scriptPresent, scriptCurrent, null);
    }

    /// <summary>Writes the script and adds whichever entries are missing. Backs up an existing
    /// settings.json first. Throws, changing nothing, if settings.json isn't plain JSON or its
    /// "hooks" (or one of the events this app uses) has a shape Claude Code wouldn't read as
    /// hooks: replacing it would lose the user's value (found in a pre-release audit).</summary>
    public void Install()
    {
        var settings = ReadSettings() ?? new JsonObject();
        if (settings.ContainsKey("hooks") && settings["hooks"] is not JsonObject)
            throw new InvalidDataException("\"hooks\" in settings.json is not an object; nothing was changed");
        foreach (var evt in Entries.Select(e => e.Event).Distinct())
            if (settings["hooks"] is JsonObject h && h.ContainsKey(evt) && h[evt] is not JsonArray)
                throw new InvalidDataException($"\"hooks.{evt}\" in settings.json is not a list; nothing was changed");

        Directory.CreateDirectory(Path.GetDirectoryName(ScriptPath)!);
        File.WriteAllText(ScriptPath, EmbeddedScript());

        if (settings["hooks"] is not JsonObject hooks)
        {
            hooks = new JsonObject();
            settings["hooks"] = hooks;
        }
        var added = 0;
        foreach (var (evt, matcher, state) in Entries)
        {
            if (FindEntry(settings, evt, matcher, state)) continue;
            added++;
            if (hooks[evt] is not JsonArray groups)
            {
                groups = new JsonArray();
                hooks[evt] = groups;
            }
            var group = new JsonObject();
            if (matcher != null) group["matcher"] = matcher;
            group["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = Command(state) });
            groups.Add(group);
        }
        if (added > 0) WriteSettings(settings); // updating only the script leaves settings.json alone
    }

    /// <summary>Removes every hook whose command runs our script and deletes the script. A
    /// group or event is dropped only when removing ours emptied it: the user's own empty
    /// groups stay. settings.json is backed up and rewritten only when something was
    /// removed.</summary>
    public void Uninstall()
    {
        var settings = ReadSettings();
        var removed = 0;
        if (settings?["hooks"] is JsonObject hooks)
        {
            foreach (var evt in hooks.Select(p => p.Key).ToList())
            {
                if (hooks[evt] is not JsonArray groups) continue;
                var emptied = false;
                foreach (var group in groups.OfType<JsonObject>().ToList())
                {
                    if (group["hooks"] is not JsonArray list) continue;
                    var ours = list.OfType<JsonObject>().Where(IsOurs).ToList();
                    if (ours.Count == 0) continue;
                    foreach (var h in ours) list.Remove(h);
                    removed += ours.Count;
                    if (list.Count == 0)
                    {
                        groups.Remove(group);
                        emptied = true;
                    }
                }
                if (emptied && groups.Count == 0) hooks.Remove(evt);
            }
            if (removed > 0) WriteSettings(settings);
        }
        if (File.Exists(ScriptPath)) File.Delete(ScriptPath);
    }

    /// <summary>A node's string value, or null for anything else (a number, an object):
    /// settings.json is the user's file, and an odd value must not crash the app.</summary>
    private static string? Str(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool IsOurs(JsonObject hook) =>
        Str(hook["command"]) is { } cmd && cmd.Contains(ScriptName, StringComparison.OrdinalIgnoreCase);

    private static bool FindEntry(JsonObject settings, string evt, string? matcher, string state)
    {
        if (settings["hooks"] is not JsonObject hooks || hooks[evt] is not JsonArray groups) return false;
        foreach (var group in groups.OfType<JsonObject>())
        {
            var m = Str(group["matcher"]);
            if ((string.IsNullOrEmpty(m) ? null : m) != matcher) continue;
            if (group["hooks"] is not JsonArray list) continue;
            if (list.OfType<JsonObject>().Any(h => IsOurs(h)
                && Str(h["command"])!.EndsWith($"-State {state}", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>The parsed settings file, or null if it doesn't exist. Plain JSON only: a
    /// file that doesn't parse, or has comments or trailing commas, throws, so it is never
    /// rewritten (rewriting would drop the comments).</summary>
    private JsonObject? ReadSettings()
    {
        if (!File.Exists(SettingsPath)) return null;
        var text = File.ReadAllText(SettingsPath);
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        return JsonNode.Parse(text) as JsonObject
            ?? throw new InvalidDataException("settings.json is not a JSON object");
    }

    /// <summary>Backs the current file up under a name no earlier backup has (to the
    /// millisecond, never overwriting one), then replaces it through a temporary file.</summary>
    private void WriteSettings(JsonObject settings)
    {
        Directory.CreateDirectory(ClaudeDir);
        if (File.Exists(SettingsPath))
            File.Copy(SettingsPath, $"{SettingsPath}.bak-claude-taskbar-{DateTime.Now:yyyyMMdd-HHmmss-fff}", overwrite: false);
        var tmp = SettingsPath + ".tmp";
        File.WriteAllText(tmp, settings.ToJsonString(WriteOptions));
        File.Move(tmp, SettingsPath, overwrite: true);
    }

    public static string EmbeddedScript()
    {
        using var stream = typeof(HookInstaller).Assembly.GetManifestResourceStream(ScriptName)
            ?? throw new InvalidOperationException($"{ScriptName} is not embedded in this build");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Normalize(string s) => s.TrimStart('﻿').Replace("\r\n", "\n").TrimEnd();
}
