# hooks

`claude-taskbar-hook.ps1` is the Claude Code hook the app depends on. This copy is the source
of truth: it is embedded in `ClaudeTaskbar.exe` at build time, and the app's settings window
installs it as `%USERPROFILE%\.claude\hooks\claude-taskbar-hook.ps1`. Claude Code runs the
installed copy. After changing this file, rebuild and reinstall from the settings window; it
shows the installed copy as outdated until then.

The settings window also adds these entries to `~/.claude/settings.json` (backing the file up
first, and leaving every other setting and hook alone):

| Event (matcher) | `-State` |
|---|---|
| UserPromptSubmit | working |
| Stop | idle |
| StopFailure | error |
| Notification (`permission_prompt\|elicitation_dialog\|elicitation_url_dialog\|agent_needs_input`) | question |
| PreToolUse (`AskUserQuestion`) | question |
| PreToolUse (`Task\|Agent`) / SubagentStop | subagent_start / subagent_stop (counter only; the colour uses SubagentTracker) |
| SessionStart (`startup\|resume\|clear\|fork`) | start |
| SessionEnd | end (deletes the state file) |
| PreCompact (`manual`) / PostCompact (`manual`) | working / idle |
