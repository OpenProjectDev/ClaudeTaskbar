using System.Runtime.InteropServices;
using System.Text;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

/// <summary>
/// A per-pixel-alpha layered window embedded in the taskbar exactly the way TrafficMonitor
/// embeds its own (github.com/zhongyang219/TrafficMonitor), so it is drawn as part of
/// Shell_TrayWnd: it stays put while the Start menu is open and through virtual-desktop
/// switches, where a floating HWND_TOPMOST window vanished (on a desktop switch or the Win
/// key, the floating window disappeared while TrafficMonitor's stayed put).
///
/// Found live, 2026-09-28 (with a throwaway probe app): what makes it render is the window being a
/// real DIALOG (#32770, created from a dialog template via CreateDialogIndirectParam), as
/// TrafficMonitor's MFC CDialog is. A plain CreateWindowEx window with every observable
/// property made identical to TrafficMonitor's (style, ex-style, class style, colour key,
/// DPI context, parent, owner, z-order, hidden until parented, even elevation) was parented
/// successfully, received WM_PAINT, reported visible and never appeared on screen; the
/// dialog appeared on the first try. UpdateLayeredWindow per-pixel alpha works on the
/// embedded dialog too, so no colour-key rendering is needed.
///
/// Sequence (TaskBarDlg::OnInitDialog): dialog created hidden with a hidden owner →
/// WS_EX_TOOLWINDOW + WS_EX_LAYERED → SetParent(Shell_TrayWnd), no WS_CHILD → positioned in
/// taskbar-client coordinates → shown only after its first frame is pushed. If SetParent
/// fails, the window works as the old floating topmost window instead and embedding is
/// retried every 10s (TrafficMonitor also retries by re-creating its window). If Explorer
/// restarts, the taskbar and this child die with it; the next Ensure() re-creates it.
/// </summary>
public sealed class TaskbarEmbeddedWindow
{
    /// <summary>Returns true if the message was handled.</summary>
    public delegate bool MessageHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private static readonly TimeSpan EmbedRetryInterval = TimeSpan.FromSeconds(10);

    private readonly IntPtr _owner;
    private readonly string _title;
    private readonly MessageHandler _handler;
    private readonly Win32.DlgProc _dlgProcDelegate; // kept alive: prevents the marshaled callback from being GC'd
    private IntPtr _template;
    private IntPtr _taskbar;
    private DateTime _lastCreateUtc = DateTime.MinValue;
    private bool _shown;
    private bool _wantVisible = true;

    public IntPtr Hwnd { get; private set; }
    public bool IsEmbedded { get; private set; }

    public TaskbarEmbeddedWindow(IntPtr owner, string title, MessageHandler handler)
    {
        _owner = owner;
        _title = title;
        _handler = handler;
        _dlgProcDelegate = DialogProc;
    }

    /// <summary>Call every tick: creates the window, or re-creates it if it was destroyed
    /// (Explorer restart), is no longer parented to the current taskbar, or is floating
    /// and the retry interval has passed.</summary>
    public void Ensure()
    {
        var taskbar = Win32.FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero) return; // Explorer restarting — nothing to anchor to yet

        var alive = Hwnd != IntPtr.Zero && Win32.IsWindow(Hwnd);
        if (alive && IsEmbedded && Win32.GetAncestor(Hwnd, Win32.GA_PARENT) == taskbar)
        {
            if (!IsBelowXamlLayer(taskbar)) return;
            // Found live, 2026-09-28 (after an Explorer restart the circles stayed hidden
            // while TrafficMonitor came back): we re-embedded into the new
            // taskbar before Explorer had created its full-width XAML child, which was then
            // inserted above us and covers us completely — still parented, visible, and
            // positioned correctly, just underneath. A fresh SetParent inserts at the top of
            // the children, the verified-working position, so re-create.
            DiagLog.Write($"embed: '{_title}' is below the taskbar's XAML layer — re-creating");
        }
        else if (alive && !IsEmbedded && DateTime.UtcNow - _lastCreateUtc < EmbedRetryInterval)
        {
            return;
        }

