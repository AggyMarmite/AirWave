using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AirWave.Core.Audio;

namespace AirWave.Core.Mirroring.Windows.Gpu;

/// <summary>
/// The graphics card's own H.264 encoder (NVIDIA NVENC, AMD AMF/VCE, Intel Quick Sync), through Media Foundation.
/// These run asynchronously: the encoder asks for frames and hands back output through events, which a
/// background thread handles. Frames arriving while the encoder is still busy are skipped, so delay never builds up.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class HardwareH264Encoder : IDisposable
{
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    private const int MF_E_SHUTDOWN = unchecked((int)0xC00D3E85);

    public string Name { get; }
    /// <summary>One encoded frame, with the capture time (ns, <see cref="Timing.MonoClock"/>) it was stamped with.</summary>
    public event Action<H264.AccessUnit, long> Output;
    public event Action<Exception> Failed;

    private readonly IMFActivate _activate;
    private readonly IMFTransform _mft;
    private readonly IMFMediaEventGenerator _events;
    private readonly ICodecAPI _codec;
    private readonly bool _providesSamples;
    private readonly int _outSize;
    private readonly AccessUnitBuilder _units = new();
    private readonly Thread _thread;
    private int _wanted;
    private volatile bool _closed;

    /// <summary>Finds the hardware encoders for the adapter (best first).</summary>
    public static List<IMFActivate> Find(long adapterLuid)
    {
        var list = new List<IMFActivate>();
        Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");
        var input = new MFT_REGISTER_TYPE_INFO { MajorType = MfGpu.MFMediaType_Video, SubType = MfGpu.MFVideoFormat_NV12 };
        var output = new MFT_REGISTER_TYPE_INFO { MajorType = MfGpu.MFMediaType_Video, SubType = MfGpu.MFVideoFormat_H264 };
        if (MfGpu.MFTEnumEx(MfGpu.MFT_CATEGORY_VIDEO_ENCODER, MfGpu.MFT_ENUM_FLAG_HARDWARE | MfGpu.MFT_ENUM_FLAG_SORTANDFILTER, input, output, out var arr, out uint n) < 0 || arr == IntPtr.Zero)
            return list;
        var other = new List<IMFActivate>();
        for (int i = 0; i < n; i++)
        {
            var p = Marshal.ReadIntPtr(arr, i * IntPtr.Size);
            var a = (IMFActivate)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            // Encoders on the same graphics card as the screen first
            if (a.GetUINT64(MfGpu.MFT_ENUM_ADAPTER_LUID, out ulong luid) == 0 && (long)luid != adapterLuid) other.Add(a);
            else list.Add(a);
        }
        Marshal.FreeCoTaskMem(arr);
        list.AddRange(other);
        return list;
    }

    public HardwareH264Encoder(IMFActivate activate, IMFDXGIDeviceManager manager, int width, int height, int fps, int bitrate)
    {
        _activate = activate;
        Name = MfGpu.FriendlyName(activate);
        Mf.Check(activate.ActivateObject(typeof(IMFTransform).GUID, out var o), "start the graphics card encoder");
        _mft = (IMFTransform)o;
        _events = (IMFMediaEventGenerator)o;
        _codec = o as ICodecAPI;
        try
        {
            if (_mft.GetAttributes(out var attrs) == 0 && attrs != null)
            {
                attrs.SetUINT32(MfGpu.MF_TRANSFORM_ASYNC_UNLOCK, 1);
                attrs.SetUINT32(MfGpu.MF_LOW_LATENCY, 1);
                Marshal.ReleaseComObject(attrs);
            }
            var mgr = Marshal.GetIUnknownForObject(manager);
            try { Mf.Check(_mft.ProcessMessage(MfGpu.MFT_MESSAGE_SET_D3D_MANAGER, mgr), "encoder on the graphics card"); }
            finally { Marshal.Release(mgr); }

            TrySet(CODECAPI_AVEncCommonRateControlMode, 0u); // CBR
            TrySet(CODECAPI_AVEncCommonMeanBitRate, (uint)bitrate);
            TrySet(CODECAPI_AVLowLatencyMode, true);
            TrySet(CODECAPI_AVEncMPVDefaultBPictureCount, 0u);
            TrySet(CODECAPI_AVEncMPVGOPSize, (uint)(fps * 2));

            var outType = GpuConverter.VideoType(MfGpu.MFVideoFormat_H264, width, height, fps);
            outType.SetUINT32(MF_MT_AVG_BITRATE, (uint)bitrate);
            outType.SetUINT32(MF_MT_MPEG2_PROFILE, 100); // High
            int hr = _mft.SetOutputType(0, outType, 0);
            if (hr < 0) { outType.SetUINT32(MF_MT_MPEG2_PROFILE, 77); hr = _mft.SetOutputType(0, outType, 0); } // Main
            Mf.Check(hr, "encoder output type");
            var inType = GpuConverter.VideoType(MfGpu.MFVideoFormat_NV12, width, height, fps);
            inType.SetUINT32(GpuConverter.MF_MT_YUV_MATRIX, 1);
            inType.SetUINT32(GpuConverter.MF_MT_VIDEO_NOMINAL_RANGE, 2);
            Mf.Check(_mft.SetInputType(0, inType, 0), "encoder input type");
            if (outType.GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, out uint hs) == 0 && hs > 0)
            {
                var blob = new byte[hs];
                outType.GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, blob, hs, out _);
                _units.Remember(H264.SplitAnnexB(blob));
            }
            Marshal.ReleaseComObject(outType);
            Marshal.ReleaseComObject(inType);

            Mf.Check(_mft.GetOutputStreamInfo(0, out var info), "encoder info");
            _providesSamples = (info.dwFlags & 0x300) != 0;
            _outSize = Math.Max((int)info.cbSize, width * height);
            Mf.Check(_mft.ProcessMessage(0x10000000, IntPtr.Zero), "start encoding");   // BEGIN_STREAMING
            Mf.Check(_mft.ProcessMessage(0x10000003, IntPtr.Zero), "start encoding");   // START_OF_STREAM
        }
        catch
        {
            try { activate.ShutdownObject(); } catch { }
            throw;
        }
        _thread = new Thread(EventLoop) { IsBackground = true, Name = "AirWave GPU encoder", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void TrySet(Guid key, object value)
    {
        if (_codec == null) return;
        try
        {
            object v = value;
            int hr = _codec.SetValue(ref key, ref v);
            if (hr != 0) Log.Debug($"GPU encoder setting {key} not applied ({hr:X8})");
        }
        catch (Exception ex) { Log.Debug($"GPU encoder setting {key}: {ex.Message}"); }
    }

    public void ForceKeyframe() => TrySet(CODECAPI_AVEncVideoForceKeyFrame, 1u);

    /// <summary>Hands a frame (NV12 on the card, sample time = capture time / 100) to the encoder, if it's ready for one.</summary>
    /// <returns>False if the encoder was still busy and the frame was skipped.</returns>
    public bool Submit(IMFSample sample)
    {
        if (_closed) return false;
        if (Interlocked.Decrement(ref _wanted) < 0) { Interlocked.Increment(ref _wanted); return false; }
        int hr = _mft.ProcessInput(0, sample, 0);
        if (hr < 0) throw new InvalidOperationException($"Graphics card encoder rejected a frame ({hr:X8})");
        return true;
    }

    private void EventLoop()
    {
        try
        {
            while (!_closed)
            {
                int hr = _events.GetEvent(0, out var ev);
                if (hr == MF_E_SHUTDOWN || _closed) break;
                Mf.Check(hr, "encoder event");
                try
                {
                    ev.GetType(out uint type);
                    ev.GetStatus(out int status);
                    if (status < 0) throw new InvalidOperationException($"Graphics card encoder error ({status:X8})");
                    if (type == MfGpu.METransformNeedInput) Interlocked.Increment(ref _wanted);
                    else if (type == MfGpu.METransformHaveOutput) TakeOutput();
                }
                finally { Marshal.ReleaseComObject(ev); }
            }
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                Log.Warn($"Graphics card encoder stopped: {ex.Message}");
                _closed = true;
                Failed?.Invoke(ex);
            }
        }
    }

    private void TakeOutput()
    {
        IMFSample outSample = null;
        IMFMediaBuffer outBuf = null;
        if (!_providesSamples)
        {
            Mf.MFCreateMemoryBuffer((uint)_outSize, out outBuf);
            Mf.MFCreateSample(out outSample);
            outSample.AddBuffer(outBuf);
        }
        var odb = new MftOutputDataBuffer { pSample = outSample == null ? IntPtr.Zero : Marshal.GetComInterfaceForObject(outSample, typeof(IMFSample)) };
        int hr = _mft.ProcessOutput(0, 1, ref odb, out _);
        if (odb.pEvents != IntPtr.Zero) Marshal.Release(odb.pEvents);
        try
        {
            if (hr == unchecked((int)0xC00D6D72) || hr == unchecked((int)0xC00D6D61)) return; // need input / stream change
            Mf.Check(hr, "encoder output");
            var s = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
            bool clean = s.GetUINT32(MFSampleExtension_CleanPoint, out uint cp) == 0 && cp != 0;
            long captured = s.GetSampleTime(out long t) == 0 && t > 0 ? t * 100 : Timing.MonoClock.NowNs;
            s.ConvertToContiguousBuffer(out var cb);
            cb.Lock(out IntPtr p, out _, out uint len);
            var annexB = new byte[len];
            Marshal.Copy(p, annexB, 0, (int)len);
            cb.Unlock();
            Marshal.ReleaseComObject(cb);
            if (!ReferenceEquals(s, outSample)) Marshal.ReleaseComObject(s);
            var au = _units.Build(annexB, clean);
            if (au != null) Output?.Invoke(au, captured);
        }
        finally
        {
            if (odb.pSample != IntPtr.Zero) Marshal.Release(odb.pSample);
            if (outSample != null) Marshal.ReleaseComObject(outSample);
            if (outBuf != null) Marshal.ReleaseComObject(outBuf);
        }
    }

    public void Dispose()
    {
        _closed = true;
        try { _mft.ProcessMessage(0x10000001, IntPtr.Zero); } catch { } // END_STREAMING
        try { _activate.ShutdownObject(); } catch { }                   // also wakes the event thread
        _thread?.Join(1000);
        try { Marshal.ReleaseComObject(_mft); } catch { }
    }
}
