using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Audio;

/// <summary>
/// Encodes 44.1 kHz stereo PCM to raw AAC-LC frames (1024 samples each), for AirPlay 2 "buffered" audio
/// (what iPhones send to HomePods and Apple TVs). Frames come out in order; output frame k carries input
/// samples from k*1024 - <see cref="DelaySamples"/>, the encoder's start-up delay.
/// </summary>
public interface IAacEncoder : IDisposable
{
    int DelaySamples { get; }
    /// <summary>Feeds 1024 stereo frames; returns any AAC frames that came out (possibly none, or more than one).</summary>
    List<byte[]> Encode(ReadOnlySpan<short> pcm);
    string Name { get; }
}

public static class AacEncoder
{
    /// <summary>Optional override (tests).</summary>
    public static Func<IAacEncoder> Factory;

    public static IAacEncoder Create()
    {
        if (Factory != null) return Factory();
        if (OperatingSystem.IsWindows()) return new MediaFoundationAacEncoder();
        return new FfmpegAacEncoder();
    }

    /// <summary>
    /// Measures an encoder's start-up delay: encodes a click, decodes it and sees where it lands.
    /// Returns null if it couldn't tell.
    /// </summary>
    public static int? MeasureDelay(IAacEncoder enc, IAacDecoder dec, int clickAt = 3000)
    {
        var decoded = new List<short>();
        dec.Decoded += (_, _, pcm, frames) => { lock (decoded) decoded.AddRange(pcm.AsSpan(0, frames * 2).ToArray()); };
        var block = new short[1024 * 2];
        uint rtp = 0;
        for (int b = 0; b < 12; b++)
        {
            Array.Clear(block);
            int pos = clickAt - b * 1024;
            if (pos >= 0 && pos < 1024) { block[pos * 2] = 20000; block[pos * 2 + 1] = 20000; }
            foreach (var f in enc.Encode(block)) { dec.Submit(rtp, rtp, f); rtp += 1024; }
        }
        // Encoders that run in another process deliver late: keep feeding silence until enough has come out
        for (int t = 0; t < 60; t++)
        {
            lock (decoded) if (decoded.Count >= 8 * 1024 * 2) break;
            Array.Clear(block);
            foreach (var f in enc.Encode(block)) { dec.Submit(rtp, rtp, f); rtp += 1024; }
            Thread.Sleep(50);
        }
        short[] all;
        lock (decoded) all = decoded.ToArray();
        int best = -1, bestAbs = 0;
        for (int i = 0; i < all.Length / 2; i++) { int a = Math.Abs((int)all[2 * i]); if (a > bestAbs) { bestAbs = a; best = i; } }
        if (best < 0 || bestAbs < 2000) return null;
        return best - clickAt;
    }

    /// <summary>Splits an ADTS stream into raw frames (header removed).</summary>
    internal static bool TryTakeAdts(List<byte> buf, out byte[] frame)
    {
        frame = null;
        // find sync
        int i = 0;
        while (i + 1 < buf.Count && !(buf[i] == 0xFF && (buf[i + 1] & 0xF6) == 0xF0)) i++;
        if (i > 0) buf.RemoveRange(0, i);
        if (buf.Count < 7) return false;
        bool crc = (buf[1] & 1) == 0;
        int len = ((buf[3] & 3) << 11) | (buf[4] << 3) | (buf[5] >> 5);
        int hdr = crc ? 9 : 7;
        if (len < hdr) { buf.RemoveAt(0); return false; }
        if (buf.Count < len) return false;
        frame = buf.GetRange(hdr, len - hdr).ToArray();
        buf.RemoveRange(0, len);
        return true;
    }
}

// =====================================================================================================
// Windows: the Media Foundation AAC encoder that ships with Windows
// =====================================================================================================

[SupportedOSPlatform("windows")]
public sealed class MediaFoundationAacEncoder : IAacEncoder
{
    private static readonly Guid CLSID_AACMFTEncoder = new("93AF0C51-2275-45d2-A35B-F2BA21CAED00");
    private static readonly Guid MF_MT_AUDIO_AVG_BYTES_PER_SECOND = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    private static readonly Guid MF_MT_AUDIO_BLOCK_ALIGNMENT = new("322de230-9eeb-43bd-ab7a-ff412251541d");

