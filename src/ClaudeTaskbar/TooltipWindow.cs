using System.Drawing;
using System.Runtime.InteropServices;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

/// <summary>
/// Hover detail: a small borderless popup showing a few lines of text about
/// whichever circle the mouse is currently over (project name, state, running duration).
/// Created hidden, shown/repositioned/re-texted on demand by TaskbarWindow.
///
/// Rendering: GDI+ into a DIB section via LayeredBitmapRenderer + UpdateLayeredWindow,
/// same as TaskbarWindow — see that file's header comment for why (replaces WM_PAINT +
/// LWA_COLORKEY, fixes flicker and pixelated/fringed edges). Also per feedback: bigger
/// font (was unreadably small at -13). The background went from a solid dark panel to
/// fully transparent as an experiment, then to the live-sampled taskbar colour (see
/// ShowAt) when transparent didn't read well.
/// </summary>
public sealed class TooltipWindow
{
    private const string ClassName = "ClaudeTaskbarTooltipWndClass";
    private const int PaddingX = 10;
    private const int PaddingY = 8;

    // GraphicsUnit.Pixel makes this a literal physical-pixel em size regardless of DPI —
    // the process is per-monitor-DPI-aware and all our coordinates are already physical,
    // so this stays consistent with everything else. History: -13 (GDI CreateFont, a
    // different unit) was far too small -> 22px too big -> 15px -> 18px, settled in use.
    private static readonly Font TextFont = new("Segoe UI", 18f, FontStyle.Regular, GraphicsUnit.Pixel);
    private static readonly Bitmap MeasureBitmap = new(1, 1);
    private static readonly Graphics MeasureGraphics = Graphics.FromImage(MeasureBitmap);
    private static readonly int LineHeight = (int)Math.Ceiling(TextFont.GetHeight(MeasureGraphics)) + 2;

    private readonly LayeredBitmapRenderer _renderer = new();
    private IntPtr _hwnd;
    private string[] _lines = Array.Empty<string>();
    private Color _background = Color.FromArgb(32, 32, 32); // overwritten on first ShowAt
    private bool _visible;
    private Win32.WndProc? _wndProcDelegate; // kept alive: prevents the marshaled callback from being GC'd

    public void EnsureCreated()
    {
        if (_hwnd != IntPtr.Zero) return;

        _wndProcDelegate = WndProc;
        var wc = new Win32.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<Win32.WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hbrBackground = IntPtr.Zero,
            hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW), // see Win32.cs
            lpszClassName = ClassName,
        };
        Win32.RegisterClassEx(ref wc);

        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOPMOST | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            ClassName, "", Win32.WS_POPUP, 0, 0, 10, 10,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        // See TaskbarWindow.Run for why SetLayeredWindowAttributes is deliberately NOT
        // called here — WS_EX_LAYERED alone is enough for UpdateLayeredWindow.
        var exStyle = Win32.GetWindowLong(_hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(_hwnd, Win32.GWL_EXSTYLE, exStyle | (int)Win32.WS_EX_LAYERED);
    }

    /// <summary>Show (or reposition/re-text, if already visible) above the given anchor
    /// point (typically the hovered circle's top-center, in screen coordinates).
    /// <paramref name="background"/> is the taskbar's own live-sampled background color
    /// (see TaskbarWindow.SampleTaskbarBackground) — a fully transparent background was
    /// tried per earlier feedback and didn't read well; matching the real taskbar color
    /// instead keeps the tooltip readable while still looking like part of the taskbar
    /// rather than a mismatched box.</summary>
    public void ShowAt(int anchorX, int anchorTopY, string[] lines, Color background)
    {
        EnsureCreated();
        _lines = lines;
        _background = background;

        var width = MeasureWidth(lines) + PaddingX * 2;
        var height = lines.Length * LineHeight + PaddingY * 2;
        var x = anchorX - width / 2;
        var y = anchorTopY - height - 6; // just above the anchor, small gap

        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, x, y, width, height,
            Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
        _visible = true;
        Draw(width, height, x, y);
    }

    public void Hide()
    {
        _visible = false;
        if (_hwnd != IntPtr.Zero) Win32.SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, Win32.SWP_HIDEWINDOW);
    }

    /// <summary>BUG (found live, 2026-09-27): TaskbarWindow's circles and
    /// TaskbarBorderMask both re-assert HWND_TOPMOST on every 400ms timer tick (needed —
    /// see the earlier topmost-loss bug both of them had), but Windows' topmost band has
    /// no real sub-ordering: whichever window most recently called SetWindowPos(
    /// HWND_TOPMOST) ends up above every OTHER topmost window, this tooltip included.
    /// Observed live: the tooltip showed fine initially, then the border mask's stripe
    /// visibly cut through it a few hundred ms later, once the next timer tick re-topmost'd
    /// the mask after the tooltip. Fix: TaskbarWindow calls this on every tick too, after
    /// re-asserting its own and the border mask's topmost position, so the tooltip (when
    /// actually visible) always reclaims the very top of the stack last. (The circles are
    /// now embedded in the taskbar and the border mask is gone, so this now only guards
    /// against other apps' topmost windows.)</summary>
    public void ReassertTopmostIfVisible()
    {
        if (!_visible || _hwnd == IntPtr.Zero) return;
        Win32.SetWindowPos(_hwnd, Win32.HWND_TOPMOST, 0, 0, 0, 0,
            Win32.SWP_NOACTIVATE | Win32.SWP_NOMOVE | Win32.SWP_NOSIZE);
    }

    /// <summary>Real text measurement (GDI+ MeasureString) rather than the earlier
    /// avg-glyph-width heuristic — needed anyway once the font size changed, and more
    /// accurate for the transparent background where a too-narrow box would clip text
    /// with no panel edge to visually mask it.</summary>
    private static int MeasureWidth(string[] lines) =>
        Math.Max(60, (int)Math.Ceiling(lines.Length == 0 ? 0 : lines.Max(l => MeasureGraphics.MeasureString(l, TextFont).Width)));

    private void Draw(int width, int height, int screenX, int screenY)
    {
        using var g = _renderer.BeginDraw(width, height);
        using (var bg = new SolidBrush(_background))
        {
            g.FillRectangle(bg, 0, 0, width, height);
        }
        // Light text on a dark taskbar, dark text on a light one (Windows' light theme
        // samples near-white, where fixed light text could not be read).
        var light = (0.299 * _background.R + 0.587 * _background.G + 0.114 * _background.B) > 140;
        using var brush = new SolidBrush(light ? Color.FromArgb(28, 28, 28) : Color.FromArgb(235, 235, 235));
        for (var i = 0; i < _lines.Length; i++)
        {
            g.DrawString(_lines[i], TextFont, brush, PaddingX, PaddingY + i * LineHeight);
        }
        _renderer.Present(_hwnd, screenX, screenY);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_PAINT:
                // Content is pushed entirely via UpdateLayeredWindow — nothing to draw on
                // this window's own DC, just ack the update region.
                Win32.BeginPaint(hWnd, out var ps);
                Win32.EndPaint(hWnd, ref ps);
                return IntPtr.Zero;
            default:
                return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }
    }
}
