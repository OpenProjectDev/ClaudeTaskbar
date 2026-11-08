using Microsoft.Win32;

namespace ClaudeTaskbar;

/// <summary>"Start with Windows": the current user's Run key, value <see cref="ValueName"/>.</summary>
public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "ClaudeTaskbar";

    /// <summary>The command Windows runs at sign-in, or null if autostart is off.</summary>
    public static string? Command()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) as string;
    }

    public static bool IsEnabled => Command() != null;

    /// <summary>On: start this exe, from where it is now, at sign-in. Off: remove the value.</summary>
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
