using System.Media;
using ClaudeTaskbar.Models;

namespace ClaudeTaskbar;

/// <summary>
/// Plays a Windows system sound on certain session state transitions — the audio cue this
/// whole project originally set out to replace (an earlier completion chime that also fired
/// for subagent completion and was silent when system sounds were off), done
/// deliberately this time: three specific transitions keyed off this app's own already-
/// tracked State/Color model (see Models/SessionState.cs), not "any tool failure" or "any
/// completion". Done and error play the .wav the user's Windows sound scheme assigns to
/// Asterisk and Critical Stop (Control Panel's Sound > Sounds tab), and the warning plays
/// "Windows Exclamation.wav". All three go through SoundPlayer, not MessageBeep; see the
/// remarks at Sound below for why. The brief: Windows' current success sound, its fail
/// sound, and some neutral or warning sound. Live-verified end to end (real polling
/// pipeline, a synthetic session cycled through working -> idle -> error -> idle -> backdated-idle) — all three
/// transitions fired exactly once, in order, matching each step.
/// </summary>
public static class SessionSoundNotifier
{
    private sealed record Snapshot(string State, CircleColor Color);

    // Keyed by SessionId, which is a fresh, stable identity per real session, so it is safe
    // to hold across polls without ever confusing two different sessions.
    private static readonly Dictionary<string, Snapshot> Previous = new();

    /// <summary>Compares the newly-polled session list against this app's own last poll and
    /// plays a sound for any session that just crossed one of three watched transitions.
    /// Must be called once per poll with the FULL current session list (as returned by
    /// SessionStore.ReadAllWithSeeding, before any reordering/slot-building) so a closed
    /// session's tracked state is correctly dropped rather than lingering forever.</summary>
    public static List<string> NotifyTransitions(List<SessionState> sessions)
    {
        var finished = new List<string>(); // sessions that just played "done" (TaskbarWindow flashes them)
        var seenIds = new HashSet<string>();
        foreach (var s in sessions)
        {
            seenIds.Add(s.SessionId);

            if (Previous.TryGetValue(s.SessionId, out var prev))
            {
                if (prev.State != "error" && s.State == "error")
                {
                    // Fail: newly errored (a real crash/error that stops the agent, not a
                    // user cancel or a routine recoverable tool failure: red means a real error).
                    Play(Sound.Error, s);
                }
                else if (prev.State is "working" or "background" && s.State is "idle" or "start" && s.Color == CircleColor.Green)
                {
                    // Success: was working (or waiting on its background work), just finished — freshly Green, not a
                    // stale Blue re-poll of something that was already done.
                    Play(Sound.Done, s);
                    finished.Add(s.SessionId);
                }
                else if (prev.Color == CircleColor.Green && s.Color == CircleColor.Blue)
                {
                    // Neutral/warning: finished a while ago and still hasn't been checked
                    // (the ones that slip into idle). Uses
                    // SessionState.Color's own Green->Blue threshold.
                    Play(Sound.Warning, s);
                }
            }

            Previous[s.SessionId] = new Snapshot(s.State, s.Color);
        }

        // Drop sessions that no longer appear (closed window, or a seed placeholder that a
        // real session has now covered) so this dictionary doesn't grow unbounded over a
        // long-running app lifetime, and so a session id that somehow got reused doesn't
        // inherit stale prior state.
        foreach (var goneId in Previous.Keys.Except(seenIds).ToList())
        {
            Previous.Remove(goneId);
        }
        return finished;
    }

    public enum Sound { Done, Error, Warning }

    // BUG found live, 2026-09-29 (the warning sound seemed never to fire, then no sound was
    // heard at all). Two separate problems:
    // 1. In the sound scheme where this was found
    //    (HKCU\AppEvents\Schemes\Apps\.Default), SystemExclamation and SystemAsterisk were both
    //    "Windows Background.wav", so the warning sounded exactly like "done". The warning now
    //    plays Windows' own "Windows Exclamation.wav".
    // 2. diag.log proved the app DID play a sound ("sound: done") and nothing was heard.
    //    SystemSounds.X.Play() is MessageBeep, which goes through the Volume Mixer's "System
    //    Sounds" channel, and that channel can be silent. All three now play their .wav
    //    through SoundPlayer (PlaySound), in this app's own audio session. Done and error
    //    still use the files the scheme assigns to Asterisk and Critical Stop, as originally
    //    intended (Windows' current success and fail sounds).
    private static string? WavFor(Sound sound) => sound switch
    {
        Sound.Done => SchemeWav("SystemAsterisk"),
        Sound.Error => SchemeWav("SystemHand"),
        _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Windows Exclamation.wav"),
    };

