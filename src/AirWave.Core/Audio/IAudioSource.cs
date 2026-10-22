namespace AirWave.Core.Audio;

/// <summary>
/// Supplies 44.1 kHz, 16-bit, stereo PCM to the streamer. The streamer pulls exactly the number
/// of frames it needs on its own clock; a source must always fill the buffer (silence if it has nothing).
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Fills <paramref name="interleaved"/> with <paramref name="frames"/> stereo frames.</summary>
    void Read(Span<short> interleaved, int frames);

    /// <summary>Called once when streaming starts (so a source can start capturing).</summary>
    void Start();

    /// <summary>Called when streaming stops.</summary>
    void Stop();

    /// <summary>Recent peak level 0..1 for the UI meter.</summary>
    float Level { get; }
}

/// <summary>A test tone: a quiet sine, optionally beeping once a second (handy for checking sync by ear).</summary>
public sealed class ToneSource : IAudioSource
{
    private readonly double _freq;
    private readonly bool _beeps;
    private long _pos;
    public float Level { get; private set; }

    public ToneSource(double freq = 440, bool beeps = true)
    {
        _freq = freq;
        _beeps = beeps;
    }

    public void Read(Span<short> buf, int frames)
    {
        float peak = 0;
        for (int i = 0; i < frames; i++, _pos++)
        {
            double t = _pos / 44100.0;
            double env = !_beeps ? 1 : ((_pos % 44100) < 4410 ? 1 : 0); // 100 ms beep each second
            double v = Math.Sin(2 * Math.PI * _freq * t) * 0.25 * env;
            short s = (short)(v * short.MaxValue);
            buf[2 * i] = s;
            buf[2 * i + 1] = s;
            peak = Math.Max(peak, (float)Math.Abs(v));
        }
        Level = peak;
    }

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
