using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AirWave.Core.Audio;
using AirWave.Core.Mirroring.Windows.Gpu;

namespace AirWave.Core.Mirroring.Windows;

/// <summary>Captures and encodes the screen, one frame per <see cref="Tick"/>; encoded frames come out of <see cref="Output"/>.</summary>
internal interface IMirrorPipeline : IDisposable
{
    string Description { get; }
    string EncoderKind { get; }
    int Width { get; }
    int Height { get; }
    int Fps { get; }
    bool Broken { get; }
    event Action<H264.AccessUnit, long> Output;
    void Tick(long captureNs);
    /// <summary>Bitrate the encoder started with (the most the network adaptation will go back up to).</summary>
    int MaxBitrate { get; }
    void ForceKeyframe();
    void SetBitrate(int bps);
}

internal static class MirrorSizes
{
    /// <summary>Largest size that fits both the screen and the limit, keeping the screen's shape (never enlarging).</summary>
    public static (int w, int h) Fit(int srcW, int srcH, int maxW, int maxH)
    {
        double scale = Math.Min(1.0, Math.Min(maxW / (double)srcW, maxH / (double)srcH));
        return (Math.Max(16, (int)(srcW * scale) & ~1), Math.Max(16, (int)(srcH * scale) & ~1));
    }

    public static int Bitrate(int w, int h, int fps) => (int)Math.Clamp(w * (long)h * fps / 10, 4_000_000, 25_000_000);
}

/// <summary>The original path: GDI capture and the Windows software encoder (works everywhere; 30 fps, up to 1080p).</summary>
[SupportedOSPlatform("windows")]
internal sealed class SoftwarePipeline : IMirrorPipeline
{
    private readonly ScreenCapture _cap;
    private readonly H264Encoder _enc;
    public string Description { get; }
    public string EncoderKind => "Processor (software)";
    public int Width => _cap.Width;
    public int Height => _cap.Height;
    public int Fps { get; }
    public bool Broken => false;
    public event Action<H264.AccessUnit, long> Output;

    public int MaxBitrate { get; }
    public void ForceKeyframe() => _enc.ForceKeyframe();
    public void SetBitrate(int bps) => _enc.SetBitrate(bps);

    public SoftwarePipeline(int maxW, int maxH, int fps = 30)
    {
        Fps = fps;
        _cap = new ScreenCapture(Math.Min(maxW, 1920), Math.Min(maxH, 1080));
        MaxBitrate = MirrorSizes.Bitrate(_cap.Width, _cap.Height, fps);
        _enc = new H264Encoder(_cap.Width, _cap.Height, fps, MaxBitrate);
        Description = $"{_cap.SourceWidth}×{_cap.SourceHeight} screen → {Width}×{Height} at {fps} fps, {_enc.Name}";
    }

    public void Tick(long captureNs)
    {
        foreach (var au in _enc.Encode(_cap.Capture())) Output?.Invoke(au, captureNs);
    }

    public void Dispose() { _enc.Dispose(); _cap.Dispose(); }
}

