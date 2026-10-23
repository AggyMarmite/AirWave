using System.Diagnostics;
using System.Runtime.Versioning;
using AirWave.Core.Audio;
using AirWave.Core.Timing;

namespace AirWave.Core.Mirroring.Windows;

/// <summary>
/// Runs a mirroring session: captures the screen ~30 times a second, encodes it and sends it, and (optionally)
/// sends the PC's sound with it, all stamped with capture time so picture and sound line up on the TV.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ScreenMirror : IDisposable
{
    private readonly MirrorSession _session;
    private readonly IAudioSource _audio;
    private readonly int _fps;
    private Thread _video, _sound;
    private volatile bool _run;
    public double EncodeMs { get; private set; }
    public int FramesSent { get; private set; }
    public string Resolution { get; private set; }
    public string LastError { get; private set; }

    /// <param name="audio">The PC's sound (already started), or null for picture only.</param>
    public ScreenMirror(MirrorSession session, IAudioSource audio, int fps = 30)
    {
        _session = session;
        _audio = audio;
        _fps = fps;
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

    private void VideoLoop()
    {
        try
        {
            var (maxW, maxH) = _session.Display ?? (1920, 1080);
            if (maxW > 1920 || maxH > 1080) (maxW, maxH) = (1920, 1080); // 4K TVs: 1080p keeps encoding light
            using var cap = new ScreenCapture(maxW, maxH);
            using var enc = new H264Encoder(cap.Width, cap.Height, _fps, BitrateFor(cap.Width, cap.Height));
            Resolution = $"{cap.Width}×{cap.Height}";
            Log.Info($"Mirroring {cap.SourceWidth}×{cap.SourceHeight} screen at {Resolution}, {_fps} fps");
            long frameNs = 1_000_000_000L / _fps;
            long next = MonoClock.NowNs;
            var sw = new Stopwatch();
            while (_run && _session.IsOpen)
            {
                long now = MonoClock.NowNs;
                if (now < next) { Thread.Sleep((int)Math.Max(1, (next - now) / 1_000_000)); continue; }
                // Fell far behind (slow PC)? Skip ahead rather than queue up
                next = now - next > frameNs * 3 ? now + frameNs : next + frameNs;

                long captured = MonoClock.NowNs;
                sw.Restart();
                var bgra = cap.Capture();
                var units = enc.Encode(bgra);
                foreach (var au in units)
                {
                    if (au.Sps != null && au.Pps != null) _session.SetCodecConfig(H264.AvcC(au.Sps, au.Pps), au.Width > 0 ? au.Width : cap.Width, au.Height > 0 ? au.Height : cap.Height);
                    _session.SendFrame(au.Avcc, au.Keyframe, captured);
                    FramesSent++;
                }
                EncodeMs = EncodeMs * 0.95 + sw.Elapsed.TotalMilliseconds * 0.05;
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
    }

    private static int BitrateFor(int w, int h) => w * h >= 1920 * 1000 ? 10_000_000 : w * h >= 1280 * 700 ? 6_000_000 : 4_000_000;

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
