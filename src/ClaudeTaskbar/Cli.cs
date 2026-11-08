using System.Diagnostics;
using System.Text.Json;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

/// <summary>
/// The switches the settings app (settings_app/, Flutter) calls: everything that touches the
/// system stays here in C#, so the Flutter side is only the window. Each prints one line of
/// JSON on stdout and exits 0, or prints <c>{"failed": "..."}</c> and exits 1.
///
/// <list type="bullet">
/// <item><c>--status</c>: the version, autostart, and the hooks' status.</item>
/// <item><c>--autostart on|off</c>: sets the Run value to this exe, or removes it; then the status.</item>
/// <item><c>--hooks status|install|uninstall [&lt;claude dir&gt;]</c>: the hook installer; the
/// directory defaults to ~/.claude (another one is for testing against a copy); then the status.</item>
/// <item><c>--quit</c>: asks the running copy to end and waits until its process is gone.</item>
/// </list>
/// </summary>
public static class Cli
{
    /// <summary>Handles <paramref name="args"/> if they are one of these switches, returning
    /// the exit code; null means they are not.</summary>
    public static int? Run(string[] args)
    {
        if (args.Length == 0) return null;
        try
        {
            switch (args[0])
            {
                case "--status" when args.Length == 1:
                    return Print(new HookInstaller());
                case "--autostart" when args.Length == 2 && args[1] is "on" or "off":
                    Autostart.Set(args[1] == "on");
                    return Print(new HookInstaller());
                case "--hooks" when args.Length is 2 or 3 && args[1] is "status" or "install" or "uninstall":
                    var installer = new HookInstaller(args.Length == 3 ? args[2] : null);
                    if (args[1] == "install") installer.Install();
                    else if (args[1] == "uninstall") installer.Uninstall();
                    return Print(installer);
                case "--quit" when args.Length == 1:
                    return Quit();
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            DiagLog.Write($"cli {string.Join(' ', args)}: {ex.GetType().Name}: {ex.Message}");
            Console.WriteLine(JsonSerializer.Serialize(new { failed = ex.Message.TrimEnd('.') }));
            return 1;
        }
    }

    public static string Version =>
        typeof(Cli).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "unknown";

    private static int Print(HookInstaller installer)
    {
        var st = installer.GetStatus();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            version = Version,
            autostart = Autostart.IsEnabled,
            hooks = new
            {
                state = st.State.ToString(),
                entries = st.EntriesPresent,
                total = HookInstaller.Entries.Length,
                script = st.ScriptPresent,
                current = st.ScriptCurrent,
                error = st.Error,
            },
        }));
        return 0;
    }

    /// <summary>Posts <see cref="TaskbarWindow.QuitMessage"/> to the running copy's controller
    /// window, then waits on that process's handle: the real signal that it ended, rather
    /// than a fixed wait. The wait is capped at 10 s against a copy that hangs; then it says
    /// so and exits 1.</summary>
    private static int Quit()
    {
        var hwnd = Win32.FindWindow(TaskbarWindow.ControllerClassName, null);
        if (hwnd == IntPtr.Zero)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { quit = "not running" }));
            return 0;
        }
        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        using var process = Process.GetProcessById((int)pid);
        Win32.PostMessage(hwnd, TaskbarWindow.QuitMessage, IntPtr.Zero, IntPtr.Zero);
        if (!process.WaitForExit(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("Claude Taskbar did not end within 10 seconds");
        Console.WriteLine(JsonSerializer.Serialize(new { quit = "ended" }));
        return 0;
    }
}
