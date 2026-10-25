using AirWave.Core.Audio;
using AirWave.Core.Discovery;

namespace AirWave.Core.Streaming;

/// <summary>
/// Speaker test: plays numbered beeps to one speaker in each of the ways AirWave can send audio, recording what the
/// speaker answers to each. Used to find the way a particular speaker (e.g. a HomePod) actually plays.
/// Test n beeps n times, then pauses, so a listener can tell which tests were heard.
/// </summary>
public static class SpeakerLab
{
    public sealed record Method(int Number, string Description, StreamStyle Style, bool SpeakerKeepsTime);

    public static readonly Method[] Methods =
    {
        new(1, "Realtime ALAC, sent 2 s ahead, AirWave keeps time (previous versions)", StreamStyle.RealtimeAhead, false),
        new(2, "Realtime ALAC, sent just in time, AirWave keeps time", StreamStyle.RealtimeJustInTime, false),
        new(3, "Buffered AAC with a play-at time, AirWave keeps time (v0.4.2)", StreamStyle.Buffered, false),
        new(4, "Buffered AAC, play-at time sent once clocks have settled", StreamStyle.BufferedLateAnchor, false),
        new(5, "Realtime ALAC, sent 2 s ahead, the speaker keeps time", StreamStyle.RealtimeAhead, true),
        new(6, "Realtime ALAC, sent just in time, the speaker keeps time", StreamStyle.RealtimeJustInTime, true),
        new(7, "Buffered AAC with a play-at time, the speaker keeps time", StreamStyle.Buffered, true),
    };

    public sealed class Result
    {
        public Method Method;
        public string State, Feedback, AnchorReply, Ptp, Error;
        public override string ToString() =>
            $"Test {Method.Number} ({Method.Description}): {State}{(Error != null ? " – " + Error : "")}; " +
            $"speaker says {Feedback ?? "-"}{(AnchorReply != null ? $"; play-at reply {AnchorReply}" : "")}; PTP: {Ptp}";
    }

    /// <param name="progress">(test number, status line) as it goes.</param>
    public static async Task<List<Result>> RunAsync(StreamEngine engine, SpeakerInfo speaker, double volume, int secondsEach,
        Action<int, string> progress, CancellationToken ct)
    {
        var results = new List<Result>();
        var oldSource = engine.Source;
        var oldOverride = engine.StyleOverride;
        bool oldYield = engine.Ptp?.Yield ?? false;
        var oldLevel = Log.MinLevel;
        Log.MinLevel = LogLevel.Debug;
        Log.Info($"Speaker test on {speaker.Name} ({speaker.Model}, {speaker.PreferredAddress}) starting");
        try
        {
            await engine.RemoveAllAsync();
            foreach (var m in Methods)
            {
                ct.ThrowIfCancellationRequested();
                var r = new Result { Method = m };
                progress(m.Number, $"Test {m.Number} of {Methods.Length}: listen for {m.Number} beep{(m.Number > 1 ? "s" : "")} at a time…");
                Log.Info($"Speaker test {m.Number}: {m.Description}");
                engine.Source = new NumberedBeeps(m.Number);
                engine.StyleOverride = _ => m.Style;
                if (engine.Ptp != null) engine.Ptp.Yield = m.SpeakerKeepsTime;
                if (m.SpeakerKeepsTime) await Task.Delay(1500, ct); // let the speaker take over as timekeeper
                try
                {
                    var s = await engine.AddSpeakerAsync(speaker, volume, ct);
                    r.State = s.State.ToString();
                    if (s.State == SpeakerState.Failed) r.Error = s.ProblemText;
                    else
                    {
                        await Task.Delay(secondsEach * 1000, ct);
                        r.State = s.State.ToString();
                        r.Feedback = s.LastFeedback;
                        r.AnchorReply = s.AnchorReply;
                        r.Ptp = s.Address != null ? engine.Ptp?.DescribePeer(s.Address) : null;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { r.Error = ex.Message; }
                finally
                {
                    await engine.RemoveAllAsync();
                }
                results.Add(r);
                Log.Info("Speaker test result: " + r);
                await Task.Delay(2500, ct);
            }
        }
        finally
        {
            await engine.RemoveAllAsync();
            engine.StyleOverride = oldOverride;
            if (engine.Ptp != null) engine.Ptp.Yield = oldYield;
            engine.Source = oldSource;
            Log.MinLevel = oldLevel;
            Log.Info($"Speaker test on {speaker.Name} finished");
        }
        return results;
    }

    /// <summary>n beeps (0.12 s each, 0.25 s apart), then a pause; repeats every 3 s.</summary>
    private sealed class NumberedBeeps : IAudioSource
    {
        private readonly int _n;
        private long _pos;
        public NumberedBeeps(int n) { _n = n; }
        public float Level { get; private set; }
        public void Read(Span<short> buf, int frames)
        {
            float peak = 0;
            for (int i = 0; i < frames; i++, _pos++)
            {
                double t = _pos % (3 * 44100) / 44100.0;
                int k = (int)(t / 0.25);
                bool on = k < _n && t - k * 0.25 < 0.12;
                short v = on ? (short)(9000 * Math.Sin(2 * Math.PI * 880 * _pos / 44100.0)) : (short)0;
                buf[2 * i] = buf[2 * i + 1] = v;
                if (on) peak = 0.3f;
            }
            Level = peak;
        }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
