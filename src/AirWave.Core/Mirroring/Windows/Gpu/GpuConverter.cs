using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AirWave.Core.Audio;

namespace AirWave.Core.Mirroring.Windows.Gpu;

/// <summary>
/// Converts (and if needed shrinks) the BGRA screen texture to NV12 on the graphics card, using the
/// Windows video processor. The NV12 result stays on the card, ready for the hardware encoder.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GpuConverter : IDisposable
{
    private static readonly Guid IID_Texture2D = typeof(ID3D11Texture2D).GUID;
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    internal static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    internal static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    internal static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    internal static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");

    public int Width { get; }
    public int Height { get; }
    private readonly IMFTransform _vp;
    private readonly DesktopDuplicator _src;
    private readonly bool _providesSamples;
    private readonly IMFMediaBuffer _inBuf;
    private readonly IMFSample _inSample;
    private readonly ID3D11Texture2D[] _ring;
    private int _next;

    public GpuConverter(DesktopDuplicator src, IMFDXGIDeviceManager manager, int width, int height, int fps)
    {
        _src = src; Width = width; Height = height;
        Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");
        var clsid = MfGpu.CLSID_VideoProcessorMFT;
        var iid = typeof(IMFTransform).GUID;
        Mf.Check(Mf.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var o), "create video processor");
        _vp = (IMFTransform)o;
        var mgr = Marshal.GetIUnknownForObject(manager);
        try { Mf.Check(_vp.ProcessMessage(MfGpu.MFT_MESSAGE_SET_D3D_MANAGER, mgr), "video processor on the graphics card"); }
        finally { Marshal.Release(mgr); }

        Mf.Check(_vp.SetInputType(0, VideoType(MfGpu.MFVideoFormat_RGB32, src.Width, src.Height, fps), 0), "video processor input");
        var outType = VideoType(MfGpu.MFVideoFormat_NV12, width, height, fps);
        outType.SetUINT32(MF_MT_YUV_MATRIX, 1);          // BT.709
        outType.SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, 2); // 16-235
        Mf.Check(_vp.SetOutputType(0, outType, 0), "video processor output");
        Mf.Check(_vp.GetOutputStreamInfo(0, out var info), "video processor info");
        _providesSamples = (info.dwFlags & 0x100) != 0;

        Mf.Check(MfGpu.MFCreateDXGISurfaceBuffer(IID_Texture2D, src.Frame, 0, 0, out _inBuf), "screen buffer");
        _inBuf.GetMaxLength(out uint max);
        _inBuf.SetCurrentLength(max);
        Mf.Check(Mf.MFCreateSample(out _inSample), "sample");
        _inSample.AddBuffer(_inBuf);

        if (!_providesSamples)
        {
            // Our own NV12 textures, a few so the encoder can still be reading one while we fill the next
            _ring = new ID3D11Texture2D[6];
            var td = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = D3D.DXGI_FORMAT_NV12,
                SampleCount = 1, Usage = D3D.USAGE_DEFAULT, BindFlags = D3D.BIND_RENDER_TARGET | 0x200 /* VIDEO_ENCODER */,
            };
            for (int i = 0; i < _ring.Length; i++)
            {
                if (src.Device.CreateTexture2D(td, IntPtr.Zero, out _ring[i]) < 0)
                {
                    td.BindFlags = D3D.BIND_RENDER_TARGET;
                    DesktopDuplicator.Check(src.Device.CreateTexture2D(td, IntPtr.Zero, out _ring[i]), "NV12 texture");
                }
            }
        }
        _vp.ProcessMessage(0x10000000, IntPtr.Zero); // BEGIN_STREAMING
        _vp.ProcessMessage(0x10000003, IntPtr.Zero); // START_OF_STREAM
    }

    internal static IMFMediaType VideoType(Guid subtype, int w, int h, int fps)
    {
        ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;
        Mf.Check(Mf.MFCreateMediaType(out var t), "media type");
        t.SetGUID(Mf.MF_MT_MAJOR_TYPE, MfGpu.MFMediaType_Video);
        t.SetGUID(Mf.MF_MT_SUBTYPE, subtype);
        t.SetUINT64(MF_MT_FRAME_SIZE, Pack(w, h));
        t.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
        t.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        t.SetUINT32(MF_MT_INTERLACE_MODE, 2);
        return t;
    }

    private const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5), MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);

    /// <summary>
    /// Converts the current screen texture. The returned sample (NV12 on the card) is the caller's to release;
    /// null if the converter had nothing to give yet.
    /// </summary>
    public IMFSample Convert(long time100ns, long duration100ns)
    {
        _inSample.SetSampleTime(time100ns);
        _inSample.SetSampleDuration(duration100ns);
        int hr = _vp.ProcessInput(0, _inSample, 0);
        if (hr == MF_E_NOTACCEPTING)
        {
            // Some drivers' converters hold on to a frame: collect what's waiting, then try again
            for (int i = 0; i < 4; i++)
            {
                var old = TakeOutput(time100ns, duration100ns);
                if (old == null) break;
                Marshal.ReleaseComObject(old);
            }
            hr = _vp.ProcessInput(0, _inSample, 0);
        }
        Mf.Check(hr, "convert the screen");
        return TakeOutput(time100ns, duration100ns);
    }

    private IMFSample TakeOutput(long time100ns, long duration100ns)
    {
        IMFSample outSample = null;
        IMFMediaBuffer outBuf = null;
        if (!_providesSamples)
        {
            var tex = _ring[_next++ % _ring.Length];
            Mf.Check(MfGpu.MFCreateDXGISurfaceBuffer(IID_Texture2D, tex, 0, 0, out outBuf), "NV12 buffer");
            Mf.MFCreateSample(out outSample);
            outSample.AddBuffer(outBuf);
            Marshal.ReleaseComObject(outBuf);
        }
        var odb = new MftOutputDataBuffer { pSample = outSample == null ? IntPtr.Zero : Marshal.GetComInterfaceForObject(outSample, typeof(IMFSample)) };
        int hr = _vp.ProcessOutput(0, 1, ref odb, out _);
        if (odb.pEvents != IntPtr.Zero) Marshal.Release(odb.pEvents);
        try
        {
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) { if (outSample != null) Marshal.ReleaseComObject(outSample); return null; }
            Mf.Check(hr, "convert the screen (output)");
            var s = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
            s.SetSampleTime(time100ns);
            s.SetSampleDuration(duration100ns);
            return s;
        }
        catch
        {
            if (outSample != null) Marshal.ReleaseComObject(outSample);
            throw;
        }
        finally
        {
            if (odb.pSample != IntPtr.Zero) Marshal.Release(odb.pSample);
        }
    }

    public void Dispose()
    {
        try { _vp.ProcessMessage(0x10000001, IntPtr.Zero); } catch { }
        Marshal.ReleaseComObject(_inSample);
        Marshal.ReleaseComObject(_inBuf);
        if (_ring != null) foreach (var t in _ring) if (t != null) Marshal.ReleaseComObject(t);
        Marshal.ReleaseComObject(_vp);
    }
}