/// <summary>
/// Capture and colour conversion on the graphics card; encoding on the card's own H.264 encoder at 60 fps and full
/// resolution, or (if it hasn't one) by the software encoder at 30 fps.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class GpuPipeline : IMirrorPipeline
{
    private readonly DesktopDuplicator _dup;
    private readonly IMFDXGIDeviceManager _manager;
    private readonly GpuConverter _conv;
    private readonly HardwareH264Encoder _hw;
    private readonly H264Encoder _sw;
    private byte[] _packed;
    private volatile bool _broken;
    public string Description { get; }
    public string EncoderKind { get; }
    public int Width => _conv.Width;
    public int Height => _conv.Height;
    public int Fps { get; }
    public bool Broken => _broken;
    public event Action<H264.AccessUnit, long> Output;
    public int MaxBitrate { get; }
    public void ForceKeyframe() { if (_hw != null) _hw.ForceKeyframe(); else _sw?.ForceKeyframe(); }
    public void SetBitrate(int bps) { if (_hw != null) _hw.SetBitrate(bps); else _sw?.SetBitrate(bps); }

    public GpuPipeline(int maxW, int maxH, bool hardwareEncoder)
    {
        _dup = new DesktopDuplicator();
        try
        {
            DesktopDuplicator.Check(MfGpu.MFCreateDXGIDeviceManager(out uint token, out _manager), "device manager");
            DesktopDuplicator.Check(_manager.ResetDevice(Marshal.GetObjectForIUnknown(_dup.DevicePtr), token), "device manager");

            if (hardwareEncoder)
            {
                var (w, h) = MirrorSizes.Fit(_dup.Width, _dup.Height, maxW, maxH);
                foreach (var a in HardwareH264Encoder.Find(_dup.AdapterLuid))
                {
                    try
                    {
                        _conv ??= new GpuConverter(_dup, _manager, w, h, 60);
                        _hw = new HardwareH264Encoder(a, _manager, w, h, 60, MirrorSizes.Bitrate(w, h, 60));
                        MaxBitrate = MirrorSizes.Bitrate(w, h, 60);
                        break;
                    }
                    catch (Exception ex) { Log.Info($"Graphics card encoder {MfGpu.FriendlyName(a)} not usable: {ex.Message}"); }
                }
            }
            if (_hw != null)
            {
                Fps = 60;
                EncoderKind = "Graphics card";
                _hw.Output += (au, t) => Output?.Invoke(au, t);
                _hw.Failed += _ => _broken = true;
                Description = $"{_dup.Width}×{_dup.Height} screen → {Width}×{Height} at 60 fps, {_hw.Name} ({_dup.AdapterName})";
            }
            else
            {
                // No usable encoder on the card: convert there, encode in software at 30 fps
                Fps = 30;
                var (w, h) = MirrorSizes.Fit(_dup.Width, _dup.Height, Math.Min(maxW, 1920), Math.Min(maxH, 1080));
                if (_conv == null || _conv.Width != w || _conv.Height != h) { _conv?.Dispose(); _conv = new GpuConverter(_dup, _manager, w, h, 30); }
                MaxBitrate = MirrorSizes.Bitrate(w, h, 30);
                _sw = new H264Encoder(w, h, 30, MaxBitrate);
                EncoderKind = "Processor (software)";
                Description = $"{_dup.Width}×{_dup.Height} screen → {Width}×{Height} at 30 fps, {_sw.Name} (no graphics card encoder found)";
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Tick(long captureNs)
    {
        if (_broken) return;
        try
        {
            if (!_dup.Capture(0)) return;
            var sample = _conv.Convert(captureNs / 100, 10_000_000L / Fps);
            if (sample == null) return;
            try
            {
                if (_hw != null) _hw.Submit(sample);
                else EncodeInSoftware(sample, captureNs);
            }
            finally { Marshal.ReleaseComObject(sample); }
        }
        catch (Exception ex)
        {
            Log.Warn($"Graphics card mirroring stopped: {ex.Message}");
            _broken = true;
        }
    }

    private void EncodeInSoftware(IMFSample sample, long captureNs)
    {
        Mf.Check(sample.ConvertToContiguousBuffer(out var buf), "read back");
        try
        {
            Mf.Check(buf.Lock(out IntPtr p, out _, out uint len), "read back");
            try
            {
                int w = Width, h = Height, size = w * h * 3 / 2;
                if (len == size) { foreach (var au in _sw.EncodeNv12(p, size)) Output?.Invoke(au, captureNs); return; }
                // Rows padded: pack them
                int pitch = (int)(len * 2 / (3 * h));
                if (pitch < w) throw new InvalidOperationException($"unexpected frame layout ({len} bytes)");
                _packed ??= new byte[size];
                fixed (byte* d = _packed)
                {
                    byte* s = (byte*)p;
                    for (int r = 0; r < h * 3 / 2; r++) Buffer.MemoryCopy(s + (long)r * pitch, d + (long)r * w, w, w);
                    foreach (var au in _sw.EncodeNv12((IntPtr)d, size)) Output?.Invoke(au, captureNs);
                }
            }
            finally { buf.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buf); }
    }

    public void Dispose()
    {
        _hw?.Dispose();
        _sw?.Dispose();
        _conv?.Dispose();
        if (_manager != null) Marshal.ReleaseComObject(_manager);
        _dup?.Dispose();
    }
}
