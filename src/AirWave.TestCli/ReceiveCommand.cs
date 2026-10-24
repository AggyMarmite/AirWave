using System.Diagnostics;
using AirWave.Core;
using AirWave.Core.Discovery;
using AirWave.Core.Receiver;
using AirWave.Core.Timing;

/// <summary>
/// "receive --name N --pipe file [--seconds S] [--port P]": runs AirWave as an AirPlay speaker and writes
/// what it plays to a pipe in real time (s16le, 44.1 kHz stereo), like Shairport Sync's pipe backend.
/// </summary>
static class ReceiveCommand
{
    public static async Task<int> Run(string[] args)
    {
        string name = Arg(args, "--name") ?? "AirWave Test";
        string pipe = Arg(args, "--pipe");
        int seconds = int.Parse(Arg(args, "--seconds") ?? "3600");
        int port = int.Parse(Arg(args, "--port") ?? "7000");
        string idDir = Arg(args, "--id-dir") ?? Path.Combine(Path.GetTempPath(), "airwave-rx-" + name.Replace(' ', '_'));

        // As in the app: one engine (and one set of PTP sockets) shared by sending and receiving
        var engine = new AirWave.Core.Streaming.StreamEngine(enablePtp: true) { ClientName = name };
        var id = ReceiverIdentity.LoadOrCreate(idDir);
        await using var rx = new AirPlayReceiver(id, engine.Ptp, name, new MdnsResponder());
        rx.Start(port);
        rx.StateChanged += () => Console.WriteLine($"STATE client={rx.ActiveClient ?? "-"}");
        bool remoteSent = false;
        rx.NowPlayingChanged += () =>
        {
            var np = rx.NowPlaying;
            if (np == null || !np.HasInfo) return;
            Console.WriteLine($"NOWPLAYING title={np.Title} artist={np.Artist} album={np.Album} art={np.Artwork?.Length ?? 0} duration={np.Duration:0.0} elapsed={np.Elapsed:0.0} rate={np.Rate} remote={rx.HasRemote}");
            if (rx.HasRemote && !remoteSent)
            {
                remoteSent = true;
                _ = Task.Run(async () => Console.WriteLine($"REMOTE playpause ok={await rx.SendRemoteAsync(RemoteCommand.PlayPause)}"));
            }
        };

        using var output = pipe != null ? new PacedPipeOutput(rx.Player, pipe) : null;
        output?.Start();

        // --render: play through Windows (as the app does) and time each beep that comes out of the default device
        IDisposable render = null, recorder = null;
        if (args.Contains("--render") && OperatingSystem.IsWindows())
        {
            var r = new AirWave.Core.Audio.Windows.RenderOutput(rx.Player);
            r.Start();
            render = r;
            var rec = new AirWave.Core.Audio.Windows.LoopbackRecorder();
            double lastOnset = -10;
            rec.Audio += (t, left, rate) =>
            {
                for (int i = 0; i < left.Length; i++)
                {
                    double ts = t + i / (double)rate;
                    if (Math.Abs(left[i]) > 0.02 && ts - lastOnset > 0.5) { lastOnset = ts; Console.WriteLine($"ONSET {ts:F6}"); }
                }
            };
            rec.Start();
            recorder = rec;
        }

        // --relay ip:port: pass what we receive on to another AirPlay speaker
        if (Arg(args, "--relay") is string relay)
        {
            engine.Source = new RelaySource(rx.Player);
            var ep = System.Net.IPEndPoint.Parse(relay);
            var info = new SpeakerInfo { Id = relay, Name = relay, Port = ep.Port, Features = (1UL << 9) | (1UL << 48) | (1UL << 41) };
            info.Addresses.Add(ep.Address);
            var sess = await engine.AddSpeakerAsync(info, 0.8);
            Console.WriteLine($"RELAY {relay}: {sess.State}");
        }
        Console.WriteLine($"READY {name} port {rx.Port}");
        await Task.Delay(seconds * 1000);
        recorder?.Dispose();
        render?.Dispose();
        await engine.DisposeAsync();
        return 0;
    }

    static string Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
}

/// <summary>Writes the player's output to a file/FIFO in real time: each chunk is written when it is due to be heard.</summary>
sealed class PacedPipeOutput : IReceiverOutput
{
    private readonly ReceiverPlayer _player;
    private readonly string _path;
    private Thread _t;
    private volatile bool _run;

    public PacedPipeOutput(ReceiverPlayer player, string path) { _player = player; _path = path; }

    public void Start()
    {
        _run = true;
        _t = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _t.Start();
    }

    private void Loop()
    {
        using var fs = new FileStream(_path, FileMode.Open, FileAccess.Write);
        const int chunk = 44; // 1 ms: each chunk is written as its last sample becomes due
        var pcm = new short[chunk * 2];
        var bytes = new byte[chunk * 4];
        long startNs = MonoClock.NowNs;
        long written = 0;
        while (_run)
        {
            long dueNs = startNs + (long)(written * 1e9 / 44100);
            long writeAt = dueNs + (long)(chunk * 1e9 / 44100);
            long wait = writeAt - MonoClock.NowNs;
            if (wait > 1_000_000) Thread.Sleep((int)(wait / 1_000_000));
            while (MonoClock.NowNs < writeAt) Thread.SpinWait(50);
            // These frames are heard the moment they're written
            _player.Read(dueNs, pcm, chunk);
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);
            fs.Write(bytes);
            fs.Flush();
            written += chunk;
        }
    }

    public void Stop() { _run = false; _t?.Join(500); }
    public void Dispose() => Stop();
}
