using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using AirWave.Core;
using AirWave.Core.Crypto;
using AirWave.Core.Plist;
using AirWave.Core.Rtsp;
using AirWave.Core.Timing;

/// <summary>
/// "send-buffered ip:port[,ip:port] --aac file.adts [--seconds N]": streams AAC the way an iPhone does for music
/// (AirPlay 2 buffered audio: TCP, sent ahead, played from a SETRATEANCHORTIME anchor on our PTP clock),
/// to every target at once with the same anchor, so they should all play in sync.
/// </summary>
static class SendBufferedCommand
{
    public static async Task<int> Run(string[] args)
    {
        var targets = args[1].Split(',').Select(IPEndPoint.Parse).ToList();
        string aacPath = Arg(args, "--aac") ?? throw new ArgumentException("--aac file needed");
        int seconds = int.Parse(Arg(args, "--seconds") ?? "10");
        var frames = ReadAdts(File.ReadAllBytes(aacPath));
        Console.WriteLine($"{frames.Count} AAC frames");

        using var ptp = new PtpMaster();
        var sessions = new List<Session>();
        foreach (var t in targets)
        {
            try { sessions.Add(await Session.StartAsync(t, ptp)); Console.WriteLine($"SESSION {t}: ready"); }
            catch (Exception ex) { Console.WriteLine($"ERR {t}: {ex.Message}"); }
        }
        if (sessions.Count == 0) return 2;

        // Let the receivers lock on to our clock, then anchor: frame rtp0 plays 2 s from now
        await Task.Delay(1500);
        uint rtp0 = (uint)RandomNumberGenerator.GetInt32(1 << 30);
        long anchorNs = MonoClock.NowNs + 2_000_000_000L;
        foreach (var s in sessions) await s.AnchorAsync(rtp0, anchorNs, ptp.ClockId);
        // Same instant on the OS monotonic clock, so a test can work out exactly when each sample should be heard
        double anchorMono = (anchorNs - MonoClock.NowNs) / 1e9 + System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine($"ANCHOR monotonic {anchorMono:F6}");

        // Feed frames, staying up to 3 s ahead of playback. Optionally pause like an iPhone does
        // (rate 0 + flush of everything not yet played), then resume from where it stopped with a new anchor.
        int pauseAt = int.Parse(Arg(args, "--pause-at") ?? "0");
        bool paused = pauseAt == 0;
        uint seq = (uint)RandomNumberGenerator.GetInt32(1 << 20);
        long endNs = anchorNs + seconds * 1_000_000_000L;
        int i0 = 0;          // frame index the current anchor refers to
        for (int i = 0; MonoClock.NowNs < endNs; i++)
        {
            if (!paused && MonoClock.NowNs > anchorNs + pauseAt * 1_000_000_000L)
            {
                paused = true;
                // The frame being heard now is where we'll resume
                int resume = i0 + (int)((MonoClock.NowNs - anchorNs) / 1e9 * 44100 / 1024) + 1;
                uint resumeSeq = (uint)((seq - (uint)(i - resume)) & 0x7fffff);
                foreach (var s in sessions) await s.PauseAsync(resumeSeq, rtp0 + (uint)(resume * 1024));
                Console.WriteLine($"PAUSED monotonic {System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F6}");
                await Task.Delay(2000);
                anchorNs = MonoClock.NowNs + 1_000_000_000L;
                rtp0 += (uint)(resume * 1024);
                i0 = 0; i = resume - resume; // restart frame numbering from the resume point
                int skip = resume;
                frames = frames.Skip(skip % frames.Count).Concat(frames.Take(skip % frames.Count)).ToList();
                seq = resumeSeq;
                foreach (var s in sessions) await s.AnchorAsync(rtp0, anchorNs, ptp.ClockId);
                double mono2 = (anchorNs - MonoClock.NowNs) / 1e9 + System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
                Console.WriteLine($"ANCHOR2 monotonic {mono2:F6} offset {(skip * 1024) % 44100}");
                endNs = anchorNs + (seconds - pauseAt) * 1_000_000_000L;
            }
            var f = frames[i % frames.Count];
            uint rtp = rtp0 + (uint)(i * 1024);
            long playNs = anchorNs + (long)(i * 1024 * 1e9 / 44100);
            while (playNs - MonoClock.NowNs > 3_000_000_000L) await Task.Delay(20);
            foreach (var s in sessions) s.SendAudio(seq, rtp, f);
            seq = (seq + 1) & 0x7fffff;
        }
        await Task.Delay(500);
        foreach (var s in sessions) { await s.TeardownAsync(); Console.WriteLine($"END {s.Target}"); }
        return 0;
    }

