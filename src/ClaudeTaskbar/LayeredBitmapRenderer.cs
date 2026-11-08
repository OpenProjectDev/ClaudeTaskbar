using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using ClaudeTaskbar.Native;

namespace ClaudeTaskbar;

/// <summary>
/// Renders anti-aliased GDI+ content into a 32bpp-per-pixel-alpha DIB section and
/// composites it onto a WS_EX_LAYERED window via UpdateLayeredWindow. Replaces the
/// original WM_PAINT + LWA_COLORKEY approach after live feedback found visible flicker
/// on refresh and a hard-edged fringe around anti-aliased circle edges (colorkey
/// transparency is binary per pixel; real alpha blending isn't). One instance per
/// layered window (TaskbarWindow and TooltipWindow each own one).
/// </summary>
public sealed class LayeredBitmapRenderer : IDisposable
{
    private IntPtr _memDc;
    private IntPtr _dib;
    private IntPtr _oldBitmap;
    private Bitmap? _bitmap;
    private int _width;
    private int _height;

    /// <summary>(Re)allocates the backing bitmap for the given size and returns a
    /// Graphics ready to draw into it (caller disposes the Graphics, not the bitmap).</summary>
    public Graphics BeginDraw(int width, int height)
    {
        if (width != _width || height != _height) Recreate(width, height);

        var g = Graphics.FromImage(_bitmap!);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // AntiAliasGridFit (not plain AntiAlias, and not ClearTypeGridFit): grayscale
        // antialiasing with glyph stems snapped/hinted to the pixel grid. Plain AntiAlias
        // left text looking soft/pixelated at small sizes (feedback: "text looks
        // pixelated"). ClearTypeGridFit was considered too — its subpixel RGB-fringing
        // technique assumes the final composite lands directly on a real opaque display
        // surface; going through an intermediate premultiplied-alpha DIB then
        // UpdateLayeredWindow is exactly the scenario Microsoft's own docs warn produces
        // color fringing artifacts, so avoided even though our tooltip background is now
        // solid. AntiAliasGridFit has no such risk and is noticeably crisper than AntiAlias.
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);
        return g;
    }

    private void Recreate(int width, int height)
    {
        FreeGdiResources();
        _width = width;
        _height = height;

        var screenDc = Win32.GetDC(IntPtr.Zero);
        _memDc = Win32.CreateCompatibleDC(screenDc);
        Win32.ReleaseDC(IntPtr.Zero, screenDc);

        var bmi = new Win32.BITMAPINFO
        {
            bmiHeader = new Win32.BITMAPINFOHEADER
            {
                biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height, // negative = top-down DIB, matching GDI+'s expected layout
                biPlanes = 1,
                biBitCount = 32,
                biCompression = Win32.BI_RGB,
            },
        };
        _dib = Win32.CreateDIBSection(_memDc, ref bmi, Win32.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
        _oldBitmap = Win32.SelectObject(_memDc, _dib);

        // Wrap the SAME memory the DIB owns — GDI+ drawing writes directly into it, and
        // UpdateLayeredWindow later reads it back out via the (still-selected) memory DC.
        _bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
    }

    /// <summary>Composites the current bitmap content onto the given layered window at
    /// (screenX, screenY), fully opaque per-pixel-alpha (source alpha drives blending).</summary>
    public void Present(IntPtr hwnd, int screenX, int screenY)
    {
        if (_memDc == IntPtr.Zero) return;
        var dest = new Win32.POINT { X = screenX, Y = screenY };
        var src = new Win32.POINT { X = 0, Y = 0 };
        var size = new Win32.SIZE { cx = _width, cy = _height };
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        Win32.UpdateLayeredWindow(hwnd, IntPtr.Zero, ref dest, ref size, _memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);
    }

    /// <summary>Same as Present, but leaves the window's position alone (null pptDst) —
    /// for TaskbarEmbeddedWindow, which positions itself in taskbar-client coordinates.</summary>
    public void PresentInPlace(IntPtr hwnd)
    {
        if (_memDc == IntPtr.Zero) return;
        var src = new Win32.POINT { X = 0, Y = 0 };
        var size = new Win32.SIZE { cx = _width, cy = _height };
        var blend = new Win32.BLENDFUNCTION
        {
            BlendOp = Win32.AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = Win32.AC_SRC_ALPHA,
        };
        Win32.UpdateLayeredWindowKeepPos(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, _memDc, ref src, 0, ref blend, Win32.ULW_ALPHA);
    }

    private void FreeGdiResources()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        if (_memDc != IntPtr.Zero)
        {
            if (_oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_dib != IntPtr.Zero) Win32.DeleteObject(_dib);
            Win32.DeleteDC(_memDc);
        }
        _memDc = IntPtr.Zero;
        _dib = IntPtr.Zero;
        _oldBitmap = IntPtr.Zero;
    }

    public void Dispose() => FreeGdiResources();
}
