using System.Net;
using AirWave.Core;
using AirWave.Core.Audio;
using AirWave.Core.Discovery;
using AirWave.Core.Streaming;

// AirWave test tool: discover speakers, or stream a test tone / raw PCM file to one or more speakers.
//   discover [seconds]
//   play <ip:port>[,<ip:port>...] [--seconds N] [--ntp] [--pcm file.raw] [--debug]
Log.Written += (_, line) => Console.WriteLine(line);
if (args.Contains("--debug")) Log.MinLevel = LogLevel.Debug;

if (args.Length > 0 && args[0] == "receive") { Environment.ExitCode = await ReceiveCommand.Run(args); return; }
if (args.Length > 0 && args[0] == "send-buffered") { Environment.ExitCode = await SendBufferedCommand.Run(args); return; }
if (args.Length > 0 && args[0] == "unit") { Environment.ExitCode = UnitChecks.Run(); return; }

if (args.Length == 0 || args[0] == "discover")
{
    int secs = args.Length > 1 && int.TryParse(args[1], out var s) ? s : 8;
    using var b = new MdnsBrowser();
    b.Start();
    await Task.Delay(secs * 1000);
    foreach (var sp in b.Speakers) Console.WriteLine($"FOUND {sp.Name} | {sp.Kind} | {string.Join(",", sp.Addresses)}:{sp.Port} | ptp={sp.SupportsPtp} ap2={sp.SupportsAirPlay2Pairing}");
    return;
}

if (args[0] == "play")
{
    int seconds = 10;
    string pcm = null;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "--seconds") seconds = int.Parse(args[++i]);
        if (args[i] == "--pcm") pcm = args[++i];
    }
    await using var engine = new StreamEngine(enablePtp: !args.Contains("--ntp")) { ClientName = "AirWave Test", ForceNtp = args.Contains("--ntp") };
    if (args.Contains("--delay")) engine.DelayMs = int.Parse(args[Array.IndexOf(args, "--delay") + 1]);
    engine.Source = pcm != null ? new RawPcmSource(pcm) : new ToneSource();
    int stagger = args.Contains("--stagger") ? int.Parse(args[Array.IndexOf(args, "--stagger") + 1]) : 0;
    var tasks = new List<Task<SpeakerSession>>();
    foreach (var t in args[1].Split(','))
    {
        if (tasks.Count > 0 && stagger > 0) await Task.Delay(stagger * 1000);
        var ep = IPEndPoint.Parse(t);
        var info = new SpeakerInfo { Id = t, Name = t, Port = ep.Port, Features = (1UL << 9) | (1UL << 48) | (args.Contains("--ntp") ? 0 : 1UL << 41) };
        info.Addresses.Add(ep.Address);
        tasks.Add(engine.AddSpeakerAsync(info, 0.8));
    }
    var sessions = await Task.WhenAll(tasks);
    foreach (var s in sessions) Console.WriteLine($"SESSION {s.Info.Name}: {s.State} {s.ProblemText}");
    if (sessions.All(s => s.State == SpeakerState.Failed)) { Environment.ExitCode = 2; return; }
    await Task.Delay(seconds * 1000);
    foreach (var s in engine.Sessions) Console.WriteLine($"END {s.Info.Name}: {s.State}");
    return;
}

sealed class RawPcmSource : IAudioSource
{
    private readonly byte[] _data; private int _pos;
    public RawPcmSource(string path) { _data = File.ReadAllBytes(path); }
    public float Level => 0;
    public void Read(Span<short> buf, int frames)
    {
        for (int i = 0; i < frames * 2; i++)
        {
            if (_pos + 1 >= _data.Length) { buf[i] = 0; continue; }
            buf[i] = (short)(_data[_pos] | (_data[_pos + 1] << 8)); _pos += 2;
        }
    }
    public void Start() { } public void Stop() { } public void Dispose() { }
}
