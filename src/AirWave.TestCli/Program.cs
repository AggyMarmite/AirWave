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
if (args.Length > 0 && args[0] == "mock-tv") { Environment.ExitCode = await MockTvCommand.Run(args); return; }
if (args.Length > 0 && args[0] == "mirror-test") { Environment.ExitCode = await MirrorTestCommand.Run(args); return; }
if (args.Length > 0 && args[0] == "capture-test" && OperatingSystem.IsWindows()) { Environment.ExitCode = CaptureTestCommand.Run(args); return; }
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

if (args[0] == "lab")
{
    // Speaker test against one speaker: "lab ip:port [--homepod] [--seconds N]"
    var ep = IPEndPoint.Parse(args[1]);
    await using var engine = new StreamEngine(enablePtp: true) { ClientName = "AirWave Test", DelayMs = 2000 };
    var info = new SpeakerInfo { Id = args[1], Name = args[1], Port = ep.Port, Features = (1UL << 9) | (1UL << 48) | (1UL << 40) | (1UL << 41) };
    if (args.Contains("--homepod")) info.Model = "AudioAccessory5,1";
    info.Addresses.Add(ep.Address);
    int secs = args.Contains("--seconds") ? int.Parse(args[Array.IndexOf(args, "--seconds") + 1]) : 8;
    var results = await AirWave.Core.Streaming.SpeakerLab.RunAsync(engine, info, 0.8, secs,
        (n, line) => Console.WriteLine($"LABTEST {n} monotonic {System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F6} {line}"),
        CancellationToken.None);
    foreach (var r in results) Console.WriteLine("RESULT " + r);
    return;
}

if (args[0] == "ptp-yield")
{
    // PTP to a mixed group: a speaker we keep time for gets our strong Announce plus Sync/Follow_Up; a HomePod (which
    // keeps time itself) gets a weak Announce (priority 250) and no Sync. Peers on 127.0.0.2 and 127.0.0.3.
    var a = IPAddress.Parse("127.0.0.2"); var h = IPAddress.Parse("127.0.0.3");
    var socks = new[] { (a, 319), (a, 320), (h, 319), (h, 320) }.Select(x => (x.Item1, x.Item2, u: new System.Net.Sockets.UdpClient(new IPEndPoint(x.Item1, x.Item2)))).ToList();
    using var m = new AirWave.Core.Timing.PtpMaster(standardPorts: false);
    m.AddPeer(a); m.AddPeer(h, yieldTo: true);
    var got = new Dictionary<IPAddress, List<(int type, int p1)>> { [a] = new(), [h] = new() };
    var until = DateTime.UtcNow.AddSeconds(2.5);
    await Task.WhenAll(socks.Select(async x =>
    {
        while (DateTime.UtcNow < until)
        {
            var t = x.u.ReceiveAsync(); if (await Task.WhenAny(t, Task.Delay(200)) != t) continue;
            var b = t.Result.Buffer; lock (got) got[x.Item1].Add((b[0] & 0x0f, b.Length >= 48 ? b[47] : -1));
        }
    }));
    foreach (var x in socks) x.u.Dispose();
    int syncA = got[a].Count(g => g.type == 0), syncH = got[h].Count(g => g.type is 0 or 8);
    var annA = got[a].Where(g => g.type == 0x0B).Select(g => g.p1).Distinct().ToList();
    var annH = got[h].Where(g => g.type == 0x0B).Select(g => g.p1).Distinct().ToList();
    Console.WriteLine($"PTPYIELD speaker: sync {syncA}, announce priority {string.Join("/", annA)}; homepod: sync {syncH}, announce priority {string.Join("/", annH)}");
    bool ok = syncA > 5 && annA.SequenceEqual(new[] { 128 }) && syncH == 0 && annH.SequenceEqual(new[] { 250 });
    Console.WriteLine(ok ? "PASS: per-speaker timekeeping" : "FAIL: per-speaker timekeeping");
    Environment.ExitCode = ok ? 0 : 1;
    return;
}