        if (alive) Win32.DestroyWindow(Hwnd);
        Create(taskbar);
    }

    /// <summary>True if the taskbar's XAML content child (DesktopWindowContentBridge — the
    /// full-width surface that draws the modern taskbar) comes before this window in the
    /// taskbar's child z-order, i.e. covers it. Children are enumerated top to bottom.</summary>
    private bool IsBelowXamlLayer(IntPtr taskbar)
    {
        var cls = new StringBuilder(64);
        for (var c = Win32.GetWindow(taskbar, Win32.GW_CHILD); c != IntPtr.Zero; c = Win32.GetWindow(c, Win32.GW_HWNDNEXT))
        {
            if (c == Hwnd) return false;
            cls.Clear();
            Win32.GetClassName(c, cls, cls.Capacity);
            if (cls.ToString() == "Windows.UI.Composition.DesktopWindowContentBridge") return true;
        }
        return false;
    }

    private void Create(IntPtr taskbar)
    {
        _lastCreateUtc = DateTime.UtcNow;
        _shown = false;
        IsEmbedded = false;

        _template = _template != IntPtr.Zero ? _template : BuildTemplate(_title);
        var hwnd = Win32.CreateDialogIndirectParam(Win32.GetModuleHandle(null), _template, _owner, _dlgProcDelegate, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            DiagLog.Write($"embed: CreateDialogIndirectParam failed for '{_title}', error {Marshal.GetLastWin32Error()}");
            Hwnd = IntPtr.Zero;
            return;
        }

        var ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | (int)(Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_LAYERED));

        if (Win32.SetParent(hwnd, taskbar) != IntPtr.Zero)
        {
            IsEmbedded = true;
            _taskbar = taskbar;
        }
        else
        {
            DiagLog.Write($"embed: SetParent failed for '{_title}', error {Marshal.GetLastWin32Error()} — floating topmost until retry");
            ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
            Win32.SetWindowLong(hwnd, Win32.GWL_EXSTYLE, ex | (int)(Win32.WS_EX_TOPMOST | Win32.WS_EX_NOACTIVATE));
        }
        Hwnd = hwnd;
    }

    /// <summary>Moves/resizes to the given SCREEN rectangle. Embedded windows are
    /// positioned in taskbar-client coordinates and never touch z-order (TrafficMonitor has
    /// no z-order calls on its working path); the floating fallback re-asserts topmost.</summary>
    public void Place(int screenX, int screenY, int width, int height)
    {
        if (Hwnd == IntPtr.Zero) return;
        if (IsEmbedded)
        {
            var pt = new Win32.POINT { X = screenX, Y = screenY };
            Win32.ScreenToClient(_taskbar, ref pt);
            Win32.SetWindowPos(Hwnd, IntPtr.Zero, pt.X, pt.Y, width, height, Win32.SWP_NOZORDER | Win32.SWP_NOACTIVATE);
        }
        else
        {
            Win32.SetWindowPos(Hwnd, Win32.HWND_TOPMOST, screenX, screenY, width, height, Win32.SWP_NOACTIVATE);
        }
    }

    /// <summary>Pushes the renderer's frame, and shows the window after its first frame
    /// so it never appears empty or in the wrong place.</summary>
    public void Present(LayeredBitmapRenderer renderer)
    {
        if (Hwnd == IntPtr.Zero) return;
        renderer.PresentInPlace(Hwnd);
        if (!_shown && _wantVisible)
        {
            Win32.ShowWindow(Hwnd, Win32.SW_SHOWNOACTIVATE);
            _shown = true;
        }
    }

    public void SetVisible(bool visible)
    {
        _wantVisible = visible;
        if (Hwnd == IntPtr.Zero) return;
        if (!visible)
        {
            Win32.ShowWindow(Hwnd, Win32.SW_HIDE);
            _shown = false;
        }
        // Showing again happens on the next Present, with a fresh frame.
    }

    private IntPtr DialogProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_INITDIALOG:
                return 1;
            case Win32.WM_DESTROY:
                // Destroyed by us (re-create) or with the taskbar (Explorer restart) —
                // never forwarded: this must not end the app. Ensure() re-creates it.
                if (hWnd == Hwnd) { Hwnd = IntPtr.Zero; IsEmbedded = false; }
                return 0;
            default:
                return _handler(hWnd, msg, wParam, lParam) ? 1 : 0;
        }
    }

    /// <summary>In-memory DLGTEMPLATE with TrafficMonitor's IDD_TASK_BAR_DIALOG styles:
    /// DS_SETFONT | DS_3DLOOK | WS_POPUP | WS_CLIPSIBLINGS | WS_SYSMENU, no WS_VISIBLE, no
    /// controls. Allocated once and kept for the process lifetime.</summary>
    private static IntPtr BuildTemplate(string title)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms, Encoding.Unicode);
        bw.Write(Win32.DS_SETFONT | Win32.DS_3DLOOK | Win32.WS_POPUP | Win32.WS_CLIPSIBLINGS | Win32.WS_SYSMENU);
        bw.Write(Win32.WS_EX_CONTROLPARENT); // TrafficMonitor's live ex-style includes it
        bw.Write((ushort)0);                 // no controls
        bw.Write((short)0); bw.Write((short)0); bw.Write((short)10); bw.Write((short)10); // x y cx cy (DLUs; resized on Place)
        bw.Write((ushort)0);                 // no menu
        bw.Write((ushort)0);                 // default dialog class (#32770)
        foreach (var ch in title + "\0") bw.Write((ushort)ch);
        bw.Write((ushort)9);                 // DS_SETFONT: point size
        foreach (var ch in "Segoe UI\0") bw.Write((ushort)ch);
        bw.Flush();
        var bytes = ms.ToArray();
        var mem = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, mem, bytes.Length);
        return mem;
    }
}
