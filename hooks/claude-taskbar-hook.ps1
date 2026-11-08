# Claude Taskbar — session-state hook
#
# Installed by the Claude Taskbar settings window as
# %USERPROFILE%\.claude\hooks\claude-taskbar-hook.ps1 (the app embeds this file and writes
# it there, together with the matching hook entries in %USERPROFILE%\.claude\settings.json).
# Self-contained: it reads and writes only its own files, writing one small JSON state file
# per Claude Code session for the taskbar status-indicator app to read. Other hooks in the
# same folder or settings file are unaffected by it.
#
# Usage: called from settings.json hooks with -State one of:
#   start | working | idle | question | error | end | subagent_start | subagent_stop
#
# State file: %LOCALAPPDATA%\ClaudeTaskbar\sessions\<session_id>.json
#   { "sessionId": "...", "cwd": "...", "state": "...", "updatedAt": "<ISO 8601 UTC>",
#     "activeSubagents": N, "transcriptPath": "..." }
# "end" deletes the file instead of writing it.
# "subagent_start"/"subagent_stop" adjust activeSubagents via read-modify-write, leaving
# the main "state" field untouched (a hover-detail count, not a top-level color).
# transcriptPath: `transcript_path` is a documented common field on every hook's JSON
# stdin payload (confirmed against the transcript file's own real schema, not guessed) —
# the app reads it directly to sum real per-session token usage for the hover-detail
# tooltip. Kept updated on every write since a session's
# transcript path is stable for its lifetime, but writing it every time costs nothing.

param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('start', 'working', 'idle', 'question', 'error', 'end', 'subagent_start', 'subagent_stop')]
    [string]$State
)

$ErrorActionPreference = 'SilentlyContinue'

# Claude Code sends the hook's JSON as UTF-8. [Console]::In would decode it with the
# console's code page and garble non-ASCII paths (a user name with accents or a non-Latin script).
$raw = (New-Object System.IO.StreamReader([Console]::OpenStandardInput(), [System.Text.Encoding]::UTF8)).ReadToEnd()
try { $data = $raw | ConvertFrom-Json } catch { exit 0 }

$sessionId = "$($data.session_id)"
# Session ids are UUIDs; anything else is not used as a file name.
if ($sessionId -notmatch '^[0-9A-Fa-f-]{1,64}$') { exit 0 }

$dir = Join-Path $env:LOCALAPPDATA 'ClaudeTaskbar\sessions'
New-Item -ItemType Directory -Path $dir -Force | Out-Null
$file = Join-Path $dir "$sessionId.json"

if ($State -eq 'end') {
    Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
    exit 0
}

$cwd = if ($data.cwd) { "$($data.cwd)" } else { (Get-Location).Path }
$transcriptPath = if ($data.transcript_path) { "$($data.transcript_path)" } else { $null }

# Write via a temp file + move so the taskbar app's file watcher never sees a
# partially-written JSON file.
$tmp = "$file.tmp"

if ($State -eq 'subagent_start' -or $State -eq 'subagent_stop') {
    $existingState = 'working' # a Task tool call implies the top-level session is working
    $existingCount = 0
    if (Test-Path -LiteralPath $file) {
        try {
            $existing = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json
            if ($existing.state) { $existingState = $existing.state }
            if ($existing.activeSubagents) { $existingCount = [int]$existing.activeSubagents }
        } catch {}
    }
    $delta = if ($State -eq 'subagent_start') { 1 } else { -1 }
    # A pending question keeps the time it was asked. The app hides a question as soon as the
    # transcript moves past that time (answering fires no hook), so re-stamping it here revived
    # an answered question as purple until Claude next wrote to the transcript (2026-11-08).
    $updatedAt = (Get-Date).ToUniversalTime().ToString('o')
    if ($existingState -eq 'question' -and $existing.updatedAt) {
        $updatedAt = if ($existing.updatedAt -is [DateTime]) { $existing.updatedAt.ToUniversalTime().ToString('o') } else { "$($existing.updatedAt)" }
    }
    $newCount = [Math]::Max(0, $existingCount + $delta)
    if (-not $transcriptPath -and $existing.transcriptPath) { $transcriptPath = "$($existing.transcriptPath)" }

    $obj = [ordered]@{
        sessionId       = $sessionId
        cwd             = $cwd
        state           = $existingState
        updatedAt       = $updatedAt
        activeSubagents = $newCount
        transcriptPath  = $transcriptPath
    }
} else {
    $existingCount = 0
    if (Test-Path -LiteralPath $file) {
        try {
            $existing = Get-Content -Raw -LiteralPath $file | ConvertFrom-Json
            if ($existing.activeSubagents) { $existingCount = [int]$existing.activeSubagents }
            if (-not $transcriptPath -and $existing.transcriptPath) { $transcriptPath = "$($existing.transcriptPath)" }
        } catch {}
    }
    $obj = [ordered]@{
        sessionId       = $sessionId
        cwd             = $cwd
        state           = $State
        updatedAt       = (Get-Date).ToUniversalTime().ToString('o')
        activeSubagents = $existingCount
        transcriptPath  = $transcriptPath
    }
}

$obj | ConvertTo-Json -Compress | Set-Content -LiteralPath $tmp -Encoding utf8
Move-Item -LiteralPath $tmp -Destination $file -Force

exit 0
