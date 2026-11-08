using System.Runtime.InteropServices;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

// --- Documented public interface (ShObjIdl_core.h / MS Learn — not reverse-engineered) ---
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A5CD92FF-29BE-454C-8D04-D82879FB3F1B")]
internal interface IVirtualDesktopManager
{
    [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow, out int onCurrentDesktop);
    [PreserveSig] int GetWindowDesktopId(IntPtr topLevelWindow, out Guid desktopId);
    [PreserveSig] int MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

// --- Undocumented internal interfaces, used only for true left-to-right desktop order ---
// (A deliberate trade-off: these can break with a Windows update, and everything that
// uses them has a fallback.) GUIDs and minimal method slice sourced from
// github.com/MScholtes/VirtualDesktop (an actively-maintained open-source reference that
// tracks per-Windows-version vtable changes), not reverse-engineered from scratch, and
// cross-checked live against the documented API above before being trusted.
//
// Only methods up through GetDesktops are declared. Confirmed by diffing MScholtes'
// VirtualDesktop11.cs against VirtualDesktop11-24H2.cs directly: Windows 11 24H2 inserted
// a new method (SwitchDesktopAndMoveForegroundView) into this vtable, but only AFTER
// SwitchDesktop — which is itself after everything declared here — so this minimal slice
// is identical and safe across both pre-24H2 and 24H2+ Windows builds. Every call is
// wrapped in try/catch; a failure here must never take down the whole app, only silently
// disable ordering (falls back to grouping/original order).
[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
internal interface IApplicationView { }

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IApplicationView view);
    [return: MarshalAs(UnmanagedType.Struct)]
    Guid GetId();
    // GetName() is a real WinRT HSTRING return. MScholtes/VirtualDesktop declares this with
    // [return: MarshalAs(UnmanagedType.HString)] (VirtualDesktop11.cs AND
    // VirtualDesktop11-24H2.cs, cross-checked: same GUID, same GetId/GetName vtable order
    // in both) — but that threw "Invalid managed/unmanaged type combination" live on .NET
    // 8's built-in COM interop (confirmed with a standalone test program): that attribute
    // apparently isn't supported on a RETURN value here, parameters only. Declared as the
    // raw HSTRING handle instead and converted manually via the documented
    // WindowsGetStringRawBuffer/WindowsDeleteString WinRT string ABI (combase.dll) — see
    // HStringToString below. Empty string for an unnamed (default) desktop, confirmed live.
    IntPtr GetName();
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9")]
internal interface IObjectArray
{
    void GetCount(out int count);
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object GetAt(int index, ref Guid iid);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("53F5CA0B-158F-4124-900C-057158060B27")]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(IApplicationView view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    // Declared (not just GetDesktops) so SwitchDesktop lands at the correct vtable slot —
    // GetAdjacentDesktop itself is never called. Both are still before the Windows 11 24H2
    // insertion point (see the class remarks), so this stays safe on either side of it.
    [PreserveSig] int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
}

[ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

/// <summary>
/// Virtual-desktop integration: click-to-switch uses only the documented
/// IVirtualDesktopManager + SetForegroundWindow (no undocumented API needed at all — a
/// window's own virtual desktop switches to become current automatically as a side effect
/// of focusing it, confirmed live). True left-to-right ordering needs the undocumented
/// IVirtualDesktopManagerInternal, isolated here so a failure there can never affect
/// click-to-switch, which has no such dependency.
/// </summary>
public static class VirtualDesktopInterop
{
    private static readonly Guid ClsidImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ClsidVirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    private static readonly Guid ClsidVirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");

    private static IVirtualDesktopManager? _manager;
    private static IVirtualDesktopManager Manager => _manager ??=
        (IVirtualDesktopManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidVirtualDesktopManager)!)!;

    /// <summary>The window's virtual desktop GUID, or null if it can't be determined
    /// (documented API failed, window closed, etc. — never guessed).</summary>
    public static Guid? GetWindowDesktopId(IntPtr hwnd)
    {
        try
        {
            var hr = Manager.GetWindowDesktopId(hwnd, out var id);
            return hr == 0 ? id : null;
        }
        catch
        {
            _manager = null; // drop a possibly-broken proxy, retry fresh next call
            return null;
        }
    }

    /// <summary>True if the window is on the currently-active virtual desktop, null if
    /// that can't be determined (never guessed) — used for the current-desktop border
    /// indicator on circles.</summary>
    public static bool? IsWindowOnCurrentDesktop(IntPtr hwnd)
    {
        try
        {
            var hr = Manager.IsWindowOnCurrentVirtualDesktop(hwnd, out var onCurrent);
            return hr == 0 ? onCurrent != 0 : null;
        }
        catch
        {
            _manager = null;
            return null;
        }
    }

    /// <summary>Focuses the given window — Windows itself switches the active virtual
    /// desktop to match as a side effect if it's on a different one (confirmed live, no
    /// undocumented API involved). Restores it first if minimized.</summary>
    public static bool TrySwitchToWindow(IntPtr hwnd)
    {
        try
        {
            return Win32.SetForegroundWindow(hwnd);
        }
        catch
        {
            return false;
        }
    }

    private static IVirtualDesktopManagerInternal GetManagerInternal()
    {
        var shell = (IServiceProvider10)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidImmersiveShell)!)!;
        var svc = ClsidVirtualDesktopManagerInternal;
        var iid = typeof(IVirtualDesktopManagerInternal).GUID;
        return (IVirtualDesktopManagerInternal)shell.QueryService(ref svc, ref iid);
    }

    [DllImport("combase.dll")]
    private static extern IntPtr WindowsGetStringRawBuffer(IntPtr hstring, out uint length);
    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    /// <summary>Converts a raw WinRT HSTRING handle (as returned by IVirtualDesktop.GetName)
    /// into a normal string — see that interface's remarks for why this is needed instead
    /// of a MarshalAs attribute. WindowsDeleteString releases the string, per WinRT's
    /// "caller owns the returned HSTRING" convention.</summary>
    private static string HStringToString(IntPtr hstring)
    {
        if (hstring == IntPtr.Zero) return "";
        var ptr = WindowsGetStringRawBuffer(hstring, out var len);
        var result = len == 0 || ptr == IntPtr.Zero ? "" : Marshal.PtrToStringUni(ptr, (int)len);
        WindowsDeleteString(hstring);
        return result ?? "";
    }

    /// <summary>True left-to-right desktop order (index 0 = leftmost in Task View), each
    /// with its real user-visible name (empty string for a desktop never renamed — Windows
    /// itself computes the displayed "Desktop N" label at Task-View-render time, it's not
    /// stored per-desktop, confirmed live), plus which one is currently active —
    /// undocumented, see the class-level remarks. Null order if anything about the
    /// undocumented path fails entirely; callers must have a non-ordered fallback (grouping
    /// everything into one slot rather than guessing). Combined into one call so building
    /// the per-tick desktop layout costs only one COM round-trip.</summary>
    public static (List<(Guid Id, string Name)>? Order, Guid? CurrentId) GetDesktopLayout()
    {
        try
        {
            var mgrInternal = GetManagerInternal();
            var currentId = mgrInternal.GetCurrentDesktop().GetId();

            mgrInternal.GetDesktops(out var desktops);
            desktops.GetCount(out var count);

            var result = new List<(Guid, string)>(count);
            var vdIid = typeof(IVirtualDesktop).GUID;
            for (var i = 0; i < count; i++)
            {
                var iidCopy = vdIid;
                var vd = (IVirtualDesktop)desktops.GetAt(i, ref iidCopy);
                result.Add((vd.GetId(), HStringToString(vd.GetName())));
            }
            return (result, currentId);
        }
        catch
        {
            return (null, null); // undocumented path broke (Windows update, etc.) — fall back, don't guess
        }
    }

    /// <summary>Switches directly to a desktop by id — used only for a grey (session-less)
    /// desktop slot, where there's no window to focus (SetForegroundWindow has nothing to
    /// target), and for a capsule click that lands on its padding rather than a specific
    /// session circle. Undocumented, same fragility class as ordering. Re-resolves the
    /// desktop list fresh each call rather than caching COM object references across ticks
    /// (safer than holding on to a proxy that might go stale between polls). Live-tested in
    /// isolation before being wired in, with a standalone test program — confirmed switching
    /// to a specific desktop by GUID and back, verified via GetCurrentDesktop before/after,
    /// as every undocumented API used here must be verified before it is relied on.</summary>
    public static bool TrySwitchToDesktopId(Guid desktopId)
    {
        try
        {
            var mgrInternal = GetManagerInternal();
            mgrInternal.GetDesktops(out var desktops);
            desktops.GetCount(out var count);
            var vdIid = typeof(IVirtualDesktop).GUID;
            for (var i = 0; i < count; i++)
            {
                var iidCopy = vdIid;
                var vd = (IVirtualDesktop)desktops.GetAt(i, ref iidCopy);
                if (vd.GetId() != desktopId) continue;
                mgrInternal.SwitchDesktop(vd);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }
}
