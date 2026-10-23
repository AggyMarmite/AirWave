using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Mirroring.Windows;

/// <summary>
/// Grabs the main screen (with the mouse pointer) into a BGRA buffer, scaled to fit the output size.
/// Plain GDI: works on every Windows PC and graphics card, including remote/virtual sessions.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenCapture : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    private readonly IntPtr _screenDc, _memDc, _bitmap, _old, _bits;

    /// <param name="maxWidth">Largest output width (the TV's), keeping the screen's shape.</param>
    public ScreenCapture(int maxWidth = 1920, int maxHeight = 1080)
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2: real pixels
        SourceWidth = GetSystemMetrics(0);
        SourceHeight = GetSystemMetrics(1);
        double scale = Math.Min(1.0, Math.Min(maxWidth / (double)SourceWidth, maxHeight / (double)SourceHeight));
        Width = Math.Max(16, (int)(SourceWidth * scale) & ~1);
        Height = Math.Max(16, (int)(SourceHeight * scale) & ~1);

        _screenDc = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(_screenDc);
        var bmi = new BITMAPINFO { biSize = 40, biWidth = Width, biHeight = -Height, biPlanes = 1, biBitCount = 32 };
        _bitmap = CreateDIBSection(_screenDc, ref bmi, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero) throw new InvalidOperationException("Couldn't create the capture bitmap");
        _old = SelectObject(_memDc, _bitmap);
        SetStretchBltMode(_memDc, 4 /* HALFTONE */);
        SetBrushOrgEx(_memDc, 0, 0, IntPtr.Zero);
    }

    /// <summary>Captures one frame; returns a pointer to Width*Height BGRA pixels (top-down), valid until the next call.</summary>
    public IntPtr Capture()
    {
        const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;
        bool ok = Width == SourceWidth && Height == SourceHeight
            ? BitBlt(_memDc, 0, 0, Width, Height, _screenDc, 0, 0, SRCCOPY | CAPTUREBLT)
            : StretchBlt(_memDc, 0, 0, Width, Height, _screenDc, 0, 0, SourceWidth, SourceHeight, SRCCOPY | CAPTUREBLT);
        if (!ok) Log.Debug("Screen capture: blit failed (locked screen or secure desktop?)");
        DrawCursor();
        GdiFlush();
        return _bits;
    }

    private void DrawCursor()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        if (!GetIconInfo(ci.hCursor, out var ii)) return;
        try
        {
            double sx = Width / (double)SourceWidth, sy = Height / (double)SourceHeight;
            int x = (int)((ci.ptScreenPos.x - ii.xHotspot) * sx), y = (int)((ci.ptScreenPos.y - ii.yHotspot) * sy);
            int size = Math.Max(16, (int)(32 * Math.Max(sx, sy) * (GetDpiForSystem() / 96.0)));
            DrawIconEx(_memDc, x, y, ci.hCursor, size, size, 0, IntPtr.Zero, 3 /* DI_NORMAL */);
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    public void Dispose()
    {
        SelectObject(_memDc, _old);
        DeleteObject(_bitmap);
        DeleteDC(_memDc);
        ReleaseDC(IntPtr.Zero, _screenDc);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        public int bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
    [DllImport("gdi32.dll")] private static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, int rop);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);
    [DllImport("gdi32.dll")] private static extern bool SetBrushOrgEx(IntPtr dc, int x, int y, IntPtr old);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
}