    /// <summary>Start-up delay of the Windows AAC encoder: measured once (encode a click, decode it with Windows' decoder).</summary>
    private static readonly Lazy<int> Measured = new(() =>
    {
        try
        {
            using var e = new MediaFoundationAacEncoder(measuring: true);
            using var d = new MediaFoundationAacDecoder(44100, 2);
            int? m = AacEncoder.MeasureDelay(e, d);
            Log.Debug($"Windows AAC encoder start-up delay measured as {m?.ToString() ?? "unknown"} samples");
            if (m is int v && v >= 0 && v <= 4096) return v;
        }
        catch (Exception ex) { Log.Debug($"AAC delay measurement failed: {ex.Message}"); }
        return 1024;
    });
    public int DelaySamples => _measuring ? 0 : Measured.Value;
    private readonly bool _measuring;
    public string Name => "Windows AAC encoder";

    private IMFTransform _mft;
    private bool _providesSamples;
    private int _outSize;
    private long _time;
    private static int _mfStarted;

    public MediaFoundationAacEncoder(int bytesPerSecond = 24000, bool measuring = false)
    {
        _measuring = measuring;
        if (Interlocked.Exchange(ref _mfStarted, 1) == 0) Mf.Check(Mf.MFStartup(0x00020070, 0), "MFStartup");
        var clsid = CLSID_AACMFTEncoder;
        var iid = typeof(IMFTransform).GUID;
        Mf.Check(Mf.CoCreateInstance(ref clsid, IntPtr.Zero, 1, ref iid, out var obj), "create AAC encoder (is Media Foundation installed?)");
        _mft = (IMFTransform)obj;

        Mf.Check(Mf.MFCreateMediaType(out var outType), "media type");
        outType.SetGUID(Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Audio);
        outType.SetGUID(Mf.MF_MT_SUBTYPE, Mf.MFAudioFormat_AAC);
        outType.SetUINT32(Mf.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        outType.SetUINT32(Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
        outType.SetUINT32(Mf.MF_MT_AUDIO_NUM_CHANNELS, 2);
        outType.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)bytesPerSecond); // 24000 = 192 kbit/s, the most it offers
        outType.SetUINT32(Mf.MF_MT_AAC_PAYLOAD_TYPE, 0);                       // raw frames
        outType.SetUINT32(Mf.MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29);  // AAC-LC
        Mf.Check(_mft.SetOutputType(0, outType, 0), "AAC encoder output type");
        Marshal.ReleaseComObject(outType);

        Mf.Check(Mf.MFCreateMediaType(out var inType), "media type");
        inType.SetGUID(Mf.MF_MT_MAJOR_TYPE, Mf.MFMediaType_Audio);
        inType.SetGUID(Mf.MF_MT_SUBTYPE, Mf.MFAudioFormat_PCM);
        inType.SetUINT32(Mf.MF_MT_AUDIO_BITS_PER_SAMPLE, 16);
        inType.SetUINT32(Mf.MF_MT_AUDIO_SAMPLES_PER_SECOND, 44100);
        inType.SetUINT32(Mf.MF_MT_AUDIO_NUM_CHANNELS, 2);
        inType.SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, 4);
        inType.SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, 44100 * 4);
        Mf.Check(_mft.SetInputType(0, inType, 0), "AAC encoder input type");
        Marshal.ReleaseComObject(inType);

        Mf.Check(_mft.GetOutputStreamInfo(0, out var info), "output info");
        _providesSamples = (info.dwFlags & 0x300) != 0;
        _outSize = Math.Max((int)info.cbSize, 8192);
        _mft.ProcessMessage(0x10000000, IntPtr.Zero);
        _mft.ProcessMessage(0x10000003, IntPtr.Zero);
    }

    public List<byte[]> Encode(ReadOnlySpan<short> pcm)
    {
        var result = new List<byte[]>();
        int bytes = pcm.Length * 2;
        Mf.Check(Mf.MFCreateMemoryBuffer((uint)bytes, out var buf), "buffer");
        buf.Lock(out IntPtr p, out _, out _);
        unsafe { fixed (short* s = pcm) Buffer.MemoryCopy(s, (void*)p, bytes, bytes); }
        buf.Unlock();
        buf.SetCurrentLength((uint)bytes);
        Mf.Check(Mf.MFCreateSample(out var sample), "sample");
        sample.AddBuffer(buf);
        long dur = pcm.Length / 2 * 10_000_000L / 44100;
        sample.SetSampleTime(_time);
        sample.SetSampleDuration(dur);
        _time += dur;
        int hr = _mft.ProcessInput(0, sample, 0);
        if (hr == unchecked((int)0xC00D36B5)) { Drain(result); hr = _mft.ProcessInput(0, sample, 0); }
        Marshal.ReleaseComObject(sample);
        Marshal.ReleaseComObject(buf);
        if (hr != 0) throw new InvalidOperationException($"AAC encoder rejected audio ({hr:X8})");
        Drain(result);
        return result;
    }

    private void Drain(List<byte[]> result)
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
                if (hr == unchecked((int)0xC00D6D72)) return;
                if (hr != 0) throw new InvalidOperationException($"AAC encoder output error ({hr:X8})");
                var s = (IMFSample)Marshal.GetObjectForIUnknown(odb.pSample);
                s.ConvertToContiguousBuffer(out var cb);
                cb.Lock(out IntPtr p, out _, out uint len);
                var frame = new byte[len];
                Marshal.Copy(p, frame, 0, (int)len);
                cb.Unlock();
                Marshal.ReleaseComObject(cb);
                if (!ReferenceEquals(s, outSample)) Marshal.ReleaseComObject(s);
                if (len > 0) result.Add(frame);
            }
            finally
            {
                if (odb.pSample != IntPtr.Zero) Marshal.Release(odb.pSample);
                if (outSample != null) Marshal.ReleaseComObject(outSample);
                if (outBuf != null) Marshal.ReleaseComObject(outBuf);
            }
        }
    }

    public void Dispose()
    {
        if (_mft != null) { try { _mft.ProcessMessage(0x10000001, IntPtr.Zero); } catch { } Marshal.ReleaseComObject(_mft); _mft = null; }
    }
}

