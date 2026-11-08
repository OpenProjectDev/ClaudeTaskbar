using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using ClaudeTaskbar.Models;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

/// <summary>
/// The status window, positioned immediately left of the system tray/notification cluster
/// and painted with one filled circle per active Claude Code session (grey placeholder
/// when there are none). It is embedded in the taskbar as a dialog, the way TrafficMonitor
/// does it (see TaskbarEmbeddedWindow: the embedded window must be a real #32770 dialog,
/// because a plain CreateWindowEx window parented to the taskbar never renders); it was a
/// floating always-on-top window before that, and falls back to one if embedding fails.
///
/// Rendering: GDI+ (anti-aliased) into a DIB section via LayeredBitmapRenderer, composited
/// with UpdateLayeredWindow (real per-pixel alpha). Replaces an earlier WM_PAINT +
/// SetLayeredWindowAttributes(LWA_COLORKEY) approach that had visible flicker on refresh
/// (InvalidateRect(erase:true) on every timer tick) and a hard-edged fringe around
/// anti-aliased circle edges (colorkey transparency is binary per pixel; real alpha
/// blending isn't) — both were live-feedback complaints.
///
/// Layout (2026-09-27): one-circle-per-session stays the basis, laid out as a uniform
/// row of "slots" (see CircleSlot) built by BuildSlots — every slot is exactly one circle
/// (a real session, or a grey filler for a virtual desktop with zero sessions), always the
/// same size and the same Gap apart, regardless of whether it happens to share a desktop
/// with others. When 2+ sessions share one desktop, Draw additionally paints a capsule/pill
/// background behind that contiguous run of circles — this must NOT change the circles' own
/// positions or spacing (the developer's correction: "render your circles as if theres no
/// capsule, then render the capsule around them").
/// </summary>
public sealed class TaskbarWindow
{
    private const string ClassName = "ClaudeTaskbarWndClass";
    private const int CircleDiameter = 19; // was 21; 10% smaller per feedback (21 * 0.9 ~= 19)
    private const int Gap = 12; // 6 * 2, per feedback
    private const int Padding = 8;
    private const int WindowHeight = 32; // was 24; grown to fit the larger circle with margin
    private const int VerticalNudge = 2; // was 4 (overcorrected from an earlier "too high"
                                          // complaint) — feedback said still "a tiny tiny bit
                                          // low", so nudged back up slightly rather than swung hard
    private const int TrayMargin = 6; // gap left of whatever we anchor to

    // Capsule geometry — purely decorative outward padding around a contiguous run
    // of circles that already sit at their normal position/Gap (see class remarks). Never
    // used to compute circle position, only the background pill's own bounding box.
    private const int CapsulePadX = 6;
    private const int CapsulePadY = 5;

    private readonly Win32.WndProc _wndProcDelegate; // kept alive: prevents the marshaled callback from being GC'd
    private readonly Win32.WinEventProc _winEventProcDelegate; // kept alive, same reason
    private IntPtr _winEventHook;
    private readonly SessionStore _store;
    private readonly TooltipWindow _tooltip = new();
    private readonly LayeredBitmapRenderer _renderer = new();
    private IntPtr _hwnd; // hidden controller window — see Run
    private TaskbarEmbeddedWindow _circles = null!; // the visible circles, embedded in the taskbar
    private int _currentWidth;
    private List<SessionState> _sessions = new();
    private List<CircleSlot> _slots = new();
    private int? _hoveredIndex;
    private bool _tracking;
    private Color _lastSampledBackground = FallbackTaskbarColor; // refreshed once per Reflow — see SampleTaskbarBackground

    public TaskbarWindow(SessionStore store)
    {
        _store = store;
        _wndProcDelegate = WndProc;
        _winEventProcDelegate = OnForegroundChanged;
        _store.Changed += () =>
        {
            // FileSystemWatcher events fire on a background thread — never touch the
            // window/GDI from there. Hop to the message loop via PostMessage instead.
            if (_hwnd != IntPtr.Zero) Win32.PostMessage(_hwnd, Win32.WM_APP_REPAINT, IntPtr.Zero, IntPtr.Zero);
        };
    }