    private static string? SchemeWav(string eventName)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey($@"AppEvents\Schemes\Apps\.Default\{eventName}\.Current");
        var value = key?.GetValue(null) as string;
        return string.IsNullOrWhiteSpace(value) ? null : Environment.ExpandEnvironmentVariables(value);
    }

    // 2026-09-29, on headphones: the sounds played but were very quiet. Measured:
    // the files peak at only 34.6% (Background, done), 28.5% (Foreground, error) and 9.7%
    // (Exclamation, warning) of full scale. So each sound is scaled so its peak lands at
    // TargetPeak: all three end up equally loud and much louder, with no clipping. It is
    // never turned down. Tune this one number if they're too loud or too quiet.
    private const double TargetPeak = 1.0; // full scale: the user controls loudness with the system volume

    /// <summary>The .wav's bytes with its 16-bit PCM samples scaled so the peak is
    /// TargetPeak of full scale. Any other format is returned unchanged.</summary>
    private static byte[] Normalized(string wavPath)
    {
        var bytes = File.ReadAllBytes(wavPath);
        int? bits = null, dataStart = null, dataLen = null;
        var pos = 12; // after "RIFF" <size> "WAVE"
        while (pos + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
            var len = BitConverter.ToInt32(bytes, pos + 4);
            if (id == "fmt " && BitConverter.ToInt16(bytes, pos + 8) == 1) bits = BitConverter.ToInt16(bytes, pos + 22); // PCM
            if (id == "data") { dataStart = pos + 8; dataLen = Math.Min(len, bytes.Length - pos - 8); break; }
            pos += 8 + len + (len & 1);
        }
        if (bits != 16 || dataStart is not { } start || dataLen is not { } n) return bytes;

        var peak = 1;
        for (var i = start; i + 1 < start + n; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(bytes, i)));
        var gain = TargetPeak * short.MaxValue / peak;
        if (gain <= 1) return bytes;
        for (var i = start; i + 1 < start + n; i += 2)
        {
            var v = (int)Math.Round(BitConverter.ToInt16(bytes, i) * gain);
            BitConverter.TryWriteBytes(bytes.AsSpan(i, 2), (short)Math.Clamp(v, short.MinValue, short.MaxValue));
        }
        return bytes;
    }

    // One player per file, kept for the app's lifetime: Play() is asynchronous, and disposing
    // a player mid-playback can cut the sound off.
    private static readonly Dictionary<string, SoundPlayer> Players = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plays the sound and records it in diag.log, so "did it fire?" can be answered
    /// from the log. If the scheme assigns no file, nothing plays, and that is logged too.</summary>
    public static void Play(Sound sound, SessionState? s = null, bool wait = false)
    {
        var who = s == null ? "test" : $"'{(string.IsNullOrEmpty(s.TabTitle) ? s.ProjectName : s.TabTitle)}' {s.SessionId[..8]}";
        var wav = WavFor(sound);
        if (wav == null || !File.Exists(wav))
        {
            DiagLog.Write($"sound: {sound} — no file to play ({wav ?? "scheme assigns none"}) — {who}");
            return;
        }
        try
        {
            if (!Players.TryGetValue(wav, out var player)) Players[wav] = player = new SoundPlayer(new MemoryStream(Normalized(wav)));
            if (wait) player.PlaySync(); else player.Play();
            DiagLog.Write($"sound: {sound} ({Path.GetFileName(wav)}) — {who}");
        }
        catch (Exception ex)
        {
            DiagLog.Write($"sound: {sound} failed to play {wav}: {ex.GetType().Name}: {ex.Message} — {who}");
        }
    }
}
