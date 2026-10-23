using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AirWave.Core.Audio;

namespace AirWave.Core.Mirroring.Windows;

/// <summary>
/// H.264 encoding with the encoder built into Windows (Media Foundation), tuned for screen mirroring:
/// no B-frames, a keyframe every couple of seconds, constant-ish bitrate. Input is BGRA from
/// <see cref="ScreenCapture"/>; output is one access unit per frame.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed unsafe class H264Encoder : IDisposable
{
    private static readonly Guid CLSID_MSH264Encoder = new("6CA50344-051A-4DED-9779-A43305165E35");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    private static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    private static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    private static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    private static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    private static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    private static readonly Guid MFSampleExtension_CleanPoint = new("9cdf01d8-a0f0-43ba-b077-eaa06cbd728a");
    private static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    private static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    private static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    private static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    private static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");

    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public string Name { get; } = "Windows H.264 encoder";
    private readonly IMFTransform _mft;
    private readonly ICodecAPI _codec;
    private readonly int _outSize;
    private readonly bool _providesSamples;
    private readonly byte[] _nv12;
    private long _frameIndex;
    private readonly AccessUnitBuilder _units = new();

    public H264Encoder(int width, int height, int fps = 30, int bitrate = 8_000_000)
    {
        Width = width; Height = height; Fps = fps;
        _nv12 = new byte[width * height * 3 / 2];
        Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");
        var clsid = CLSID_MSH264Encoder;
        var iid = typeof(IMFTransform).GUID;
        Mf.Check(Mf.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var o), "create H.264 encoder (is Media Foundation installed?)");
        _mft = (IMFTransform)o;
        _codec = o as ICodecAPI;

        // Settings first (some only apply before the types are set)
        TrySet(CODECAPI_AVEncCommonRateControlMode, 0u);   // CBR: steady over Wi-Fi
        TrySet(CODECAPI_AVLowLatencyMode, true);
        TrySet(CODECAPI_AVEncMPVDefaultBPictureCount, 0u);
        TrySet(CODECAPI_AVEncMPVGOPSize, (uint)(fps * 2));  // keyframe every 2 s

        ulong Pack(int a, int b) => ((ulong)(uint)a << 32) | (uint)b;
        Mf.Check(Mf.MFCreateMediaType(out var outType), "media type");
        outType.SetGUID(Mf.MF_MT_MAJOR_TYPE, MFMediaType_Video);
        outType.SetGUID(Mf.MF_MT_SUBTYPE, MFVideoFormat_H264);
        outType.SetUINT32(MF_MT_AVG_BITRATE, (uint)bitrate);
        outType.SetUINT64(MF_MT_FRAME_SIZE, Pack(width, height));
        outType.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
        outType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        outType.SetUINT32(MF_MT_INTERLACE_MODE, 2);       // progressive
        outType.SetUINT32(MF_MT_MPEG2_PROFILE, 100);       // High
        Mf.Check(_mft.SetOutputType(0, outType, 0), "H.264 output type");

        Mf.Check(Mf.MFCreateMediaType(out var inType), "media type");
        inType.SetGUID(Mf.MF_MT_MAJOR_TYPE, MFMediaType_Video);
        inType.SetGUID(Mf.MF_MT_SUBTYPE, MFVideoFormat_NV12);
        inType.SetUINT64(MF_MT_FRAME_SIZE, Pack(width, height));
        inType.SetUINT64(MF_MT_FRAME_RATE, Pack(fps, 1));
        inType.SetUINT64(MF_MT_PIXEL_ASPECT_RATIO, Pack(1, 1));
        inType.SetUINT32(MF_MT_INTERLACE_MODE, 2);
        inType.SetUINT32(MF_MT_YUV_MATRIX, 1);             // BT.709
        inType.SetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, 2);    // 16-235
        Mf.Check(_mft.SetInputType(0, inType, 0), "H.264 input type");

        // Parameter sets may only be in the output type
        if (outType.GetBlobSize(MF_MT_MPEG_SEQUENCE_HEADER, out uint hs) == 0 && hs > 0)
        {
            var blob = new byte[hs];
            outType.GetBlob(MF_MT_MPEG_SEQUENCE_HEADER, blob, hs, out _);
            _units.Remember(H264.SplitAnnexB(blob));
        }
        Marshal.ReleaseComObject(outType);
        Marshal.ReleaseComObject(inType);

        Mf.Check(_mft.GetOutputStreamInfo(0, out var info), "output info");
        _providesSamples = (info.dwFlags & 0x300) != 0;
        _outSize = Math.Max((int)info.cbSize, width * height * 2);
        _mft.ProcessMessage(0x10000000, IntPtr.Zero); // BEGIN_STREAMING
        _mft.ProcessMessage(0x10000003, IntPtr.Zero); // START_OF_STREAM
    }

    private void TrySet(Guid key, object value)
    {
        if (_codec == null) return;
        try
        {
            object v = value;
            int hr = _codec.SetValue(ref key, ref v);
            if (hr != 0) Log.Debug($"Encoder setting {key} not applied ({hr:X8})");
        }
        catch (Exception ex) { Log.Debug($"Encoder setting {key}: {ex.Message}"); }
    }

    public void ForceKeyframe() => TrySet(CODECAPI_AVEncVideoForceKeyFrame, 1u);

    /// <summary>Encodes one BGRA frame; returns the access units that came out (usually one, sometimes none at first).</summary>
    public List<H264.AccessUnit> Encode(IntPtr bgra)
    {
        BgraToNv12((byte*)bgra, Width, Height, _nv12);
        fixed (byte* p = _nv12) return EncodeNv12((IntPtr)p, _nv12.Length);
    }

    /// <summary>Encodes one NV12 frame (Width×Height, rows packed, chroma plane straight after).</summary>
    public List<H264.AccessUnit> EncodeNv12(IntPtr nv12, int length)
    {
        Mf.Check(Mf.MFCreateMemoryBuffer((uint)length, out var buf), "buffer");
        buf.Lock(out IntPtr p, out _, out _);
        Buffer.MemoryCopy((void*)nv12, (void*)p, length, length);
        buf.Unlock();
        buf.SetCurrentLength((uint)length);
        Mf.Check(Mf.MFCreateSample(out var sample), "sample");
        sample.AddBuffer(buf);
        long dur = 10_000_000L / Fps;
        sample.SetSampleTime(_frameIndex * dur);
        sample.SetSampleDuration(dur);
        _frameIndex++;
        var result = new List<H264.AccessUnit>();
        int hr = _mft.ProcessInput(0, sample, 0);
        if (hr == unchecked((int)0xC00D36B5)) { Drain(result); hr = _mft.ProcessInput(0, sample, 0); }
        Marshal.ReleaseComObject(sample);
        Marshal.ReleaseComObject(buf);
        if (hr != 0) throw new InvalidOperationException($"H.264 encoder rejected a frame ({hr:X8})");
        Drain(result);
        return result;
    }

    private void Drain(List<H264.AccessUnit> result)
    {
        while (true)
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
                if (hr == unchecked((int)0xC00D6D72)) return; // need more input
                if (hr == unchecked((int)0xC00D6D61)) continue; // stream change: try again
                if (hr != 0) throw new InvalidOperationException($"H.264 encoder output error ({hr:X8})");
                var s = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
                bool clean = s.GetUINT32(MFSampleExtension_CleanPoint, out uint cp) == 0 && cp != 0;
                s.ConvertToContiguousBuffer(out var cb);
                cb.Lock(out IntPtr p, out _, out uint len);
                var annexB = new byte[len];
                Marshal.Copy(p, annexB, 0, (int)len);
                cb.Unlock();
                Marshal.ReleaseComObject(cb);
                if (!ReferenceEquals(s, outSample)) Marshal.ReleaseComObject(s);

                var au = _units.Build(annexB, clean);
                if (au != null) result.Add(au);
            }
            finally
            {
                if (odb.pSample != IntPtr.Zero) Marshal.Release(odb.pSample);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
                if (outBuf != null) Marshal.ReleaseComObject(outBuf);
            }
        }
    }


    /// <summary>BGRA -> NV12 (BT.709, video range), two rows at a time in parallel.</summary>
    private static void BgraToNv12(byte* src, int w, int h, byte[] dst)
    {
        fixed (byte* d = dst)
        {
            byte* yPlane = d, uv = d + w * h;
            byte* dy = yPlane, duv = uv;
            Parallel.For(0, h / 2, row2 =>
            {
                for (int r = 0; r < 2; r++)
                {
                    int y = row2 * 2 + r;
                    byte* s = src + (long)y * w * 4;
                    byte* yo = dy + (long)y * w;
                    for (int x = 0; x < w; x++, s += 4)
                        yo[x] = (byte)((47 * s[2] + 157 * s[1] + 16 * s[0] + 128 >> 8) + 16);
                }
                byte* s0 = src + (long)row2 * 2 * w * 4, s1 = s0 + w * 4;
                byte* uvo = duv + (long)row2 * w;
                for (int x = 0; x < w; x += 2)
                {
                    int b = s0[x * 4] + s0[x * 4 + 4] + s1[x * 4] + s1[x * 4 + 4];
                    int g = s0[x * 4 + 1] + s0[x * 4 + 5] + s1[x * 4 + 1] + s1[x * 4 + 5];
                    int rr = s0[x * 4 + 2] + s0[x * 4 + 6] + s1[x * 4 + 2] + s1[x * 4 + 6];
                    uvo[x] = (byte)Math.Clamp(((-26 * rr - 87 * g + 112 * b) >> 10) + 128, 16, 240);
                    uvo[x + 1] = (byte)Math.Clamp(((112 * rr - 102 * g - 10 * b) >> 10) + 128, 16, 240);
                }
            });
        }
    }

    public void Dispose()
    {
        try { _mft.ProcessMessage(0x10000001, IntPtr.Zero); } catch { } // END_STREAMING
        Marshal.ReleaseComObject(_mft);
    }
}

