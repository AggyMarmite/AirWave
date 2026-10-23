using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Audio;

/// <summary>
/// Decodes raw AAC-LC frames (1024 samples each). Frames go in tagged with an RTP time and sequence number;
/// decoded PCM comes out through <see cref="Decoded"/> with the same tags, in order. Decoders may hold a
/// frame or two internally, so output can lag input.
/// </summary>
public interface IAacDecoder : IDisposable
{
    void Submit(uint rtp, uint seq, ReadOnlySpan<byte> rawFrame);
    /// <summary>(rtp, seq, interleaved stereo 16-bit, frames)</summary>
    event Action<uint, uint, short[], int> Decoded;
    /// <summary>Forget pending frames (after a flush).</summary>
    void Reset();
}

public static class AacDecoder
{
    /// <summary>Optional override (tests on machines without Media Foundation).</summary>
    public static Func<int, int, IAacDecoder> Factory;

    public static IAacDecoder Create(int sampleRate = 44100, int channels = 2)
    {
        if (Factory != null) return Factory(sampleRate, channels);
        if (OperatingSystem.IsWindows()) return new MediaFoundationAacDecoder(sampleRate, channels);
        return new FfmpegAacDecoder(sampleRate, channels);
    }

    /// <summary>ADTS header for one raw AAC-LC frame.</summary>
    public static byte[] Adts(int payloadLength, int sampleRate, int channels)
    {
        int freqIdx = sampleRate switch { 96000 => 0, 88200 => 1, 64000 => 2, 48000 => 3, 44100 => 4, 32000 => 5, 24000 => 6, 22050 => 7, 16000 => 8, _ => 4 };
        int len = payloadLength + 7;
        const int profile = 2; // AAC LC
        return new byte[]
        {
            0xFF, 0xF1,
            (byte)(((profile - 1) << 6) + (freqIdx << 2) + (channels >> 2)),
            (byte)(((channels & 3) << 6) + (len >> 11)),
            (byte)((len & 0x7FF) >> 3),
            (byte)(((len & 7) << 5) + 0x1F),
            0xFC,
        };
    }
}