    /// <summary>--dump (see Program): one pass of the real pipeline, as text.</summary>
    public string DumpDiagnostics()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("OPEN WINDOWS");
        sb.Append(SessionStore.DescribeOpenWindows());
        var sessions = _store.ReadAllWithSeeding();
        sb.AppendLine("SESSIONS");
        foreach (var s in sessions)
        {
            sb.AppendLine($"  {s.SessionId} state={s.State} color={s.Color} project={s.ProjectName} tab={s.IsOpenAsTab} winId={VsCodeWindowIds.Find(s.SessionId, s.CanonicalCwd)} subagents={s.RunningSubagents} bgcmds={s.RunningBackgroundCommands} title='{s.TabTitle}' hwnd={s.WindowHandle} cwd={s.Cwd} canonical={s.CanonicalCwd} updated={s.UpdatedAtUtc:O} transcript={s.TranscriptPath}");
        }
        sb.AppendLine("SLOTS");
        foreach (var slot in BuildSlots(sessions))
        {
            sb.AppendLine($"  {slot.Kind} desktop={slot.DesktopName}({slot.DesktopId}) current={slot.IsCurrentDesktop} session={slot.Session?.SessionId} capsule={slot.CapsuleIndexInGroup}/{slot.CapsuleGroupSize} focused={slot.IsCapsuleFocusedMember}");
        }
        return sb.ToString();
    }

    public void Run()
    {
        Win32.SetProcessDpiAwarenessContext(Win32.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hbrBackground = IntPtr.Zero, // we paint everything ourselves
            hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW), // see Win32.cs — was
                                                                       // left unset, causing
                                                                       // a stuck wait cursor
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);

        _sessions = _store.ReadAllWithSeeding();
        SessionSoundNotifier.NotifyTransitions(_sessions); // establishes the baseline; see WM_TIMER for the real per-tick check
        _slots = BuildSlots(_sessions);
        _currentWidth = ComputeWidth(_slots);

        // Two windows. _hwnd is a never-shown controller that owns the timer and
        // app messages and is the hidden OWNER of the visible windows (TrafficMonitor's
        // hidden main dialog plays the same role). The circles themselves live in
        // _circles, a dialog embedded in the taskbar — see TaskbarEmbeddedWindow for why
        // and how. Keeping the timer on the controller means an Explorer restart, which
        // destroys the embedded window along with the taskbar, can't stop the app: the
        // next tick just re-creates it.
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW, ClassName, "Claude Taskbar Controller", Win32.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        _circles = new TaskbarEmbeddedWindow(_hwnd, "Claude Taskbar", HandleCirclesMessage);

        Reflow(); // creates/embeds the circles window, positions it and pushes the first frame

        // Reposition/redraw periodically: the taskbar rect can change (DPI/monitor change,
        // tray icon count change, taskbar auto-hide), and this is also what re-creates the
        // embedded windows after an Explorer restart.
        Win32.SetTimer(_hwnd, IntPtr.Zero, 400, IntPtr.Zero); // per feedback: 400ms sync rate

        // Immediate Reflow on any foreground change (EVENT_SYSTEM_FOREGROUND, documented
        // SetWinEventHook): originally added to reclaim topmost after a desktop switch or
        // the Start menu (it didn't fix that — embedding did, see TaskbarEmbeddedWindow),
        // kept because it makes the focused-session ring follow window focus instantly
        // rather than on the next 400ms tick.
        _winEventHook = Win32.SetWinEventHook(
            Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProcDelegate, 0, 0, Win32.WINEVENT_OUTOFCONTEXT);

        // A new user has no hooks yet, and without them the circles can't show much: open
        // the settings window on its own so they can install them. Also when the status
        // can't be read at all: the window says why.
        try
        {
            var hooks = new HookInstaller().GetStatus();
            if (hooks.State == HookInstaller.State.NotInstalled || hooks.Error != null)
                Win32.PostMessage(_hwnd, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"startup: hook status failed: {ex.GetType().Name}: {ex.Message}");
            Win32.PostMessage(_hwnd, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
        }

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            Win32.TranslateMessage(ref msg);
            Win32.DispatchMessage(ref msg);
        }
    }

    /// <summary>The hidden controller window (see Run): timer and app messages only.</summary>
    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == ShowSettingsMessage)
        {
            ShowSettings();
            return IntPtr.Zero;
        }
        if (msg == QuitMessage)
        {
            Win32.DestroyWindow(_hwnd); // WM_DESTROY ends the message loop
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case Win32.WM_TIMER when wParam == FlashTimerId:
                TickFlash();
                return IntPtr.Zero;
            case Win32.WM_TIMER when wParam == TabLinkTimerId:
                TickPendingTabLink();
                return IntPtr.Zero;
            case Win32.WM_APP_REPAINT:
            case Win32.WM_TIMER:
                // The timer also re-reads sessions (not just repositions): VS Code's own tab
                // list isn't covered by the FileSystemWatcher on our sessions directory, so
                // this is what picks up sessions that haven't fired one of our hooks yet.
                RefreshSessions();
                Reflow();
                return IntPtr.Zero;
            case Win32.WM_DESTROY:
                if (_winEventHook != IntPtr.Zero) Win32.UnhookWinEvent(_winEventHook);
                Win32.PostQuitMessage(0);
                return IntPtr.Zero;
            default:
                return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }

    /// <summary>The embedded circles dialog (see TaskbarEmbeddedWindow): paint ack and
    /// mouse. Returns true if handled.</summary>
    private bool HandleCirclesMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_PAINT:
                // Content is pushed entirely via UpdateLayeredWindow, not this window's own
                // DC — there's nothing to draw here, just ack the update region so Windows
                // stops re-queuing WM_PAINT.
                Win32.BeginPaint(hWnd, out var ps);
                Win32.EndPaint(hWnd, ref ps);
                return true;
            case Win32.WM_MOUSEMOVE:
                OnMouseMove(hWnd, lParam);
                return true;
            case Win32.WM_LBUTTONDBLCLK:
                OnDoubleClick(lParam);
                return true;
            case Win32.WM_LBUTTONDOWN:
                OnClick(lParam);
                return true;
            case Win32.WM_RBUTTONUP:
                // Posted, so the window opens from the controller's message loop rather than
                // inside this dialog's message handling.
                Win32.PostMessage(_hwnd, ShowSettingsMessage, IntPtr.Zero, IntPtr.Zero);
                return true;
            case Win32.WM_MOUSELEAVE:
                _tracking = false;
                _hoveredIndex = null;
                _tooltip.Hide();
                return true;
            default:
                return false;
        }
    }

    /// <summary>SetWinEventHook callback (see Run) — fires the instant the foreground
    /// window changes anywhere on the system, including a virtual-desktop switch or the
    /// Start menu opening/closing. Delivered on this same thread's message queue
    /// (WINEVENT_OUTOFCONTEXT), so it's safe to touch the window/GDI state directly here,
    /// same as any other message-loop-driven handler.</summary>
    private void OnForegroundChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (_hwnd != IntPtr.Zero) Reflow();
    }

    /// <summary>Recompute layout (if the slot set changed) and always reposition — cheap,
    /// and covers the taskbar/tray/TrafficMonitor window having moved even when it
    /// didn't — then redraw and present in one UpdateLayeredWindow call.</summary>
    private void Reflow()
    {
        _circles.Ensure();
        if (_circles.Hwnd == IntPtr.Zero) return; // no taskbar right now (Explorer restarting) — next tick retries

        // Taskbar auto-hide: no special handling since 2026-09-28. The floating window used
        // to hide itself whenever auto-hide was on, because it behaved inconsistently with
        // an auto-hiding taskbar (2026-09-27). As a child of the taskbar it now slides away
        // and back with it, so it is shown again. To bring the hiding back, check
        // SHAppBarMessage(ABM_GETSTATE) & ABS_AUTOHIDE (declared in Win32.cs) and call
        // _circles.SetVisible(false).

        _currentWidth = ComputeWidth(_slots);
        var (x, y) = ComputePosition(_currentWidth);

        // Embedded in the taskbar, this is a plain move in taskbar-client coordinates with
        // no z-order call at all. The floating-window era fought for topmost (re-asserting
        // HWND_TOPMOST every tick; a disproved "insert after Shell_TrayWnd" attempt —
        // Shell_TrayWnd is itself topmost) and kept losing to the Start menu and desktop
        // switches; only the SetParent-failed fallback still re-asserts topmost
        // (TaskbarEmbeddedWindow.Place).
        _circles.Place(x, y, _currentWidth, WindowHeight);

        // Sampled once per tick here (not per-hover); the tooltip and capsule fill both reuse
        // this cached sample, keeping them consistent.
        _lastSampledBackground = StableBackground(SampleTaskbarBackground(x + _currentWidth));

        // The tooltip is still a floating topmost popup; anything else re-asserting topmost
        // can push it down (see TooltipWindow.ReassertTopmostIfVisible) — reclaim it every
        // tick so it never gets visibly cut through.
        _tooltip.ReassertTopmostIfVisible();

        Draw();
    }

    private void Draw()
    {
        using var g = _renderer.BeginDraw(_currentWidth, WindowHeight);

        var yPos = (WindowHeight - CircleDiameter) / 2;
        var totalWidth = _slots.Count * CircleDiameter + Math.Max(0, _slots.Count - 1) * Gap;
        var cursorX = Math.Max(Padding, (_currentWidth - totalWidth) / 2);

        for (var i = 0; i < _slots.Count; i++)
        {
            var slot = _slots[i];

            // A capsule group's background is drawn once, right before its first member,
            // spanning the group's existing (unchanged) footprint plus a bit of outward
            // padding — the circles themselves are still positioned by the exact same
            // uniform grid as every other slot, never squeezed together to fit the pill.
            if (slot.CapsuleGroupSize is { } groupSize && slot.CapsuleIndexInGroup == 0)
            {
                var groupWidth = groupSize * CircleDiameter + (groupSize - 1) * Gap;
                DrawCapsuleBackground(g, cursorX, yPos, groupWidth, slot.IsCurrentDesktop && !slot.CapsuleGroupHasFocusedMember);
            }

            var color = slot.Kind == SlotKind.Grey ? CircleColor.Grey : slot.Session!.Color;
            var ring = HasRing(slot);
            DrawCircle(g, color, cursorX, yPos, ring, FlashPulse(slot, color));

            cursorX += CircleDiameter + Gap;
        }

        _circles.Present(_renderer);
    }

    private static void DrawCircle(Graphics g, CircleColor color, int x, int y, bool ring, float pulse = 0)
    {
        var fill = ToColor(color);
        if (pulse > 0)
        {
            // Flash (see StartFlash): a soft halo of the circle's own colour swells around it
            // while the fill brightens toward white, both following the same pulse. The halo
            // stays inside the window's 32px height (19px circle + up to 12px).
            var halo = CircleDiameter + 2 + 10 * pulse;
            using var haloBrush = new SolidBrush(Color.FromArgb((int)(170 * pulse), fill));
            g.FillEllipse(haloBrush, x + CircleDiameter / 2f - halo / 2, y + CircleDiameter / 2f - halo / 2, halo, halo);
            fill = Lerp(fill, Color.White, 0.55f * pulse);
        }
        using var brush = new SolidBrush(fill);
        g.FillEllipse(brush, x, y, CircleDiameter, CircleDiameter);
        if (ring) DrawEllipseRing(g, x, y, CircleDiameter, CircleDiameter);
    }

    private static Color Lerp(Color a, Color b, float t) => Color.FromArgb(
        (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    // ---- Flash on finish (2026-10-22): when a circle turns green, an animation catches the
    // eye of someone looking at the keyboard, with the taskbar at the edge of their field of
    // vision, for about 10 seconds or until the tab is switched to or the circle is clicked.
    // Starts at the same transition as the "done" sound (working -> fresh Green). A smooth
    // 2.5 Hz pulse, since steady pulsing is what peripheral vision picks up best, drawn on a
    // dedicated 30 fps timer that only runs while something is flashing. Stops after
    // FlashDuration, on a click or double-click of that circle, once the user is looking at
    // the session (its VS Code window in front with its tab active), or once the circle is no
    // longer green (working again, marked seen, ...).
    private static readonly IntPtr FlashTimerId = new(3);
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(10);
    private const double FlashHz = 2.5;
    private readonly Dictionary<string, DateTime> _flashStartedUtc = new();

    /// <summary>Re-reads sessions, plays transition sounds and starts flashes for sessions
    /// that just finished, then rebuilds the slots.</summary>
    private void RefreshSessions()
    {
        _sessions = _store.ReadAllWithSeeding();
        foreach (var id in SessionSoundNotifier.NotifyTransitions(_sessions)) StartFlash(id);
        _slots = BuildSlots(_sessions);
    }

    private void StartFlash(string sessionId)
    {
        _flashStartedUtc[sessionId] = DateTime.UtcNow;
        Win32.SetTimer(_hwnd, FlashTimerId, 33, IntPtr.Zero);
    }

    private void StopFlash(string sessionId) => _flashStartedUtc.Remove(sessionId);

    private void TickFlash()
    {
        var now = DateTime.UtcNow;
        foreach (var (id, started) in _flashStartedUtc.ToList())
        {
            var s = _sessions.FirstOrDefault(x => x.SessionId == id);
            if (s == null || s.Color != CircleColor.Green || now - started > FlashDuration || IsLookingAt(s))
            {
                _flashStartedUtc.Remove(id);
            }
        }
        if (_flashStartedUtc.Count == 0) Win32.KillTimer(_hwnd, FlashTimerId);
        Draw(); // also draws one last frame without the pulse after the final flash ends
    }

    /// <summary>0..1 pulse for a flashing slot, 0 otherwise.</summary>
    private float FlashPulse(CircleSlot slot, CircleColor color)
    {
        if (slot.Session == null || color != CircleColor.Green) return 0;
        if (!_flashStartedUtc.TryGetValue(slot.Session.SessionId, out var started)) return 0;
        var t = (DateTime.UtcNow - started).TotalSeconds;
        return (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * FlashHz * t)); // starts at 0, smooth
    }

    /// <summary>The user is looking at this session: its VS Code window is in front and its tab
    /// is the active one (window title starts with the tab's title, as in FindFocusedMember).
    /// Without a saved tab title, the window being in front has to do.</summary>
    private static bool IsLookingAt(SessionState s)
    {
        if (s.WindowHandle is not { } hwnd || Win32.GetForegroundWindow() != hwnd) return false;
        if (string.IsNullOrEmpty(s.TabTitle)) return true;
        var sb = new System.Text.StringBuilder(512);
        Win32.GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();
        if (title.StartsWith("● ", StringComparison.Ordinal)) title = title[2..];
        return title.StartsWith(s.TabTitle + " - ", StringComparison.Ordinal);
    }


    /// <summary>The pill drawn behind a contiguous run of circles that share one
    /// virtual desktop — fill is the live-sampled taskbar background LIGHTENED a bit.
    /// Originally darkened ("slightly dimmer than the taskbar background"), but that didn't
    /// read well in use — switched to lighter instead, still never a fixed hardcoded color,
    /// so it still blends on either a dark or light-theme taskbar. Purely a backdrop: drawn
    /// first, underneath, at the group's existing circle positions plus a small outward pad
    /// — never changes where the circles themselves land.</summary>
    private void DrawCapsuleBackground(Graphics g, int groupX, int y, int groupWidth, bool ringWholeGroup)
    {
        var capsuleX = groupX - CapsulePadX;
        var capsuleY = y - CapsulePadY;
        var capsuleW = groupWidth + CapsulePadX * 2;
        var capsuleH = CircleDiameter + CapsulePadY * 2;
        var radius = capsuleH / 2f; // half-height radius on both ends = a pill shape, any group size

        var fill = Lighten(_lastSampledBackground, 20);
        using (var brush = new SolidBrush(fill))
        using (var path = RoundedRectPath(capsuleX, capsuleY, capsuleW, capsuleH, radius))
        {
            g.FillPath(brush, path);
        }

        // Ring case 2 of 2 (case 1 is the per-circle ring in Draw): the whole desktop is
        // current, but we couldn't reliably tell which specific session inside it has focus
        // — ring the pill itself instead of guessing a member.
        if (ringWholeGroup)
        {
            using var ringPen = new Pen(Color.White, RingWidth);
            var inset = ringPen.Width / 2f;
            using var ringPath = RoundedRectPath(capsuleX - inset, capsuleY - inset, capsuleW + ringPen.Width, capsuleH + ringPen.Width, radius + inset);
            g.DrawPath(ringPen, ringPath);
        }
    }

    // Ring stroke width — was 2f, then 3f ("a bit thicker"), now 2f again ("can afford to
    // be a little thinner too", after seeing it on the capsule as well as lone circles).
    private const float RingWidth = 2f;

    /// <summary>A ring drawn around a circle's own bounding box, outset by the pen's half-
    /// width so the stroke lands entirely outside the fill rather than eating into it
    /// (feedback: "the border takes up space from the actual circle's coloring").</summary>
    private static void DrawEllipseRing(Graphics g, float x, float y, float w, float h)
    {
        using var ringPen = new Pen(Color.White, RingWidth);
        var inset = ringPen.Width / 2f;
        g.DrawEllipse(ringPen, x - inset, y - inset, w + ringPen.Width, h + ringPen.Width);
    }

    private static GraphicsPath RoundedRectPath(float x, float y, float width, float height, float radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + width - d, y, d, d, 270, 90);
        path.AddArc(x + width - d, y + height - d, d, d, 0, 90);
        path.AddArc(x, y + height - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Color Lighten(Color c, int amount) =>
        Color.FromArgb(Math.Min(255, c.R + amount), Math.Min(255, c.G + amount), Math.Min(255, c.B + amount));

    internal static Color ToColor(CircleColor color) => color switch
    {
        CircleColor.Orange => Color.FromArgb(249, 115, 22), // more saturated than the old (255,165,0) — feedback: "more defined"
        CircleColor.Yellow => Color.FromArgb(250, 204, 21), // no agent or subagent running, only background commands
        CircleColor.Green => Color.FromArgb(34, 197, 94), // more saturated than the old (50,205,50) lime — feedback: "more defined"
        CircleColor.Blue => Color.FromArgb(30, 144, 255),
        CircleColor.Purple => Color.FromArgb(147, 51, 234), // was Color.MediumPurple (147,112,219) — feedback: "make the purple color stronger"
        CircleColor.Red => Color.FromArgb(220, 20, 60),
        _ => Color.FromArgb(128, 128, 128), // grey
    };

    // ---- Settings window and right-click menu (2026-11-08, so a new user can launch the
    // app's settings, install the hooks and set auto start without editing files). There is
    // no tray icon, so right-clicking a circle opens it. A second launch of the exe posts
    // ShowSettingsMessage to this window instead of starting another copy (Program), and the
    // window opens on its own when the hooks aren't installed (Run).
    //
    // BUG found live, 2026-11-08 (right-clicking a circle just flashed a white box that
    // disappeared immediately): the first version showed a TrackPopupMenu menu owned by
    // this hidden controller window. A menu closes as soon as its owner isn't the foreground
    // window, and the circles live inside Explorer's taskbar, which is what the click makes
    // foreground. The menu only held "Settings" and "Quit", and Quit is in the window, so
    // right-click now opens the window directly: a window doesn't close when focus moves.

    /// <summary>Registered so a second instance can ask this one to show its settings.</summary>
    public static readonly uint ShowSettingsMessage = Win32.RegisterWindowMessage("ClaudeTaskbar.ShowSettings");

    /// <summary>The controller window's class name, for a second instance to find it.</summary>
    public const string ControllerClassName = ClassName;

    /// <summary>Registered so the settings app (through <c>--quit</c>, see Cli) can end this
    /// copy: "Terminate Claude Taskbar".</summary>
    public static readonly uint QuitMessage = Win32.RegisterWindowMessage("ClaudeTaskbar.Quit");

    /// <summary>The settings window is its own Flutter app (settings_app/), deployed in the
    /// settings folder beside this exe. Its text is crisp where GDI+ and WPF
    /// text were not.</summary>
    private static readonly string SettingsExe =
        Path.Combine(AppContext.BaseDirectory, "settings", "ClaudeTaskbarSettings.exe");

    private Process? _settings;

    private void ShowSettings()
    {
        _tooltip.Hide();
        if (_settings is { HasExited: false })
        {
            RaiseSettings(); // already open: bring it forward instead of opening another
            return;
        }
        _settings?.Dispose();
        _settings = null;
        if (!File.Exists(SettingsExe))
        {
            DiagLog.Write($"settings: {SettingsExe} is missing");
            return;
        }
        try
        {
            // The app calls this exe back (Cli) for everything it changes; its path is given
            // so a development build of either side finds the other.
            var start = new ProcessStartInfo(SettingsExe) { UseShellExecute = false };
            start.ArgumentList.Add("--host");
            start.ArgumentList.Add(Environment.ProcessPath!);
            _settings = Process.Start(start);
        }
        catch (Exception ex)
        {
            DiagLog.Write($"settings: couldn't start {SettingsExe}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Brings the open settings window to the front. Windows may refuse this process
    /// foreground rights (it goes by who received the last input, and a click on a circle
    /// goes to Explorer's taskbar), so the window is also raised in the z-order by making it
    /// topmost and normal again, which needs no such rights.</summary>
    private void RaiseSettings()
    {
        _settings!.Refresh();
        var hwnd = _settings.MainWindowHandle;
        if (hwnd == IntPtr.Zero) return; // still starting: it shows itself in front
        if (Win32.IsIconic(hwnd)) Win32.ShowWindow(hwnd, Win32.SW_RESTORE);
        const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0, flags);
        Win32.SetWindowPos(hwnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0, flags);
        Win32.SetForegroundWindow(hwnd);
    }

    private void OnMouseMove(IntPtr hWnd, IntPtr lParam)
    {
        if (!_tracking)
        {
            var tme = new Win32.TRACKMOUSEEVENT
            {
                cbSize = (uint)Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                dwFlags = Win32.TME_LEAVE,
                hwndTrack = hWnd,
            };
            Win32.TrackMouseEvent(ref tme);
            _tracking = true;
        }

        var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        var index = HitTest(x);
        if (index == _hoveredIndex) return;
        _hoveredIndex = index;

        if (index is not { } i || i < 0 || i >= _slots.Count)
        {
            _tooltip.Hide();
            return;
        }

        var lines = BuildTooltipLines(_slots[i]);
        Win32.GetWindowRect(hWnd, out var winRect);
        var circleCenterX = winRect.Left + SlotCenterX(i);
        _tooltip.ShowAt(circleCenterX, winRect.Top, lines.ToArray(), _lastSampledBackground);
    }

    private static List<string> BuildTooltipLines(CircleSlot slot)
    {
        if (slot.Kind == SlotKind.Grey)
        {
            // Real user-given desktop name (Task View's own "GetName") when the desktop was
            // ever renamed; a never-renamed desktop's name is genuinely empty (Windows computes
            // the "Desktop N" label shown in Task View at render time, it isn't stored per-
            // desktop — confirmed live), so that's the only case using a generic label.
            var label = string.IsNullOrEmpty(slot.DesktopName) ? "Empty desktop" : slot.DesktopName;
            return new List<string> { label, "No Claude Code session" };
        }

        var lines = BuildSessionTooltipLines(slot.Session!);
        if (slot.CapsuleGroupSize is { } n and > 1)
        {
            lines.Add($"Shares this desktop with {n - 1} other session{(n - 1 == 1 ? "" : "s")}");
        }
        return lines;
    }

    private static List<string> BuildSessionTooltipLines(SessionState s)
    {
        // The tab's own title (the chat's name) first: with one window per desktop the
        // project name just repeats the desktop's. Project name only when VS Code has no
        // saved title for this session.
        var lines = new List<string> { string.IsNullOrEmpty(s.TabTitle) ? s.ProjectName : s.TabTitle, s.StateLabel };
        var duration = TaskStartReader.GetRunningDuration(s.SessionId);
        if (duration.HasValue) lines.Add($"Running {FormatDuration(duration.Value)}");
        // From Claude Code's own subagent transcripts (SubagentTracker), not the hook's
        // start/stop counter, which was never verified and doesn't drive the colour.
        if (s.IsSeen) lines.Add("Marked as seen");
        if (s.RunningSubagents > 0) lines.Add($"{s.RunningSubagents} subagent{(s.RunningSubagents == 1 ? "" : "s")} running");
        if (s.RunningBackgroundCommands > 0) lines.Add($"{s.RunningBackgroundCommands} background command{(s.RunningBackgroundCommands == 1 ? "" : "s")} running");
        // Raw token count, not a "% of quota" bar — see TranscriptTokenReader's
        // header comment for why a real progress bar isn't honestly buildable here.
        var tokens = TranscriptTokenReader.GetTotalTokens(s.TranscriptPath);
        if (tokens.HasValue) lines.Add($"{TranscriptTokenReader.Format(tokens.Value)} tokens used");
        return lines;
    }

    /// <summary>Screen-window-local x-coordinate of a slot's center — every slot is the
    /// same fixed width/Gap apart regardless of capsule membership, so this is just the
    /// uniform grid math, mirroring Draw/HitTest exactly.</summary>
    private int SlotCenterX(int slotIndex)
    {
        var totalWidth = _slots.Count * CircleDiameter + Math.Max(0, _slots.Count - 1) * Gap;
        var cursorX = Math.Max(Padding, (_currentWidth - totalWidth) / 2);
        return cursorX + slotIndex * (CircleDiameter + Gap) + CircleDiameter / 2;
    }

    /// <summary>Whether this slot's own circle is drawn with the white ring: the focused
    /// member of a capsule, or a lone circle on the current desktop.</summary>
    private static bool HasRing(CircleSlot slot) =>
        slot.CapsuleGroupSize.HasValue ? slot.IsCapsuleFocusedMember : slot.IsCurrentDesktop;

    /// <summary>"Mark as seen" (2026-09-29): double-clicking a finished circle greys it like
    /// an empty desktop, and double-clicking it again reverses that. Any session circle can
    /// be toggled while it's finished (green/blue/yellow) or already marked. It first also
    /// required the circle to carry the ring; that requirement was dropped 2026-11-01, so
    /// any circle can be marked without switching to it first. The automatic revert on
    /// orange/red (and purple) is in SessionStore.ApplySeen. The first click of the double-click has already run OnClick, so
    /// an unringed circle also switches to its tab, and to its desktop if that's another
    /// one.</summary>
    private void OnDoubleClick(IntPtr lParam)
    {
        var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        if (HitTest(x) is not { } index || index < 0 || index >= _slots.Count)
        {
            DiagLog.Write($"double-click: ignored, no circle at x={x} ({Environment.TickCount64})");
            return;
        }
        var slot = _slots[index];
        if (slot.Kind != SlotKind.Session)
        {
            DiagLog.Write($"double-click: ignored, slot {index} is an empty desktop ({Environment.TickCount64})");
            return;
        }

        // Yellow counts as finished too (2026-11-01): the agent is done, only background
        // commands remain, and a never-ending one shouldn't make the circle impossible to grey.
        var s = slot.Session!;
        StopFlash(s.SessionId);
        if (s.IsSeen) SeenSessions.Set(s.SessionId, false);
        else if (s.BaseColor is CircleColor.Green or CircleColor.Blue or CircleColor.Yellow) SeenSessions.Set(s.SessionId, true);
        else
        {
            DiagLog.Write($"double-click: ignored, {s.SessionId[..8]} is {s.BaseColor} ({Environment.TickCount64})");
            return;
        }
        DiagLog.Write($"double-click: {s.SessionId[..8]} seen={!s.IsSeen} ({Environment.TickCount64})");

        _hoveredIndex = null; // tooltip text changes; rebuild it on the next mouse move
        _tooltip.Hide();
        RefreshSessions();
        Reflow();
    }

    /// <summary>Clicking a plain session circle switches to its window — this applies
    /// identically whether or not it's part of a capsule, since every slot is still exactly
    /// one circle. Clicking a grey filler circle switches directly to that empty desktop
    /// (TrySwitchToDesktopId — there's no window to focus, so the documented
    /// SetForegroundWindow approach used for session circles doesn't apply here).</summary>
    private void OnClick(IntPtr lParam)
    {
        var x = unchecked((short)(lParam.ToInt64() & 0xFFFF));
        if (HitTest(x) is not { } index || index < 0 || index >= _slots.Count) return;
        var slot = _slots[index];

        if (slot.Kind == SlotKind.Grey)
        {
            if (slot.DesktopId.HasValue) VirtualDesktopInterop.TrySwitchToDesktopId(slot.DesktopId.Value);
            return;
        }

        var session = slot.Session!;
        StopFlash(session.SessionId);
        var hwnd = WindowFor(session);
        if (!hwnd.HasValue || !VirtualDesktopInterop.TrySwitchToWindow(hwnd.Value)) return;

        // Then focus the session's own tab inside that window. Only for sessions
        // VS Code lists as editor tabs: a sidebar session would get a duplicate editor copy
        // (anthropics/claude-code#85726). A brand-new tab may not be in VS Code's saved list
        // yet (it's saved about every 60s), and then this click just focuses the window.
        if (!session.IsOpenAsTab) return;

        // Preferred: route the link to the session's own VS Code window by its internal id,
        // which takes focus timing out of it entirely (VsCodeWindowIds). If no id is found,
        // fall back to waiting until the window has settled in front (below).
        if (VsCodeWindowIds.Find(session.SessionId, session.CanonicalCwd) is { } windowId)
        {
            ClaudeTabLink.Open(session.SessionId, windowId);
        }
        else
        {
            DiagLog.Write($"tab link: no VS Code window id for {session.SessionId[..8]} — waiting for focus instead");
            QueueTabLink(session.SessionId, hwnd.Value);
        }
    }

    // BUG found live, 2026-10-18 (clicking a circle sometimes created new tabs in the
    // current VS Code window instead of switching; one window collected five empty
    // "Claude Code" tabs). VS Code routes a vscode:// link to the window it last
    // saw focused. When the click also switched virtual desktops, the link was sent right
    // after SetForegroundWindow, before VS Code had registered the new window's focus. It
    // went to the previously focused window, which doesn't have that session, so the
    // extension started a fresh conversation there. This was the risk flagged when the tab
    // link was first researched ("the focus event has to win the race").
    //
    // FALLBACK ONLY since the same day: the primary path routes by VS Code window id (see
    // OnClick and VsCodeWindowIds). This is used only when no id can be found.
    // The link is held until the target window has been the foreground window, on the
    // current desktop, for TabLinkSettle. If that never happens within TabLinkTimeout, it is
    // dropped (logged), and the click just focuses the window. Never a stray tab.
    private static readonly IntPtr TabLinkTimerId = new(2);
    private static readonly TimeSpan TabLinkSettle = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TabLinkTimeout = TimeSpan.FromSeconds(3);
    private string? _linkSessionId;
    private IntPtr _linkHwnd;
    private DateTime _linkDeadlineUtc;
    private DateTime? _linkSettledSinceUtc;

    private void QueueTabLink(string sessionId, IntPtr hwnd)
    {
        _linkSessionId = sessionId;
        _linkHwnd = hwnd;
        _linkDeadlineUtc = DateTime.UtcNow + TabLinkTimeout;
        _linkSettledSinceUtc = null;
        Win32.SetTimer(_hwnd, TabLinkTimerId, 100, IntPtr.Zero); // re-arming replaces a pending one
    }

    private void TickPendingTabLink()
    {
        if (_linkSessionId == null) { Win32.KillTimer(_hwnd, TabLinkTimerId); return; }

        var now = DateTime.UtcNow;
        var inFront = Win32.GetForegroundWindow() == _linkHwnd
            && VirtualDesktopInterop.IsWindowOnCurrentDesktop(_linkHwnd) == true;
        if (!inFront) _linkSettledSinceUtc = null;
        else _linkSettledSinceUtc ??= now;

        if (_linkSettledSinceUtc is { } since && now - since >= TabLinkSettle)
        {
            ClaudeTabLink.Open(_linkSessionId);
        }
        else if (now < _linkDeadlineUtc)
        {
            return; // keep waiting
        }
        else
        {
            DiagLog.Write($"tab link: skipped for {_linkSessionId[..8]} — its window never settled in front within {TabLinkTimeout.TotalSeconds:0}s");
        }
        _linkSessionId = null;
        Win32.KillTimer(_hwnd, TabLinkTimerId);
    }

    // Sort/border stability cache: remembers the last successfully-resolved
    // (hwnd, desktop id) per project name, keyed on ProjectName since that's the stable
    // identity across polls (session ids change for placeholders/real-session transitions,
    // project name doesn't). BUG found live, 2026-09-27 ("the order is buggy, it keeps
    // moving around"): window/desktop resolution can occasionally fail for one tick even
    // after the real root cause (see VsCodeWindowFinder) was fixed — any polled,
    // occasionally-flaky signal benefits from "stick with the last known-good value rather
    // than snapping to a worst-case default on a single bad read". A session only actually
    // loses its position if resolution keeps failing across ticks, not on one blip.
    private readonly Dictionary<string, (IntPtr Hwnd, Guid DesktopId)> _desktopCache = new();

    /// <summary>The session's VS Code window: the one SessionStore matched it to via VS
    /// Code's own tab list/workspace storage, else title matching on the project name.</summary>
    private static IntPtr? WindowFor(SessionState s) =>
        s.WindowHandle ?? VsCodeWindowFinder.FindWindowForProject(s.ProjectName);

    /// <summary>Builds the row of circle "slots" from the raw session list —
    /// every slot is exactly one circle: a real session (alone on its desktop, or one
    /// member of a capsule group sharing a desktop with others), or a grey filler for any
    /// virtual desktop with zero sessions. Always interleaved in true left-to-right desktop
    /// order end-to-end. Falls back to the flat, un-ordered, no-filler behavior (the
    /// original shape) if the undocumented desktop-enumeration path fails entirely —
    /// same fallback discipline as every other undocumented-API use in this project.</summary>
    private List<CircleSlot> BuildSlots(List<SessionState> sessions)
    {
        var (order, currentId) = VirtualDesktopInterop.GetDesktopLayout();
        if (order == null)
        {
            // Ordering/enumeration unavailable this tick — no grey fillers or capsule
            // grouping are possible without the same enumerated desktop list, but the ring
            // indicator can still work via the separate, always-documented
            // IsWindowOnCurrentDesktop call.
            var flat = sessions.Select(s =>
            {
                var hwnd = WindowFor(s);
                var isCurrent = hwnd.HasValue && VirtualDesktopInterop.IsWindowOnCurrentDesktop(hwnd.Value) == true;
                return new CircleSlot { Kind = SlotKind.Session, Session = s, IsCurrentDesktop = isCurrent };
            }).ToList();
            if (flat.Count == 0) flat.Add(new CircleSlot { Kind = SlotKind.Grey, IsCurrentDesktop = true });
            return flat;
        }

        var resolved = new Dictionary<string, (IntPtr? Hwnd, Guid? DesktopId)>();
        foreach (var s in sessions)
        {
            var hwnd = WindowFor(s);
            var desktopId = hwnd.HasValue ? VirtualDesktopInterop.GetWindowDesktopId(hwnd.Value) : null;

            if (hwnd.HasValue && desktopId.HasValue)
            {
                _desktopCache[s.ProjectName] = (hwnd.Value, desktopId.Value);
            }
            // Only while that window still exists: a closed window's cached desktop used to
            // keep its session on the old desktop (see SessionStore.ReadAllWithSeeding).
            else if (_desktopCache.TryGetValue(s.ProjectName, out var cached) && Win32.IsWindow(cached.Hwnd))
            {
                (hwnd, desktopId) = (cached.Hwnd, cached.DesktopId);
            }

            resolved[s.SessionId] = (hwnd, desktopId);
        }

        var grouped = sessions
            .Where(s => resolved[s.SessionId].DesktopId.HasValue)
            .GroupBy(s => resolved[s.SessionId].DesktopId!.Value)
            .ToDictionary(gr => gr.Key, gr => gr.ToList());

        var slots = new List<CircleSlot>();
        foreach (var (desktopId, desktopName) in order)
        {
            var isCurrent = currentId.HasValue && desktopId == currentId.Value;
            if (!grouped.TryGetValue(desktopId, out var group) || group.Count == 0)
            {
                slots.Add(new CircleSlot { Kind = SlotKind.Grey, DesktopId = desktopId, DesktopName = desktopName, IsCurrentDesktop = isCurrent });
                continue;
            }

            if (group.Count == 1)
            {
                slots.Add(new CircleSlot { Kind = SlotKind.Session, DesktopId = desktopId, Session = group[0], IsCurrentDesktop = isCurrent });
                continue;
            }

            // 2+ sessions share this desktop — each still becomes its own ordinary,
            // normal-width Session slot (see class remarks: capsule membership never
            // changes a circle's own position/spacing), just tagged with which group it's
            // in so Draw knows to paint one shared pill behind the whole contiguous run.
            //
            // Order and focus (2026-09-27): VS Code's real tab order/focus, read from its
            // own workspaceStorage state.vscdb — see VsCodeEditorStateReader's header for
            // how this was found and verified. Falls back to ProjectName ordering (stable,
            // just not necessarily the real visual order) and GetForegroundWindow-based
            // focus (can't distinguish sessions sharing one VS Code window) if the vscdb
            // read fails for any reason — never guessed, never crashes.
            var workspacePath = group[0].CanonicalCwd ?? group[0].Cwd;
            var tabInfo = VsCodeEditorStateReader.TryGetTabInfoForFolder(workspacePath);

            List<SessionState> ordered;
            if (tabInfo != null)
            {
                var byId = group.ToDictionary(s => s.SessionId);
                var byOrder = tabInfo.OrderedSessionIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                // Any group member the vscdb didn't account for (sidebar session, multi-
                // root workspace, etc. — see VsCodeEditorStateReader's caveats) is appended
                // rather than dropped, so it's never silently hidden from the capsule.
                var missing = group.Where(s => !tabInfo.OrderedSessionIds.Contains(s.SessionId));
                ordered = byOrder.Concat(missing).ToList();
            }
            else
            {
                ordered = group.OrderBy(s => s.ProjectName, StringComparer.OrdinalIgnoreCase).ToList();
            }

            var focusedIndex = FindFocusedMember(ordered, tabInfo, resolved);
            var hasFocusedMember = isCurrent && focusedIndex >= 0;
            for (var i = 0; i < ordered.Count; i++)
            {
                slots.Add(new CircleSlot
                {
                    Kind = SlotKind.Session,
                    DesktopId = desktopId,
                    Session = ordered[i],
                    IsCurrentDesktop = isCurrent,
                    CapsuleGroupSize = ordered.Count,
                    CapsuleIndexInGroup = i,
                    CapsuleGroupHasFocusedMember = hasFocusedMember,
                    IsCapsuleFocusedMember = hasFocusedMember && i == focusedIndex,
                });
            }
        }

        // Sessions this app couldn't resolve to any desktop at all (brand new, VS Code
        // window not found yet, cache miss) — appended at the end, plain circles, no ring
        // possible since we don't know which desktop (if any) they're on. Not interleaved
        // with the real desktop-ordered slots above since there's nothing to sort it by.
        var unresolved = sessions.Where(s => !resolved[s.SessionId].DesktopId.HasValue);
        slots.AddRange(unresolved.Select(s => new CircleSlot { Kind = SlotKind.Session, Session = s }));

        return slots;
    }

    private bool _hasStableBackground;
    private Color _backgroundCandidate;
    private DateTime _backgroundCandidateSinceUtc;
    private static readonly TimeSpan BackgroundSettleTime = TimeSpan.FromSeconds(1);

    /// <summary>Adopts a new sampled taskbar color only once it has held for
    /// BackgroundSettleTime. Found live, 2026-09-28 (when switching to a new desktop the
    /// capsule background turned black for a brief second): the live pixel sample
    /// briefly reads near-black during the desktop-switch animation, and the capsule fill,
    /// tooltip and border mask are all derived from it. A real change (a theme switch) is
    /// adopted a second late. Every value used is a real sample, just the last one that
    /// held steady. Short-lived samples that never settle are logged so the cause can be
    /// checked.</summary>
    private Color StableBackground(Color sample)
    {
        var now = DateTime.UtcNow;
        if (!_hasStableBackground)
        {
            _hasStableBackground = true;
            _backgroundCandidate = sample;
            _backgroundCandidateSinceUtc = now;
            return sample;
        }
        if (sample.ToArgb() != _backgroundCandidate.ToArgb())
        {
            if (_backgroundCandidate.ToArgb() != _lastSampledBackground.ToArgb())
            {
                DiagLog.Write($"background: ignored short-lived sample {_backgroundCandidate.R},{_backgroundCandidate.G},{_backgroundCandidate.B} (kept {_lastSampledBackground.R},{_lastSampledBackground.G},{_lastSampledBackground.B})");
            }
            _backgroundCandidate = sample;
            _backgroundCandidateSinceUtc = now;
        }
        return now - _backgroundCandidateSinceUtc >= BackgroundSettleTime ? _backgroundCandidate : _lastSampledBackground;
    }

    /// <summary>Which capsule member is the focused tab, or -1 (ring the whole capsule).
    ///
    /// BUG found live, 2026-09-28 (the ring didn't follow tab switches, only clicks on a
    /// circle and desktop changes, and after a click it landed on the previously focused
    /// tab): focus came from state.vscdb,
    /// which VS Code only saves about once a minute, so re-checking every tick changed
    /// nothing. The live source is the VS Code window title, which changes the instant a tab
    /// is activated: `[● ]<active editor> - <workspace> - Visual Studio Code`. Its first part
    /// is compared against each member's saved tab title.
    ///
    /// Order of trust:
    /// 1. The title of the group's frontmost window (see below) is readable: a member whose
    ///    tab title leads it is focused. If none does (a file is active, a tab was renamed
    ///    since the last save, or two tabs share a title), no specific member is focused. The
    ///    stale saved focus is not substituted.
    /// 2. No title: VS Code's saved focus.
    /// 3. Neither: the foreground window, but only when it holds exactly one member. With
    ///    several members in one window, the window can't say which tab is active.
    ///    (Previously this picked the first member, which also put the ring on the wrong
    ///    circle.)
    ///
    /// BUG found live, 2026-11-01 (in a two-circle capsule on the current desktop, a blue
    /// circle never got the ring, so the double-click, which then required the ring, did
    /// nothing; diag.log: "slot 3 kind=Session ring=False"): with two VS Code windows on one
    /// desktop, every window's title matches its own active tab, so the first window in the list always won and the ring never left the
    /// first member. Windows are now checked front to back by z-order, and the frontmost
    /// decides. That also covers the case where neither window is the foreground one.</summary>
    private static int FindFocusedMember(List<SessionState> ordered, VsCodeEditorStateReader.TabInfo? tabInfo,
        Dictionary<string, (IntPtr? Hwnd, Guid? DesktopId)> resolved)
    {
        var frontToBack = ordered.Where(s => resolved[s.SessionId].Hwnd.HasValue)
            .GroupBy(s => resolved[s.SessionId].Hwnd!.Value)
            .OrderBy(w => WindowsAbove(w.Key));
        foreach (var window in frontToBack)
        {
            var sb = new System.Text.StringBuilder(512);
            if (Win32.GetWindowText(window.Key, sb, sb.Capacity) <= 0) continue;
            var title = sb.ToString();
            if (title.StartsWith("● ", StringComparison.Ordinal)) title = title[2..]; // VS Code's unsaved-changes marker

            var matches = window.Where(s => !string.IsNullOrEmpty(s.TabTitle)
                && title.StartsWith(s.TabTitle + " - ", StringComparison.Ordinal)).ToList();
            return matches.Count == 1 ? ordered.IndexOf(matches[0]) : -1;
        }

        if (tabInfo?.FocusedSessionId is { } savedFocus)
        {
            var i = ordered.FindIndex(s => s.SessionId == savedFocus);
            if (i >= 0) return i;
        }

        var foreground = Win32.GetForegroundWindow();
        var inForeground = ordered.Where(s => resolved[s.SessionId].Hwnd == foreground).ToList();
        return inForeground.Count == 1 ? ordered.IndexOf(inForeground[0]) : -1;
    }

    /// <summary>The window's z-order position: how many top-level windows are above it.</summary>
    private static int WindowsAbove(IntPtr hwnd)
    {
        var count = 0;
        for (var w = Win32.GetWindow(hwnd, Win32.GW_HWNDPREV); w != IntPtr.Zero; w = Win32.GetWindow(w, Win32.GW_HWNDPREV)) count++;
        return count;
    }

    // Fallback only: Windows 11's default dark-theme taskbar color, used solely if the
    // live pixel sample below fails (taskbar not found, or GetPixel reports CLR_INVALID —
    // e.g. mid Explorer-restart). Never relied on when a real sample is available.
    private static readonly Color FallbackTaskbarColor = Color.FromArgb(32, 32, 32);

    /// <summary>Samples an actual live taskbar-background pixel — from the small gap this
    /// app deliberately leaves between its own window and whatever it anchored to
    /// (TrafficMonitor or the tray) — rather than assuming a color, so the tooltip
    /// background always matches the taskbar's real current color/theme (dark, light, or
    /// any future Windows redesign) without hardcoding or guessing it.</summary>
    private static Color SampleTaskbarBackground(int ourWindowRight)
    {
        var taskbarRect = GetTaskbarRect();
        if (taskbarRect is not { } tb) return FallbackTaskbarColor;

        var sampleX = ourWindowRight + TrayMargin / 2;
        var sampleY = tb.Top + (tb.Bottom - tb.Top) / 2;

        var hdc = Win32.GetDC(IntPtr.Zero);
        if (hdc == IntPtr.Zero) return FallbackTaskbarColor;
        var colorRef = Win32.GetPixel(hdc, sampleX, sampleY);
        Win32.ReleaseDC(IntPtr.Zero, hdc);
        if (colorRef == Win32.CLR_INVALID) return FallbackTaskbarColor;

        // COLORREF is 0x00BBGGRR, not the 0x00RRGGBB most APIs expect.
        var r = (int)(colorRef & 0xFF);
        var g = (int)((colorRef >> 8) & 0xFF);
        var b = (int)((colorRef >> 16) & 0xFF);
        return Color.FromArgb(r, g, b);
    }

    /// <summary>Which slot sits under a given window-relative x, or null if between/outside
    /// slots. Every slot is the same fixed width and Gap apart — mirrors the layout math in
    /// Draw exactly, capsule membership doesn't change this.</summary>
    private int? HitTest(int localX)
    {
        var count = Math.Max(1, _slots.Count);
        var totalWidth = count * CircleDiameter + (count - 1) * Gap;
        var startX = Math.Max(Padding, (_currentWidth - totalWidth) / 2);
        if (localX < startX) return null;
        var offset = localX - startX;
        var slot = CircleDiameter + Gap;
        var index = offset / slot;
        if (index >= count) return null;
        return offset % slot <= CircleDiameter ? index : null; // strictly over the circle, not the gap
    }

    private static string FormatDuration(TimeSpan d) =>
        d.TotalHours >= 1 ? $"{(int)d.TotalHours}h {d.Minutes}m" : $"{d.Minutes}m";

    private static int ComputeWidth(List<CircleSlot> slots)
    {
        var count = Math.Max(1, slots.Count); // at least the grey placeholder
        return Padding * 2 + count * CircleDiameter + (count - 1) * Gap;
    }

    /// <summary>Screen coordinates for the window's top-left corner: right-anchored to
    /// whatever sits immediately left of the tray (TrafficMonitor's own window, if
    /// running — detected directly rather than assumed, so this doesn't silently
    /// collide with it), vertically centered in the taskbar's own rect.</summary>
    private static (int x, int y) ComputePosition(int width)
    {
        if (GetTaskbarRect() is not { } taskbarRect) return (0, 0); // taskbar not found (Explorer restarting?) — next timer tick retries
        var taskbar = Win32.FindWindow("Shell_TrayWnd", null);

        var taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;
        var y = taskbarRect.Top + Math.Max(0, (taskbarHeight - WindowHeight) / 2) + VerticalNudge;

        // TrafficMonitor's taskbar-window (github.com/zhongyang219/TrafficMonitor) is a
        // dialog-class (#32770) window titled "TrafficMonitorTaskbarWindow", sitting
        // immediately left of TrayNotifyWnd. If it's running, anchor to ITS actual left
        // edge instead of TrayNotifyWnd's — anchoring both windows to TrayNotifyWnd
        // independently is what caused them to overlap in the first live test.
        var trafficMonitor = Win32.FindWindowEx(taskbar, IntPtr.Zero, null, "TrafficMonitorTaskbarWindow");
        if (trafficMonitor != IntPtr.Zero)
        {
            Win32.GetWindowRect(trafficMonitor, out var tmRect);
            return (tmRect.Left - width - TrayMargin, y);
        }

        var trayNotify = Win32.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (trayNotify == IntPtr.Zero)
        {
            // Fall back to the taskbar's own right edge if TrayNotifyWnd can't be found.
            var fallbackX = taskbarRect.Right - width - TrayMargin;
            return (fallbackX, y);
        }

        Win32.GetWindowRect(trayNotify, out var notifyRect);
        var x = notifyRect.Left - width - TrayMargin;
        return (x, y);
    }

    private static Win32.RECT? GetTaskbarRect()
    {
        var taskbar = Win32.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return null;
        Win32.GetWindowRect(taskbar, out var rect);
        return rect;
    }


    private enum SlotKind { Session, Grey }

    /// <summary>One circle in the taskbar row — always exactly one circle's worth of
    /// width/position, whether or not it's grouped into a capsule. See BuildSlots.</summary>
    private sealed class CircleSlot
    {
        public required SlotKind Kind { get; init; }
        public Guid? DesktopId { get; init; } // null only for the unresolved-session bucket
        public string? DesktopName { get; init; } // Kind == Grey — real user-given desktop name, empty/null if never renamed
        public SessionState? Session { get; init; } // Kind == Session

        // Capsule membership (null CapsuleGroupSize = a lone circle, not part of any
        // capsule — the normal case). When set, this circle is one of CapsuleGroupSize
        // sessions sharing one desktop; Draw paints one shared pill background behind the
        // whole contiguous run when it reaches CapsuleIndexInGroup == 0.
        public int? CapsuleGroupSize { get; init; }
        public int CapsuleIndexInGroup { get; init; }
        public bool CapsuleGroupHasFocusedMember { get; init; } // whether any member is the specifically-ringed one
        public bool IsCapsuleFocusedMember { get; init; } // whether THIS circle specifically is ringed

        public bool IsCurrentDesktop { get; init; }
    }
}