if (args[0] == "aac-delay")
{
    // The AAC encoder's start-up delay, as AirWave will compensate it (Windows: measured with Media Foundation)
    var enc = AirWave.Core.Audio.AacEncoder.Create();
    int? d = OperatingSystem.IsWindows() ? enc.DelaySamples : AirWave.Core.Audio.AacEncoder.MeasureDelay(enc, AirWave.Core.Audio.AacDecoder.Create());
    Console.WriteLine($"AACDELAY {enc.Name}: {d?.ToString() ?? "unknown"} samples");
    enc.Dispose();
    Environment.ExitCode = d is >= 0 and <= 4096 ? 0 : 1;
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
    // HomePodsKeepTime off: the stand-in "HomePod" here is AirWave's own receiver, which follows a clock but never keeps one
    await using var engine = new StreamEngine(enablePtp: !args.Contains("--ntp")) { ClientName = "AirWave Test", ForceNtp = args.Contains("--ntp"), ForceBuffered = args.Contains("--buffered"), HomePodsKeepTime = false };
    if (args.Contains("--delay")) engine.DelayMs = int.Parse(args[Array.IndexOf(args, "--delay") + 1]);
    engine.AutoDelay = args.Contains("--auto-delay");
    engine.DelayChanged += () => Console.WriteLine($"DELAY {engine.CurrentDelayMs} ms at monotonic {System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F6}");
    engine.Source = pcm != null ? new RawPcmSource(pcm) : new ToneSource();
    int stagger = args.Contains("--stagger") ? int.Parse(args[Array.IndexOf(args, "--stagger") + 1]) : 0;
    var tasks = new List<Task<SpeakerSession>>();
    foreach (var t in args[1].Split(','))
    {
        if (tasks.Count > 0 && stagger > 0) await Task.Delay(stagger * 1000);
        var ep = IPEndPoint.Parse(t);
        var info = new SpeakerInfo { Id = t, Name = t, Port = ep.Port, Features = (1UL << 9) | (1UL << 48) | (1UL << 40) | (args.Contains("--ntp") ? 0 : 1UL << 41) };
        // --homepod2: treat the second speaker as a HomePod (gets buffered AAC, like from an iPhone)
        if (args.Contains("--homepod2") && tasks.Count == 1) info.Model = "AudioAccessory5,1";
        // --homepod-first: treat the first speaker as a HomePod
        if (args.Contains("--homepod-first") && tasks.Count == 0) info.Model = "AudioAccessory5,1";
        info.Addresses.Add(ep.Address);
        tasks.Add(engine.AddSpeakerAsync(info, 0.8));
        if (tasks.Count == 1)
        {
            await Task.Delay(300);
            Console.WriteLine($"STARTED monotonic {(engine.StartedAtNs - AirWave.Core.Timing.MonoClock.NowNs) / 1e9 + System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F6} delay {engine.CurrentDelayMs} ms");
        }
    }
    var sessions = await Task.WhenAll(tasks);
    foreach (var s in sessions) Console.WriteLine($"SESSION {s.Info.Name}: {s.State} {s.ProblemText}");
    if (sessions.All(s => s.State == SpeakerState.Failed)) { Environment.ExitCode = 2; return; }
    // --offset2 ms --offset-after s: nudge the second speaker's timing part-way through (as the app's slider does)
    if (args.Contains("--offset2"))
    {
        int ms = int.Parse(args[Array.IndexOf(args, "--offset2") + 1]);
        int after = args.Contains("--offset-after") ? int.Parse(args[Array.IndexOf(args, "--offset-after") + 1]) : 0;
        await Task.Delay(after * 1000);
        engine.SetSpeakerOffset(sessions[1].Info.Id, ms);
        Console.WriteLine($"OFFSET {sessions[1].Info.Name} {ms} ms");
        await Task.Delay(Math.Max(0, seconds - after) * 1000);
    }
    else await Task.Delay(seconds * 1000);
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