// =====================================================================================================
// Elsewhere (tests on Linux): ffmpeg as a streaming encoder
// =====================================================================================================

internal sealed class FfmpegAacEncoder : IAacEncoder
{
    private readonly Process _p;
    private readonly ConcurrentQueue<byte[]> _out = new();
    public int DelaySamples => 1024; // ffmpeg's AAC encoder primes with one frame
    public string Name => "ffmpeg AAC encoder";

    public FfmpegAacEncoder()
    {
        _p = Process.Start(new ProcessStartInfo("ffmpeg",
            "-hide_banner -loglevel error -probesize 32 -analyzeduration 0 -f s16le -ar 44100 -ac 2 -i pipe:0 -c:a aac -b:a 256k -flush_packets 1 -f adts pipe:1")
        { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false })
            ?? throw new InvalidOperationException("ffmpeg not available");
        new Thread(ReadLoop) { IsBackground = true, Name = "aac-enc-ffmpeg" }.Start();
    }

    public List<byte[]> Encode(ReadOnlySpan<short> pcm)
    {
        var bytes = new byte[pcm.Length * 2];
        MemoryMarshal.AsBytes(pcm).CopyTo(bytes);
        _p.StandardInput.BaseStream.Write(bytes);
        _p.StandardInput.BaseStream.Flush();
        var result = new List<byte[]>();
        while (_out.TryDequeue(out var f)) result.Add(f);
        return result;
    }

    private void ReadLoop()
    {
        var s = _p.StandardOutput.BaseStream;
        var buf = new byte[8192];
        var acc = new List<byte>();
        try
        {
            int n;
            while ((n = s.Read(buf, 0, buf.Length)) > 0)
            {
                acc.AddRange(buf.AsSpan(0, n).ToArray());
                while (AacEncoder.TryTakeAdts(acc, out var frame)) _out.Enqueue(frame);
            }
        }
        catch { }
    }

    public void Dispose()
    {
        try { _p.StandardInput.Close(); } catch { }
        try { if (!_p.WaitForExit(500)) _p.Kill(); } catch { }
    }
}
