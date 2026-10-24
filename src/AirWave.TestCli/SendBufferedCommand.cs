using AirWave.Core.Discovery;
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

        using var ptp = new PtpMaster(standardPorts: !args.Contains("--ptp-any-port"));
        // --now-playing: act like an iPhone that offers remote control (DACP) and sends track info
        bool nowPlaying = args.Contains("--now-playing");
        MdnsResponder dacpAd = null;
        if (nowPlaying)
        {
            var http = new TcpListener(IPAddress.Any, 0);
            http.Start();
            int dacpPort = ((IPEndPoint)http.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    using var c = await http.AcceptTcpClientAsync();
                    var st = c.GetStream();
                    var buf = new byte[4096];
                    int n = await st.ReadAsync(buf);
                    var req = System.Text.Encoding.ASCII.GetString(buf, 0, n);
                    var first = req.Split("\r\n")[0];
                    var ar = req.Split("\r\n").FirstOrDefault(l => l.StartsWith("Active-Remote:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
                    Console.WriteLine($"DACP {first} active-remote={ar}");
                    await st.WriteAsync(System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nContent-Length: 0\r\n\r\n"));
                }
            });
            dacpAd = new MdnsResponder();
            dacpAd.Publish(new[] { new ServiceRecord("iTunes_Ctrl_" + DacpId, "_dacp._tcp", dacpPort, new List<(string, string)> { ("txtvers", "1"), ("Ver", "131077") }) });
            Console.WriteLine($"DACP listening on {dacpPort}");
        }
        var sessions = new List<Session>();
        foreach (var t in targets)
        {
            try { sessions.Add(await Session.StartAsync(t, ptp, nowPlaying)); Console.WriteLine($"SESSION {t}: ready"); }
            catch (Exception ex) { Console.WriteLine($"ERR {t}: {ex.Message}"); }
        }
        if (sessions.Count == 0) return 2;
        if (nowPlaying)
        {
            var art = new byte[3000];
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(art, 0);
            var cmd = new Dictionary<string, object>
            {
                ["type"] = "updateMRNowPlayingInfo",
                ["params"] = new Dictionary<string, object>
                {
                    ["type"] = "npi-text", ["mergePolicy"] = "replace",
                    ["params"] = new Dictionary<string, object>
                    {
                        ["kMRMediaRemoteNowPlayingInfoTitle"] = "Test Song", ["kMRMediaRemoteNowPlayingInfoArtist"] = "Test Artist",
                        ["kMRMediaRemoteNowPlayingInfoAlbum"] = "Test Album", ["kMRMediaRemoteNowPlayingInfoDuration"] = 200.0,
                        ["kMRMediaRemoteNowPlayingInfoElapsedTime"] = 12.5, ["kMRMediaRemoteNowPlayingInfoPlaybackRate"] = 1.0,
                        ["kMRMediaRemoteNowPlayingInfoTimestamp"] = DateTime.UtcNow, ["kMRMediaRemoteNowPlayingInfoArtworkData"] = art,
                    },
                },
            };
            foreach (var s in sessions) await s.CommandAsync(cmd);
            Console.WriteLine("NOWPLAYING sent");
        }

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
        // --gm-switch-at N: N s in, hand timekeeping to another clock, as when a HomePod joins an iPhone's group and
        // becomes the PTP grandmaster. Like an Apple device it only sends its time to receivers that say hello first.
        int gmSwitchAt = int.Parse(Arg(args, "--gm-switch-at") ?? "0");
        // --restream-at N: end the stream and start a fresh one on the same connection (iOS does this when a speaker joins)
        int restreamAt = int.Parse(Arg(args, "--restream-at") ?? "0");
        bool restreamed = restreamAt == 0;
        PtpMaster gm2 = null;
        bool switched = gmSwitchAt == 0;
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
            if (!switched && MonoClock.NowNs > anchorNs + gmSwitchAt * 1_000_000_000L)
            {
                switched = true;
                gm2 = new PtpMaster(standardPorts: false) { SilentUntilHello = true };
                ptp.ForeignMessage += (m, _, from) => { if ((m[0] & 0x0f) == 0x0B) { gm2.MarkHello(from); } };
                foreach (var s in sessions) { gm2.AddPeer(s.Target.Address); ptp.RemovePeer(s.Target.Address); }
                // Same timebase (both clocks are MonoClock), so the same anchor, now on the new clock's timeline
                foreach (var s in sessions) await s.AnchorAsync(rtp0, anchorNs, gm2.ClockId);
                Console.WriteLine($"SWITCH monotonic {System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F6} to clock {gm2.ClockId:X16}");
            }
            if (!restreamed && MonoClock.NowNs > anchorNs + restreamAt * 1_000_000_000L)
            {
                restreamed = true;
                foreach (var s in sessions) await s.RestreamAsync();
                foreach (var s in sessions) await s.AnchorAsync(rtp0, anchorNs, gm2?.ClockId ?? ptp.ClockId);
                Console.WriteLine($"RESTREAM monotonic {System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency:F6}");
                // The old stream's buffered audio went with it: resend from what's due now, as iOS does
                int now_i = (int)((MonoClock.NowNs - anchorNs) / 1e9 * 44100 / 1024) + 1;
                seq = (uint)((seq - (uint)(i - now_i)) & 0x7fffff);
                i = now_i;
            }
            var f = frames[i % frames.Count];
            uint rtp = rtp0 + (uint)(i * 1024);
            long playNs = anchorNs + (long)(i * 1024 * 1e9 / 44100);
            while (playNs - MonoClock.NowNs > 3_000_000_000L) await Task.Delay(20);
            foreach (var s in sessions) { try { s.SendAudio(seq, rtp, f); } catch (IOException) { /* that receiver hung up (Shairport times out test senders without keep-alives) */ } }
            seq = (seq + 1) & 0x7fffff;
        }
        await Task.Delay(500);
        foreach (var s in sessions) { await s.TeardownAsync(); Console.WriteLine($"END {s.Target}"); }
        return 0;
    }

    const string DacpId = "14D9A1B6F47C0E3B";
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

        public async Task CommandAsync(Dictionary<string, object> cmd) =>
            await _rtsp.SendAsync("POST", "/command", BinaryPlist.Write(cmd), "application/x-apple-binary-plist");

        public static async Task<Session> StartAsync(IPEndPoint target, PtpMaster ptp, bool dacp = false)
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
            var hdrs = dacp ? new Dictionary<string, string> { ["DACP-ID"] = DacpId, ["Active-Remote"] = "1986535575" } : null;
            var rs = await s._rtsp.SendAsync("SETUP", s._url, BinaryPlist.Write(setup), "application/x-apple-binary-plist", hdrs, ct: ct);
            if (!rs.Ok) throw new Exception($"session SETUP {rs}");
            ptp.AddPeer(target.Address);
            await s._rtsp.SendAsync("RECORD", s._url, ct: ct);
            await s._rtsp.SendAsync("SETPEERS", s._url, BinaryPlist.Write(new List<object> { target.Address.ToString(), local.ToString() }), "/peer-list-changed", ct: ct);

            await s.SetupStreamAsync(key);
            await s._rtsp.SendAsync("SET_PARAMETER", s._url, System.Text.Encoding.ASCII.GetBytes("volume: -5.000000\r\n"), "text/parameters", ct: ct);
            return s;
        }

        async Task SetupStreamAsync(byte[] key)
        {
            var stream = new Dictionary<string, object>
            {
                ["audioFormat"] = 0x400000L, ["audioMode"] = "default", ["controlPort"] = 0L, ["ct"] = 4L, ["isMedia"] = true,
                ["shk"] = key, ["spf"] = 1024L, ["sr"] = 44100L, ["type"] = 103L, ["supportsDynamicStreamID"] = true,
                ["streamConnectionID"] = (long)RandomNumberGenerator.GetInt32(int.MaxValue), ["clientID"] = Guid.NewGuid().ToString(),
            };
            var rst = await _rtsp.SendAsync("SETUP", _url, BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { stream } }), "application/x-apple-binary-plist");
            if (!rst.Ok) throw new Exception($"stream SETUP {rst}");
            var st = ((BinaryPlist.Read(rst.Body) as Dictionary<string, object>)["streams"] as List<object>)[0] as Dictionary<string, object>;
            int dataPort = (int)st.GetLong("dataPort");
            _audio = new ChaCha20Poly1305(key);
            _dataTcp = new TcpClient(Target.AddressFamily) { NoDelay = true };
            await _dataTcp.ConnectAsync(Target.Address, dataPort);
            _data = _dataTcp.GetStream();
        }

        /// <summary>Ends the stream and starts a new one on the same connection, as iOS does when the group changes.</summary>
        public async Task RestreamAsync()
        {
            var r = await _rtsp.SendAsync("TEARDOWN", _url, BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { new Dictionary<string, object> { ["type"] = 103L } } }), "application/x-apple-binary-plist");
            if (!r.Ok) Console.WriteLine($"ERR stream teardown {Target}: {r}");
            try { _dataTcp.Dispose(); } catch { }
            await SetupStreamAsync(RandomNumberGenerator.GetBytes(32));
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
