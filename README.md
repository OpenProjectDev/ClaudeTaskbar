# Claude Taskbar

<img src="icon/claudetaskbar-mark-256.png" width="96" align="right">

A status light for every Claude Code session you have open in VS Code, drawn inside the Windows 11 taskbar next to the tray. One circle per session, grouped by virtual desktop, so you can see at a glance which session is working, which one finished, and which one is waiting for you.

## What the circles mean

| Colour | Meaning |
|---|---|
| Orange | Working: Claude or a subagent |
| Yellow | Turn done, background commands still running |
| Purple | Waiting for your answer or a permission |
| Red | Stopped with an error |
| Green | Finished in the last 10 minutes |
| Blue | Finished longer ago |
| Grey | An empty virtual desktop, or a session you marked as seen |

- Sessions on the same virtual desktop sit together in a capsule. A white ring marks the current desktop, and the focused session in it when that is known.
- When a session finishes, its circle pulses for about 10 seconds. Sounds play when a session finishes, when one stops with an error, and when a finished one turns from green to blue; they come from your Windows sound scheme, except the last, and are played at full volume. There is no setting to turn them off yet.
- Hover a circle for the chat's title, its state, its token count and any running subagents or background commands.

| Mouse | Does |
|---|---|
| Click | Switches to the session's desktop and window and opens its tab (the first time, VS Code asks whether to allow opening the link) |
| Double-click | Marks a finished session as seen (grey), or undoes it |
| Right-click | Opens the settings window |

## Requirements

- Windows 11, with the taskbar on the primary monitor.
- VS Code with the Claude Code extension, a recent version: built and tested with 2.1.319. It uses hook events such as `StopFailure` and `PostCompact` that older versions may not know. Sessions are found through VS Code's open tabs; terminal-only sessions are not shown.
- Made for the dark taskbar. On the light one the tooltip adapts, but the white ring is hard to see.

## Install

1. Download `ClaudeTaskbar-<version>-win-x64.zip` from [Releases](https://github.com/OpenProjectDev/ClaudeTaskbar/releases) and unzip it. Put the `ClaudeTaskbar` folder inside somewhere you'll keep, such as `%LOCALAPPDATA%\ClaudeTaskbar`: Start with Windows remembers where the exe is. The `settings` folder must stay beside `ClaudeTaskbar.exe`.
2. Run `ClaudeTaskbar.exe`. The first time, the settings window opens because the Claude Code hooks aren't installed yet.
3. Click **Install** under Claude Code hooks, and **Start with Windows** if you want it at sign-in.

Nothing else needs installing: .NET is built into the exe, and the Visual C++ runtime the settings window needs is in its folder.

To open the settings again, right-click any circle, or run `ClaudeTaskbar.exe` again while it is running. **Terminate Claude Taskbar** in the settings window stops it.

## What Install changes

The app needs Claude Code to report each session's state, through [hooks](https://code.claude.com/docs/en/hooks):

- It writes `%USERPROFILE%\.claude\hooks\claude-taskbar-hook.ps1`.
- It adds one entry per event it needs to `%USERPROFILE%\.claude\settings.json`, beside your own hooks, which it never changes. Before writing, it saves a copy as `settings.json.bak-claude-taskbar-<date>-<time>`. It changes nothing in a `settings.json` that isn't plain JSON (comments and trailing commas count) or whose `hooks` aren't in the shape Claude Code reads, and says so instead.
- Each hook runs `powershell -NoProfile -ExecutionPolicy Bypass -File <that script>`, which writes the session's state to `%LOCALAPPDATA%\ClaudeTaskbar\sessions\`.

**Remove** takes out only the entries it added, and the script; `settings.json` is backed up and rewritten only if it had any.

The hooks run Windows PowerShell with `-ExecutionPolicy Bypass`, which a Group Policy execution policy still overrides; on such a machine the circles get no state.

The app also keeps a log of failed reads and played sounds in `%LOCALAPPDATA%\ClaudeTaskbar\diag.log`, and the circles you marked as seen in `seen.json` beside it. Nothing leaves your computer, except the version check in the settings window, which asks GitHub for the latest release when you click the version.

## A full taskbar

The circles sit in the space left of the tray. Windows 11 has no way for an app to reserve taskbar space, so if your taskbar buttons fill the bar they can run under the circles. With [Windhawk](https://windhawk.net/)'s "Windows 11 Taskbar Styler" mod you can keep room free: add a style rule with the target

```
Taskbar.TaskbarFrame > Grid#RootGrid > Microsoft.UI.Xaml.Controls.ItemsRepeater#TaskbarFrameRepeater
```

and the style `Margin=0,0,<room>,0`, where `<room>` is in the taskbar's own units: about 18 per circle, plus anything else you keep there. This is optional and outside the app.

## How it works, and what can break it

Claude Code has no public API for "which sessions are open in which VS Code window", so the app combines documented and undocumented sources:

- **Documented:** Claude Code hooks (session state), Win32 (windows, the taskbar).
- **Undocumented:** VS Code's saved tab list (`state.vscdb` in its workspace storage) to know which sessions are open, and in what order; Claude Code's transcript and subagent files to tell running subagents and background commands apart; the virtual-desktop COM interfaces to group circles by desktop; the way TrafficMonitor embeds its window in the Windows 11 taskbar.

Each undocumented source is read defensively: when one changes, the app shows less (no ordering, no desktop grouping) rather than something wrong, and logs it in `diag.log`. A VS Code, Claude Code or Windows update can still change what the app sees.

## Building it

- The circles app: .NET 8 SDK. From `src/ClaudeTaskbar`: `dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o out`
- The settings window, a separate Flutter app the circles app starts: Flutter (stable) with the Windows desktop toolchain. From `settings_app`: `flutter build windows --release`, then copy `build\windows\x64\runner\Release\*` into a `settings` folder beside `ClaudeTaskbar.exe`.

Everything the settings window changes goes through `ClaudeTaskbar.exe` switches (`src/ClaudeTaskbar/Cli.cs`): `--status`, `--autostart on|off`, `--hooks status|install|uninstall`, `--quit`.

## License

GPLv3 or later, except for the third-party files listed in [NOTICE.md](NOTICE.md). Claude is a trademark of Anthropic; this project is not made by or affiliated with Anthropic.