    static string Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    static List<byte[]> ReadAdts(byte[] d)
    {
        var list = new List<byte[]>();
        int q = 0;
        while (q + 7 <= d.Length)
        {
            int len = ((d[q + 3] & 3) << 11) | (d[q + 4] << 3) | (d[q + 5] >> 5);
            int hdr = (d[q + 1] & 1) == 1 ? 7 : 9;
            list.Add(d.AsSpan(q + hdr, len - hdr).ToArray());
            q += len;
        }
        return list;
    }

    sealed class Session
    {
        public IPEndPoint Target;
        RtspClient _rtsp;
        string _url;
        ChaCha20Poly1305 _audio;
        NetworkStream _data;
        TcpClient _dataTcp;

        public static async Task<Session> StartAsync(IPEndPoint target, PtpMaster ptp)
        {
            var s = new Session { Target = target };
            var ct = CancellationToken.None;
            s._rtsp = await RtspClient.ConnectAsync(target, 5000, ct);
            s._rtsp.DefaultHeaders["User-Agent"] = "AirPlay/665.13.1";
            s._rtsp.DefaultHeaders["X-Apple-Client-Name"] = "Buffered Test";
            s._rtsp.DefaultHeaders["DACP-ID"] = "1A2B3C4D5E6F7788";
            s._rtsp.DefaultHeaders["Active-Remote"] = "12345";
            await s._rtsp.SendAsync("GET", "/info", ct: ct);

            // Transient pairing
            var cli = new PairingClient("buffered-test", Curve25519.Ed25519KeyPair().seed);
            var hkp = new Dictionary<string, string> { ["X-Apple-HKP"] = "4" };
            var r1 = await s._rtsp.SendAsync("POST", "/pair-setup", cli.SetupM1(transient: true), "application/octet-stream", hkp, ct: ct);
            var r3 = await s._rtsp.SendAsync("POST", "/pair-setup", cli.SetupM3(r1.Body, "3939"), "application/octet-stream", hkp, ct: ct);
            cli.SetupCheckM4(r3.Body, transient: true);
            var secret = cli.SharedSecret;
            s._rtsp.EnableEncryption(secret);
            var key = secret.AsSpan(0, 32).ToArray();
            s._audio = new ChaCha20Poly1305(key);

            var local = s._rtsp.Local.Address.IsIPv4MappedToIPv6 ? s._rtsp.Local.Address.MapToIPv4() : s._rtsp.Local.Address;
            s._url = $"rtsp://{local}/{RandomNumberGenerator.GetInt32(int.MaxValue)}";
            var peer = new Dictionary<string, object> { ["Addresses"] = new List<object> { local.ToString() }, ["ID"] = Guid.NewGuid().ToString().ToUpperInvariant(), ["DeviceType"] = 0L, ["ClockID"] = unchecked((long)ptp.ClockId), ["SupportsClockPortMatchingOverride"] = false };
            var setup = new Dictionary<string, object>
            {
                ["name"] = "Buffered Test", ["deviceID"] = "02:11:22:33:44:55", ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["timingProtocol"] = "PTP", ["macAddress"] = "02:11:22:33:44:55", ["groupUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["groupContainsGroupLeader"] = false, ["timingPeerInfo"] = peer, ["timingPeerList"] = new List<object> { new Dictionary<string, object>(peer) },
            };
            var rs = await s._rtsp.SendAsync("SETUP", s._url, BinaryPlist.Write(setup), "application/x-apple-binary-plist", ct: ct);
            if (!rs.Ok) throw new Exception($"session SETUP {rs}");
            ptp.AddPeer(target.Address);
            await s._rtsp.SendAsync("RECORD", s._url, ct: ct);
            await s._rtsp.SendAsync("SETPEERS", s._url, BinaryPlist.Write(new List<object> { target.Address.ToString(), local.ToString() }), "/peer-list-changed", ct: ct);

            var stream = new Dictionary<string, object>
            {
                ["audioFormat"] = 0x400000L, ["audioMode"] = "default", ["controlPort"] = 0L, ["ct"] = 4L, ["isMedia"] = true,
                ["shk"] = key, ["spf"] = 1024L, ["sr"] = 44100L, ["type"] = 103L, ["supportsDynamicStreamID"] = true,
                ["streamConnectionID"] = (long)RandomNumberGenerator.GetInt32(int.MaxValue), ["clientID"] = Guid.NewGuid().ToString(),
            };
            var rst = await s._rtsp.SendAsync("SETUP", s._url, BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { stream } }), "application/x-apple-binary-plist", ct: ct);
            if (!rst.Ok) throw new Exception($"stream SETUP {rst}");
            var st = ((BinaryPlist.Read(rst.Body) as Dictionary<string, object>)["streams"] as List<object>)[0] as Dictionary<string, object>;
            int dataPort = (int)st.GetLong("dataPort");
            s._dataTcp = new TcpClient(target.AddressFamily) { NoDelay = true };
            await s._dataTcp.ConnectAsync(target.Address, dataPort);
            s._data = s._dataTcp.GetStream();
            await s._rtsp.SendAsync("SET_PARAMETER", s._url, System.Text.Encoding.ASCII.GetBytes("volume: -5.000000\r\n"), "text/parameters", ct: ct);
            return s;
        }