// =====================================================================================================
// Windows: the Media Foundation AAC decoder that ships with Windows
// =====================================================================================================

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationAacDecoder : IAacDecoder
{
    private readonly int _rate, _channels;
    private readonly Queue<(uint rtp, uint seq)> _pending = new();
    private readonly object _lock = new();
    private IMFTransform _mft;
    private bool _providesSamples;
    private int _outBufSize;
    private int _outChannels = 2;
    public static string OutputFormat { get; private set; }
    private static int _mfStarted;

    public event Action<uint, uint, short[], int> Decoded;

    public MediaFoundationAacDecoder(int rate, int channels)
    {
        _rate = rate; _channels = channels;
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0)
            Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");
        Create();
    }

    private void Create()
    {
        var clsid = Mf.CLSID_CMSAACDecMFT;
        var iid = typeof(IMFTransform).GUID;
        Mf.Check(Mf.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out var obj), "create AAC decoder");
        _mft = (IMFTransform)obj;

        // Start from the ADTS AAC input type the decoder itself offers, and fill in our stream's details
        // (building the type from scratch is rejected on some Windows versions).
        int setHr = -1;
        for (uint i = 0; i < 16 && setHr != 0; i++)
        {
            if (_mft.GetInputAvailableType(0, i, out var input) != 0) break;
            input.GetGUID(Mf.MF_MT_SUBTYPE, out var sub);
            bool adts = input.GetUINT32(Mf.MF_MT_AAC_PAYLOAD_TYPE, out uint pt) == 0 && pt == 1;
            if (sub == Mf.MFAudioFormat_AAC && adts)
            {
                input.SetUINT32(Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND, (uint)_rate);
                input.SetUINT32(Mf.MF_MT_AUDIO_NUM_CHANNELS, (uint)_channels);
                // (ADTS frames describe themselves; adding our own config data here makes the decoder refuse the type)
                setHr = _mft.SetInputType(0, input, 0);
            }
            Marshal.ReleaseComObject(input);
        }
        Mf.Check(setHr, "AAC input type");

        // Pick the 16-bit PCM output type it offers
        for (uint i = 0; ; i++)
        {
            int hr = _mft.GetOutputAvailableType(0, i, out var t);
            if (hr != 0) throw new InvalidOperationException($"AAC decoder: no 16-bit PCM output ({hr:X8})");
            t.GetGUID(Mf.MF_MT_SUBTYPE, out var sub);
            t.GetUINT32(Mf.MF_MT_AUDIO_BITS_PER_SAMPLE, out uint bits);
            if (sub == Mf.MFAudioFormat_PCM && bits == 16)
            {
                t.GetUINT32(Mf.MF_MT_AUDIO_NUM_CHANNELS, out uint och);
                t.GetUINT32(Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND, out uint orate);
                _outChannels = (int)och;
                OutputFormat = $"{orate} Hz, {och} ch, {bits}-bit";
                Mf.Check(_mft.SetOutputType(0, t, 0), "AAC output type");
                Marshal.ReleaseComObject(t);
                break;
            }
            Marshal.ReleaseComObject(t);
        }

        Mf.Check(_mft.GetOutputStreamInfo(0, out var info), "output info");
        _providesSamples = (info.dwFlags & 0x100) != 0 || (info.dwFlags & 0x200) != 0;
        _outBufSize = Math.Max((int)info.cbSize, 1024 * 2 * _channels * 2);
        _mft.ProcessMessage(0x10000000 /* NOTIFY_BEGIN_STREAMING */, IntPtr.Zero);
        _mft.ProcessMessage(0x10000003 /* NOTIFY_START_OF_STREAM */, IntPtr.Zero);
    }

    public void Submit(uint rtp, uint seq, ReadOnlySpan<byte> raw)
    {
        lock (_lock)
        {
            var adts = AacDecoder.Adts(raw.Length, _rate, _channels);
            var data = new byte[adts.Length + raw.Length];
            adts.CopyTo(data, 0);
            raw.CopyTo(data.AsSpan(adts.Length));

            Mf.Check(Mf.MFCreateMemoryBuffer((uint)data.Length, out var buf), "buffer");
            buf.Lock(out IntPtr p, out _, out _);
            Marshal.Copy(data, 0, p, data.Length);
            buf.Unlock();
            buf.SetCurrentLength((uint)data.Length);
            Mf.Check(Mf.MFCreateSample(out var sample), "sample");
            sample.AddBuffer(buf);

            _pending.Enqueue((rtp, seq));
            int hr = _mft.ProcessInput(0, sample, 0);
            if (hr == unchecked((int)0xC00D36B5)) // MF_E_NOTACCEPTING: drain then retry
            {
                Drain();
                hr = _mft.ProcessInput(0, sample, 0);
            }
            Marshal.ReleaseComObject(sample);
            Marshal.ReleaseComObject(buf);
            if (hr != 0) { _pending.TryDequeue(out _); Log.Debug($"AAC decode input error {hr:X8}"); return; }
            Drain();
        }
    }

    private void Drain()
    {
        while (true)
        {
            IMFSample outSample = null;
            IMFMediaBuffer outBuf = null;
            if (!_providesSamples)
            {
                Mf.MFCreateMemoryBuffer((uint)_outBufSize, out outBuf);
                Mf.MFCreateSample(out outSample);
                outSample.AddBuffer(outBuf);
            }
            var odb = new MftOutputDataBuffer { dwStreamID = 0, pSample = outSample == null ? IntPtr.Zero : Marshal.GetComInterfaceForObject(outSample, typeof(IMFSample)) };
            int hr = _mft.ProcessOutput(0, 1, ref odb, out _);
            if (odb.pEvents != IntPtr.Zero) Marshal.Release(odb.pEvents);
            try
            {
                if (hr == unchecked((int)0xC00D6D72)) return; // MF_E_TRANSFORM_NEED_MORE_INPUT
                if (hr != 0) { Log.Debug($"AAC decode output error {hr:X8}"); return; }
                var s = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
                s.ConvertToContiguousBuffer(out var cb);
                cb.Lock(out IntPtr p, out _, out uint len);
                int frames = (int)len / (2 * _outChannels);
                var raw = new short[frames * _outChannels];
                Marshal.Copy(p, raw, 0, raw.Length);
                cb.Unlock();
                Marshal.ReleaseComObject(cb);
                if (!ReferenceEquals(s, outSample)) Marshal.ReleaseComObject(s);
                var pcm = ToStereo(raw, frames);
                if (_pending.TryDequeue(out var tag)) Decoded?.Invoke(tag.rtp, tag.seq, pcm, frames);
            }
            finally
            {
                if (odb.pSample != IntPtr.Zero) Marshal.Release(odb.pSample);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
                if (outBuf != null) Marshal.ReleaseComObject(outBuf);
            }
        }
    }

    private short[] ToStereo(short[] raw, int frames)
    {
        if (_outChannels == 2) return raw;
        var st = new short[frames * 2];
        for (int i = 0; i < frames; i++) { st[2 * i] = raw[i * _outChannels]; st[2 * i + 1] = raw[i * _outChannels + Math.Min(1, _outChannels - 1)]; }
        return st;
    }

    public void Reset()
    {
        lock (_lock)
        {
            _mft.ProcessMessage(0 /* COMMAND_FLUSH */, IntPtr.Zero);
            _pending.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_mft != null) { Marshal.ReleaseComObject(_mft); _mft = null; }
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputDataBuffer
{
    public uint dwStreamID;
    public IntPtr pSample;
    public uint dwStatus;
    public IntPtr pEvents;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MftOutputStreamInfo
{
    public uint dwFlags;
    public uint cbSize;
    public uint cbAlignment;
}

internal static class Mf
{
    public static readonly Guid CLSID_CMSAACDecMFT = new("32D186A7-218F-4C75-8876-DD77273A8999");
    public static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_AAC = new("00001610-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00AA00389B71");
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    public static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    public static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    public static readonly Guid MF_MT_AAC_PAYLOAD_TYPE = new("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
    public static readonly Guid MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION = new("7632f0e6-9538-4d61-acda-ea29c8c14456");
    public static readonly Guid MF_MT_USER_DATA = new("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"Media Foundation: {what} failed ({hr:X8})");
    }

    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true)] public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);
    [DllImport("ole32.dll", ExactSpelling = true)]
    public static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, int ctx, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}

