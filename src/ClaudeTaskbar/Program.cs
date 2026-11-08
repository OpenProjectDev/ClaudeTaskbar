using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Found live, 2026-09-28 (clicking a circle just kept refocusing the current tab):
        // when this app is launched from inside VS Code (e.g. deployed from a
        // Claude Code session), it inherits the extension host's environment, including
        // ELECTRON_RUN_AS_NODE=1. Every process it starts inherits that too, so the
        // vscode:// deep link's Code.exe ran as plain Node and silently did nothing.
        // Confirmed: the same link did nothing from a shell with that variable set. None of
        // these variables mean anything outside VS Code/Claude Code, so drop them all.
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var name = (string)e.Key;
            if (name == "ELECTRON_RUN_AS_NODE" || name.StartsWith("VSCODE_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase))
            {
                Environment.SetEnvironmentVariable(name, null);
            }
        }

        // The settings app's switches (--status, --autostart, --hooks, --quit): see Cli.
        if (Cli.Run(args) is { } code)
        {
            Environment.ExitCode = code;
            return;
        }

        using var store = new SessionStore();

        // `ClaudeTaskbar.exe --dump <file>`: run the exact same session/slot pipeline once
        // and write what it computed, for debugging against live data. No window is created.
        if (args.Length == 2 && args[0] == "--dump")
        {
            File.WriteAllText(args[1], new TaskbarWindow(store).DumpDiagnostics());
            return;
        }

        // `--sound done|error|warning`: play one of the app's sounds exactly as a transition
        // would (same files, same SoundPlayer path), for checking they're audible.
        if (args.Length == 2 && args[0] == "--sound" && Enum.TryParse<SessionSoundNotifier.Sound>(args[1], true, out var sound))
        {
            SessionSoundNotifier.Play(sound, wait: true);
            return;
        }

        // `--watch <file> <seconds>`: the same pipeline on the real 400ms cadence in one
        // long-lived process (so per-process caches behave as in the real app), logging
        // each distinct result with a timestamp.
        if (args.Length == 3 && args[0] == "--watch")
        {
            var w = new TaskbarWindow(store);
            var until = DateTime.UtcNow.AddSeconds(int.Parse(args[2]));
            string? last = null;
            while (DateTime.UtcNow < until)
            {
                var dump = w.DumpDiagnostics();
                // Timestamps inside the dump change constantly; compare on the SLOTS section.
                var slots = dump[dump.IndexOf("SLOTS", StringComparison.Ordinal)..];
                if (slots != last)
                {
                    File.AppendAllText(args[1], $"===== {DateTime.Now:HH:mm:ss.fff}\n{dump}\n");
                    last = slots;
                }
                Thread.Sleep(400);
            }
            return;
        }

        // Any other argument is a mistake: say so rather than start the app, which as a second
        // copy would open the running one's settings window.
        if (args.Length > 0)
        {
            Console.Error.WriteLine($"Unknown arguments: {string.Join(' ', args)}");
            Environment.ExitCode = 2;
            return;
        }

        // One copy per user session. Launching the exe again shows the running copy's
        // settings window instead.
        using var instance = new Mutex(true, @"Local\ClaudeTaskbar.Instance", out var isFirst);
        if (!isFirst)
        {
            var running = Win32.FindWindow(TaskbarWindow.ControllerClassName, null);
            if (running != IntPtr.Zero)
            {
                // This process was started by the user, so it may hand foreground rights on.
                Win32.GetWindowThreadProcessId(running, out var pid);
                Win32.AllowSetForegroundWindow(pid);
                Win32.PostMessage(running, TaskbarWindow.ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
            }
            return;
        }

        var window = new TaskbarWindow(store);
        window.Run();
    }
}