/// <summary>Turns encoder output (Annex B) into access units, making sure the first keyframe carries SPS/PPS.</summary>
internal sealed class AccessUnitBuilder
{
    private byte[] _sps, _pps;
    private bool _configSent;

    public void Remember(List<byte[]> nals)
    {
        foreach (var n in nals)
        {
            if (n.Length == 0) continue;
            if (H264.Type(n) == H264.NalSps) _sps = n;
            if (H264.Type(n) == H264.NalPps) _pps = n;
        }
    }

    public H264.AccessUnit Build(byte[] annexB, bool cleanPoint)
    {
        var nals = H264.SplitAnnexB(annexB);
        Remember(nals);
        var au = H264.FromNals(nals);
        if (cleanPoint) au.Keyframe = true;
        if (au.Keyframe && au.Sps == null && _sps != null && !_configSent) { au.Sps = _sps; au.Pps = _pps; (au.Width, au.Height) = H264.SpsSize(_sps); }
        if (au.Sps != null) _configSent = true;
        return au.Avcc.Length > 0 ? au : null;
    }
}

[ComImport, Guid("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICodecAPI
{
    [PreserveSig] int IsSupported(ref Guid api);
    [PreserveSig] int IsModifiable(ref Guid api);
    void GetParameterRange(); void GetParameterValues(); void GetDefaultValue();
    [PreserveSig] int GetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] out object value);
    [PreserveSig] int SetValue(ref Guid api, [MarshalAs(UnmanagedType.Struct)] ref object value);
}