// IMFAttributes methods first (30), then the type-specific ones. Unused slots keep the vtable order.
[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    void GetDouble();
    [PreserveSig] int GetGUID(in Guid key, out Guid value);
    void GetStringLength(); void GetString(); void GetAllocatedString();
    [PreserveSig] int GetBlobSize(in Guid key, out uint size);
    [PreserveSig] int GetBlob(in Guid key, [Out] byte[] buf, uint bufSize, out uint size);
    void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
    [PreserveSig] int SetUINT64(in Guid key, ulong value);
    void SetDouble();
    [PreserveSig] int SetGUID(in Guid key, in Guid value);
    void SetString();
    [PreserveSig] int SetBlob(in Guid key, byte[] buf, uint size);
    void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    void GetUINT64(); void GetDouble(); void GetGUID();
    void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    void GetSampleFlags(); void SetSampleFlags();
    [PreserveSig] int GetSampleTime(out long time);
    [PreserveSig] int SetSampleTime(long time);
    void GetSampleDuration();
    [PreserveSig] int SetSampleDuration(long duration);
    void GetBufferCount(); void GetBufferByIndex();
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
}

[ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFTransform
{
    void GetStreamLimits(); void GetStreamCount(); void GetStreamIDs(); void GetInputStreamInfo();
    [PreserveSig] int GetOutputStreamInfo(uint stream, out MftOutputStreamInfo info);
    void GetAttributes(); void GetInputStreamAttributes(); void GetOutputStreamAttributes(); void DeleteInputStream(); void AddInputStreams();
    [PreserveSig] int GetInputAvailableType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint stream, IMFMediaType type, uint flags);
    [PreserveSig] int SetOutputType(uint stream, IMFMediaType type, uint flags);
    void GetInputCurrentType(); void GetOutputCurrentType(); void GetInputStatus(); void GetOutputStatus(); void SetOutputBounds(); void ProcessEvent();
    [PreserveSig] int ProcessMessage(uint message, IntPtr param);
    [PreserveSig] int ProcessInput(uint stream, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, ref MftOutputDataBuffer buffers, out uint status);
}

// =====================================================================================================
// Elsewhere (tests on Linux): ffmpeg as a streaming decoder process
// =====================================================================================================

internal sealed class FfmpegAacDecoder : IAacDecoder
{
    private readonly Process _p;
    private readonly int _rate, _channels;
    private readonly ConcurrentQueue<(uint rtp, uint seq)> _pending = new();
    private readonly object _writeLock = new();
    public event Action<uint, uint, short[], int> Decoded;

    public FfmpegAacDecoder(int rate, int channels)
    {
        _rate = rate; _channels = channels;
        _p = Process.Start(new ProcessStartInfo("ffmpeg", "-hide_banner -loglevel error -probesize 32 -f aac -i pipe:0 -flush_packets 1 -f s16le -ac 2 pipe:1")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false,
        }) ?? throw new InvalidOperationException("ffmpeg not available");
        new Thread(ReadLoop) { IsBackground = true, Name = "aac-ffmpeg" }.Start();
    }

    public void Submit(uint rtp, uint seq, ReadOnlySpan<byte> raw)
    {
        lock (_writeLock)
        {
            _pending.Enqueue((rtp, seq));
            var s = _p.StandardInput.BaseStream;
            s.Write(AacDecoder.Adts(raw.Length, _rate, _channels));
            s.Write(raw);
            s.Flush();
        }
    }

    private void ReadLoop()
    {
        var s = _p.StandardOutput.BaseStream;
        var buf = new byte[1024 * 4];
        try
        {
            while (true)
            {
                int got = 0;
                while (got < buf.Length)
                {
                    int n = s.Read(buf, got, buf.Length - got);
                    if (n <= 0) return;
                    got += n;
                }
                var pcm = new short[1024 * 2];
                Buffer.BlockCopy(buf, 0, pcm, 0, buf.Length);
                if (_pending.TryDequeue(out var tag)) Decoded?.Invoke(tag.rtp, tag.seq, pcm, 1024);
            }
        }
        catch { }
    }

    public void Reset() { /* order is preserved by the process; pending tags stay matched */ }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); if (!_p.WaitForExit(500)) _p.Kill(); } catch { }
    }
}
