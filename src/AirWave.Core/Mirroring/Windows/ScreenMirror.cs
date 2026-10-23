using System.Diagnostics;
using System.Runtime.Versioning;
using AirWave.Core.Audio;
using AirWave.Core.Timing;

namespace AirWave.Core.Mirroring.Windows;

/// <summary>
/// Runs a mirroring session: captures the screen 60 times a second on the graphics card (or 30 with the standard capture), encodes it and sends it, and (optionally)
/// sends the PC's sound with it, all stamped with capture time so picture and sound line up on the TV.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenMirror : IDisposable
{
    private readonly MirrorSession _session;
    private readonly IAudioSource _audio;
    private readonly bool _useGpu;
    private Thread _video, _sound;
    private volatile bool _run;
    public double EncodeMs { get; private set; }
    public int FramesSent { get; private set; }
    public string Resolution { get; private set; }
    public int Fps { get; private set; }
    /// <summary>"Graphics card" or "Processor (software)", once running.</summary>
    public string Encoder { get; private set; }
    /// <summary>Full description of the capture and encoder in use.</summary>
    public string Details { get; private set; }
    public string LastError { get; private set; }
    /// <summary>Raised (on the video thread) when the capture/encoder in use is chosen or changes.</summary>
    public event Action PipelineChanged;

    /// <param name="audio">The PC's sound (already started), or null for picture only.</param>
    /// <param name="useGpu">Capture and encode on the graphics card (full resolution, 60 fps) when it can.</param>
    public ScreenMirror(MirrorSession session, IAudioSource audio, bool useGpu = true)
    {
        _session = session;
        _audio = audio;
        _useGpu = useGpu;
    }

    public void Start()
    {
        _run = true;
        _video = new Thread(VideoLoop) { IsBackground = true, Name = "AirWave mirror video", Priority = ThreadPriority.AboveNormal };
        _video.Start();
        if (_audio != null && _session.HasAudio)
        {
            _sound = new Thread(SoundLoop) { IsBackground = true, Name = "AirWave mirror sound", Priority = ThreadPriority.Highest };
            _sound.Start();
        }
    }

    private IMirrorPipeline Choose(bool gpu)
    {
        var (maxW, maxH) = _session.Display ?? (1920, 1080);
        if (maxW <= 0 || maxH <= 0) (maxW, maxH) = (1920, 1080);
        if (gpu)
        {
            try { return new GpuPipeline(maxW, maxH, hardwareEncoder: true); }
            catch (Exception ex) { Log.Info($"Graphics card capture not available ({ex.Message}); using the standard capture"); }
        }
        return new SoftwarePipeline(maxW, maxH);
    }

    private void VideoLoop()
    {
        IMirrorPipeline pipe = null;
        timeBeginPeriod(1); // 1 ms sleeps, for an even 60 fps
        try
        {
            bool gpu = _useGpu;
            var sw = new Stopwatch();
            while (_run && _session.IsOpen)
            {
                pipe = Choose(gpu);
                pipe.Output += Send;
                Resolution = $"{pipe.Width}×{pipe.Height}";
                Fps = pipe.Fps;
                Encoder = pipe.EncoderKind;
                Details = pipe.Description;
                Log.Info($"Mirroring: {pipe.Description}");
                PipelineChanged?.Invoke();

                long frameNs = 1_000_000_000L / pipe.Fps;
                long next = MonoClock.NowNs;
                while (_run && _session.IsOpen && !pipe.Broken)
                {
                    long now = MonoClock.NowNs;
                    if (now < next) { Thread.Sleep((int)Math.Max(1, (next - now) / 1_000_000)); continue; }
                    // Fell far behind (slow PC)? Skip ahead rather than queue up
                    next = now - next > frameNs * 3 ? now + frameNs : next + frameNs;
                    sw.Restart();
                    pipe.Tick(MonoClock.NowNs);
                    EncodeMs = EncodeMs * 0.95 + sw.Elapsed.TotalMilliseconds * 0.05;
                }
                pipe.Output -= Send;
                pipe.Dispose();
                pipe = null;
                if (gpu) Log.Warn("Graphics card mirroring failed; switching to the standard capture");
                gpu = false; // anything after a failure uses the path that works everywhere
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // session ended
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Error($"Mirroring stopped: {ex.Message}");
            _session.Dispose();
        }
        finally
        {
            pipe?.Dispose();
            timeEndPeriod(1);
        }
    }

    private void Send(H264.AccessUnit au, long captured)
    {
        try
        {
            if (au.Sps != null && au.Pps != null) _session.SetCodecConfig(H264.AvcC(au.Sps, au.Pps), au.Width, au.Height);
            _session.SendFrame(au.Avcc, au.Keyframe, captured);
            FramesSent++;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }

    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [System.Runtime.InteropServices.DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);

    private void SoundLoop()
    {
        var pcm = new short[352 * 2];
        long start = MonoClock.NowNs;
        long sent = 0;
        // The capture keeps about 100 ms in hand, so what we read now was heard ~100 ms ago
        const long CaptureDelayNs = 100_000_000L;
        while (_run && _session.IsOpen)
        {
            long due = (MonoClock.NowNs - start) * 44100 / 1_000_000_000L;
            while (sent + 352 <= due && _run)
            {
                _audio.Read(pcm, 352);
                _session.SendAudio(pcm, start + sent * 1_000_000_000L / 44100 - CaptureDelayNs);
                sent += 352;
            }
            Thread.Sleep(3);
        }
    }

    public void Dispose()
    {
        _run = false;
        _video?.Join(1500);
        _sound?.Join(500);
        _session.Dispose();
    }
}
