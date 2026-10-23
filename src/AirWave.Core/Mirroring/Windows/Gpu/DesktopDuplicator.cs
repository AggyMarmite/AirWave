using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Mirroring.Windows.Gpu;

/// <summary>
/// Captures the main screen on the graphics card with DXGI Desktop Duplication, into a texture that stays on
/// the card (no copy through system memory), with the mouse pointer drawn in.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class DesktopDuplicator : IDisposable
{
    private const int WAIT_TIMEOUT = unchecked((int)0x887A0027), ACCESS_LOST = unchecked((int)0x887A0026);

    public readonly IntPtr DevicePtr;
    public readonly ID3D11Device Device;
    public readonly D3DContext Context;
    public readonly ID3D10Multithread Lock;
    public int Width { get; }
    public int Height { get; }
    public string AdapterName { get; }
    public long AdapterLuid { get; }
    /// <summary>The latest screen image (BGRA), always valid once <see cref="Capture"/> has returned true.</summary>
    public ID3D11Texture2D Frame { get; }

    private readonly IDXGIOutput1 _output;
    private readonly int _left, _top;
    private readonly IntPtr _framePtr, _cleanPtr;
    private readonly ID3D11Texture2D _clean;
    private IDXGIOutputDuplication _dup;
    private bool _haveImage;
    private long _lastRetry;

    public DesktopDuplicator()
    {
        try { SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { }
        var iid = typeof(IDXGIFactory1).GUID;
        Check(D3D.CreateDXGIFactory1(iid, out var fo), "DXGI factory");
        var factory = (IDXGIFactory1)fo;

        // The adapter the main screen is plugged into (on laptops that's often the built-in graphics)
        IDXGIAdapter1 adapter = null;
        IDXGIOutput output = null;
        DXGI_OUTPUT_DESC od = default;
        for (uint a = 0; adapter == null && factory.EnumAdapters1(a, out var ad) == 0; a++)
        {
            for (uint o = 0; ad.EnumOutputs(o, out var op) == 0; o++)
            {
                op.GetDesc(out var d);
                if (d.AttachedToDesktop != 0 && (output == null || (d.Left == 0 && d.Top == 0)))
                {
                    output = op; od = d; adapter = ad;
                    if (d.Left == 0 && d.Top == 0) break;
                }
            }
        }
        if (adapter == null || output == null) throw new NotSupportedException("no screen found on a graphics card");
        if (od.Rotation is not (0 or 1)) throw new NotSupportedException("rotated screen");
        adapter.GetDesc1(out var desc);
        AdapterName = desc.Description?.Trim();
        AdapterLuid = ((long)desc.LuidHigh << 32) | desc.LuidLow;
        _left = od.Left; _top = od.Top;
        Width = od.Right - od.Left; Height = od.Bottom - od.Top;

        // Device on that adapter, with video support so the converter and encoder can share it
        var levels = new[] { 0xb100, 0xb000, 0xa100, 0xa000 };
        var adPtr = Marshal.GetComInterfaceForObject(adapter, typeof(IDXGIAdapter1));
        try
        {
            const uint BGRA = 0x20, VIDEO = 0x800;
            int hr = D3D.D3D11CreateDevice(adPtr, 0, IntPtr.Zero, BGRA | VIDEO, levels, (uint)levels.Length, 7, out DevicePtr, out _, out var ctx);
            if (hr < 0) hr = D3D.D3D11CreateDevice(adPtr, 0, IntPtr.Zero, BGRA, levels, (uint)levels.Length, 7, out DevicePtr, out _, out ctx);
            Check(hr, "Direct3D device");
            Context = new D3DContext(ctx);
        }
        finally { Marshal.Release(adPtr); }
        var devObj = Marshal.GetObjectForIUnknown(DevicePtr);
        Device = (ID3D11Device)devObj;
        Lock = (ID3D10Multithread)devObj;
        Lock.SetMultithreadProtected(1);

        _output = (IDXGIOutput1)output;
        Open();

        var td = new D3D11_TEXTURE2D_DESC
        {
            Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = D3D.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1, Usage = D3D.USAGE_DEFAULT, BindFlags = D3D.BIND_RENDER_TARGET | D3D.BIND_SHADER_RESOURCE, MiscFlags = D3D.MISC_GDI_COMPATIBLE,
        };
        Check(Device.CreateTexture2D(td, IntPtr.Zero, out var tex), "screen texture");
        Frame = tex;
        _framePtr = Marshal.GetComInterfaceForObject(tex, typeof(ID3D11Texture2D));
        td.MiscFlags = 0; td.BindFlags = D3D.BIND_SHADER_RESOURCE;
        Check(Device.CreateTexture2D(td, IntPtr.Zero, out _clean), "screen texture");
        _cleanPtr = Marshal.GetComInterfaceForObject(_clean, typeof(ID3D11Texture2D));
    }

    private void Open()
    {
        if (_dup != null) { Marshal.ReleaseComObject(_dup); _dup = null; }
        Check(_output.DuplicateOutput(Device, out _dup), "desktop duplication");
    }

    /// <summary>Grabs the latest screen image into <see cref="Frame"/> (keeping the previous one if nothing changed).</summary>
    /// <returns>False until a first image has been captured.</returns>
    public bool Capture(int waitMs)
    {
        if (_dup == null)
        {
            // Lost (screen mode change, sign-in screen): try again every half second
            if (Environment.TickCount64 - _lastRetry < 500) return _haveImage;
            _lastRetry = Environment.TickCount64;
            try { Open(); } catch { return _haveImage; }
        }
        int hr = _dup.AcquireNextFrame((uint)waitMs, out var info, out var res);
        if (hr == WAIT_TIMEOUT) return _haveImage;
        if (hr == ACCESS_LOST || hr < 0)
        {
            Log.Debug($"Desktop duplication lost ({hr:X8})");
            Marshal.ReleaseComObject(_dup); _dup = null;
            return _haveImage;
        }
        try
        {
            if (info.LastPresentTime != 0 || !_haveImage)
            {
                var src = (ID3D11Texture2D)res;
                var srcPtr = Marshal.GetComInterfaceForObject(src, typeof(ID3D11Texture2D));
                Lock.Enter();
                try { Context.CopyResource(_cleanPtr, srcPtr); }
                finally { Lock.Leave(); Marshal.Release(srcPtr); }
                _haveImage = true;
            }
        }
        finally
        {
            if (res != null) Marshal.ReleaseComObject(res);
            _dup.ReleaseFrame();
        }
        if (_haveImage) Compose();
        return _haveImage;
    }

    // The pointer isn't part of the duplicated image: start from the clean copy and draw it on with GDI
    private void Compose()
    {
        Lock.Enter();
        try { Context.CopyResource(_framePtr, _cleanPtr); }
        finally { Lock.Leave(); }
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0) return;
        if (!GetIconInfo(ci.hCursor, out var ii)) return;
        var surface = (IDXGISurface1)Frame;
        Lock.Enter();
        try
        {
            if (surface.GetDC(0, out var dc) != 0) return;
            try { DrawIconEx(dc, ci.ptScreenPos.x - _left - ii.xHotspot, ci.ptScreenPos.y - _top - ii.yHotspot, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { surface.ReleaseDC(IntPtr.Zero); }
        }
        finally
        {
            Lock.Leave();
            if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
        }
    }

    internal static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed ({hr:X8})");
    }

    public void Dispose()
    {
        if (_dup != null) Marshal.ReleaseComObject(_dup);
        Marshal.Release(_framePtr);
        Marshal.Release(_cleanPtr);
        Marshal.ReleaseComObject(_clean);
        Marshal.ReleaseComObject(Frame);
        Marshal.ReleaseComObject(_output);
        Marshal.Release(Context.Ptr);
        Marshal.ReleaseComObject(Device);
        Marshal.Release(DevicePtr);
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [StructLayout(LayoutKind.Sequential)] private struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("user32.dll")] private static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);
    [DllImport("user32.dll")] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
