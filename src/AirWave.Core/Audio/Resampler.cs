namespace AirWave.Core.Audio;

/// <summary>
/// Converts captured float audio (any rate, any channel count) to 44.1 kHz stereo 16-bit,
/// with a tiny adjustable rate trim so the capture device's clock can be kept in step with
/// the streamer's clock without ever running dry or piling up.
/// Uses 4-point cubic (Catmull-Rom) interpolation: cheap and clean for music.
/// </summary>
public sealed class Resampler
{
    private readonly object _lock = new();
    private float[] _hist = new float[8];   // last 4 stereo frames: L0 R0 L1 R1 L2 R2 L3 R3
    private double _frac;                   // position between hist[1] and hist[2]
    private double _ratio;                  // input frames per output frame
    private int _inRate;

    /// <summary>Small correction (e.g. +0.0005 = consume 0.05% faster).</summary>
    public double Trim { get; set; }

    public void Configure(int inputRate)
    {
        lock (_lock)
        {
            _inRate = inputRate;
            _ratio = inputRate / 44100.0;
            _frac = 0;
            Array.Clear(_hist);
        }
    }

    public int InputRate => _inRate;

    /// <summary>
    /// Feeds interleaved float input with <paramref name="channels"/> channels; appends 16-bit stereo output to <paramref name="output"/>.
    /// </summary>
    public void Process(ReadOnlySpan<float> input, int channels, PcmRing output)
    {
        lock (_lock)
        {
            double step = _ratio * (1.0 + Trim);
            int frames = input.Length / channels;
            Span<short> outBuf = stackalloc short[2048];
            int outCount = 0;
            for (int f = 0; f < frames; f++)
            {
                float l = input[f * channels];
                float r = channels > 1 ? input[f * channels + 1] : l;
                if (channels > 2)
                {
                    // Fold centre and surrounds into the stereo pair (simple downmix)
                    float c = input[f * channels + 2] * 0.707f;
                    l += c; r += c;
                    if (channels >= 6) { l += input[f * channels + 4] * 0.5f; r += input[f * channels + 5] * 0.5f; }
                }
                // shift history
                _hist[0] = _hist[2]; _hist[1] = _hist[3];
                _hist[2] = _hist[4]; _hist[3] = _hist[5];
                _hist[4] = _hist[6]; _hist[5] = _hist[7];
                _hist[6] = l; _hist[7] = r;

                // produce every output sample that falls between hist[1] and hist[2] (frames 1..2)
                while (_frac < 1.0)
                {
                    double t = _frac;
                    outBuf[outCount++] = ToShort(Cubic(_hist[0], _hist[2], _hist[4], _hist[6], t));
                    outBuf[outCount++] = ToShort(Cubic(_hist[1], _hist[3], _hist[5], _hist[7], t));
                    if (outCount >= outBuf.Length) { output.Write(outBuf[..outCount]); outCount = 0; }
                    _frac += step;
                }
                _frac -= 1.0;
            }
            if (outCount > 0) output.Write(outBuf[..outCount]);
        }
    }

    private static float Cubic(float y0, float y1, float y2, float y3, double t)
    {
        double a = -0.5 * y0 + 1.5 * y1 - 1.5 * y2 + 0.5 * y3;
        double b = y0 - 2.5 * y1 + 2 * y2 - 0.5 * y3;
        double c = -0.5 * y0 + 0.5 * y2;
        return (float)(((a * t + b) * t + c) * t + y1);
    }

    private static short ToShort(float v)
    {
        int s = (int)Math.Round(v * 32767f);
        return (short)Math.Clamp(s, short.MinValue, short.MaxValue);
    }
}

/// <summary>A lock-protected ring of interleaved stereo 16-bit samples.</summary>
public sealed class PcmRing
{
    private readonly short[] _buf;
    private int _read, _count;
    private readonly object _lock = new();

    public PcmRing(int capacitySamples) { _buf = new short[capacitySamples]; }

    public int CountFrames { get { lock (_lock) return _count / 2; } }

    public void Write(ReadOnlySpan<short> data)
    {
        lock (_lock)
        {
            foreach (var s in data)
            {
                if (_count == _buf.Length) { _read = (_read + 1) % _buf.Length; _count--; } // drop oldest
                _buf[(_read + _count) % _buf.Length] = s;
                _count++;
            }
        }
    }

    /// <summary>Reads up to frames; returns frames actually read.</summary>
    public int Read(Span<short> dst, int frames)
    {
        lock (_lock)
        {
            int n = Math.Min(frames * 2, _count) & ~1;
            for (int i = 0; i < n; i++) dst[i] = _buf[(_read + i) % _buf.Length];
            _read = (_read + n) % _buf.Length;
            _count -= n;
            return n / 2;
        }
    }

    public void DropFrames(int frames)
    {
        lock (_lock)
        {
            int n = Math.Min(frames * 2, _count) & ~1;
            _read = (_read + n) % _buf.Length;
            _count -= n;
        }
    }

    public void Clear() { lock (_lock) { _read = 0; _count = 0; } }
}

/// <summary>
/// Wraps a push-style capture (samples arrive whenever the sound card delivers them) as a pull-style
/// <see cref="IAudioSource"/>. Keeps a small cushion of audio and nudges the resampler so that cushion
/// stays put even though the sound card clock and the PC clock tick at slightly different speeds.
/// </summary>
public abstract class CapturedSource : IAudioSource
{
    protected readonly Resampler Resampler = new();
    protected readonly PcmRing Ring = new(44100 * 2 * 2); // 2 s
    private int TargetFrames = 44100 * 100 / 1000;  // aim to hold ~100 ms
    /// <summary>How much audio to keep in hand, ms. Less means lower delay but more risk of gaps (screen mirroring uses less).</summary>
    public int CushionMs { get => TargetFrames * 1000 / 44100; set => TargetFrames = Math.Clamp(value, 20, 500) * 44100 / 1000; }
    private bool _primed;
    private float _level;
    public float Level => _level;

    public void Read(Span<short> buf, int frames)
    {
        int have = Ring.CountFrames;
        if (!_primed)
        {
            if (have < TargetFrames) { buf[..(frames * 2)].Clear(); _level = 0; return; }
            _primed = true;
        }
        if (have > TargetFrames * 6) Ring.DropFrames(have - TargetFrames); // big pile-up (e.g. after a hiccup): drop to target

        int got = Ring.Read(buf, frames);
        if (got < frames)
        {
            buf.Slice(got * 2, (frames - got) * 2).Clear();
            if (got == 0) _primed = false; // ran dry: wait for a fresh cushion
        }

        // Gentle clock-drift correction: proportional to how far the cushion is from target (max ±0.2%)
        double err = (Ring.CountFrames - TargetFrames) / (double)TargetFrames;
        Resampler.Trim = Math.Clamp(err * 0.002, -0.002, 0.002); // too much queued -> consume input a touch faster

        float peak = 0;
        for (int i = 0; i < frames * 2; i++) { float a = Math.Abs(buf[i] / 32768f); if (a > peak) peak = a; }
        _level = _level * 0.7f + peak * 0.3f;
    }

    public abstract void Start();
    public abstract void Stop();
    public virtual void Dispose() => Stop();
}