        public async Task AnchorAsync(uint rtp, long ns, ulong clockId)
        {
            ulong secs = (ulong)(ns / 1_000_000_000L);
            ulong frac = (ulong)((ns % 1_000_000_000L) * (Math.Pow(2, 64) / 1e9));
            var p = new Dictionary<string, object>
            {
                ["rate"] = 1L, ["rtpTime"] = (long)rtp, ["networkTimeSecs"] = (long)secs, ["networkTimeFrac"] = frac,
                ["networkTimeTimelineID"] = clockId, ["networkTimeFlags"] = 0L,
            };
            var r = await _rtsp.SendAsync("SETRATEANCHORTIME", _url, BinaryPlist.Write(p), "application/x-apple-binary-plist");
            if (!r.Ok) Console.WriteLine($"ERR anchor {Target}: {r}");
        }

        public async Task PauseAsync(uint resumeSeq, uint resumeRtp)
        {
            await _rtsp.SendAsync("SETRATEANCHORTIME", _url, BinaryPlist.Write(new Dictionary<string, object> { ["rate"] = 0L }), "application/x-apple-binary-plist");
            var flush = new Dictionary<string, object> { ["flushUntilSeq"] = (long)resumeSeq, ["flushUntilTS"] = (long)resumeRtp };
            await _rtsp.SendAsync("FLUSHBUFFERED", _url, BinaryPlist.Write(flush), "application/x-apple-binary-plist");
        }

        public void SendAudio(uint seq, uint rtp, byte[] aac)
        {
            var pkt = new byte[2 + 12 + aac.Length + 16 + 8];
            BinaryPrimitives.WriteUInt16BigEndian(pkt, (ushort)pkt.Length);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(2), 0x80000000u | seq);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(6), rtp);
            BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(10), 0x16000000);
            var nonce8 = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(nonce8, seq);
            var nonce = new byte[12];
            nonce8.CopyTo(nonce, 4);
            _audio.Encrypt(nonce, aac, pkt.AsSpan(14, aac.Length), pkt.AsSpan(14 + aac.Length, 16), pkt.AsSpan(6, 8));
            nonce8.CopyTo(pkt, 14 + aac.Length + 16);
            _data.Write(pkt);
        }

        public async Task TeardownAsync()
        {
            try { await _rtsp.SendAsync("TEARDOWN", _url, BinaryPlist.Write(new Dictionary<string, object>()), "application/x-apple-binary-plist"); } catch { }
            _dataTcp?.Dispose();
            _rtsp.Dispose();
        }
    }
}
