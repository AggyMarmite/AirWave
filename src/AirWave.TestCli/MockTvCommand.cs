using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirWave.Core.Crypto;
using AirWave.Core.Plist;

/// <summary>
/// "mock-tv --mode open|pin|password [--port 7100] [--out file.h264] [--seconds N]": a stand-in Apple TV for testing
/// screen mirroring, built to the format AirPlayDroid verified on real hardware. It pairs, takes the session,
/// decrypts every video frame (with the DataStream key) and writes the H.264 out as Annex B, and counts audio packets.
/// Prints PIN/FRAME/AUDIO lines for the test to check.
/// </summary>
static class MockTvCommand
{
    const string Pin = "4821", Password = "secret-pw";
    /// <summary>--throttle-kbps N: read the picture no faster than N kbit/s, like a weak Wi-Fi link.</summary>
    static int _throttleKbps;

    public static async Task<int> Run(string[] args)
    {
        string mode = Arg(args, "--mode") ?? "open";
        _throttleKbps = int.Parse(Arg(args, "--throttle-kbps") ?? "0");
        int port = int.Parse(Arg(args, "--port") ?? "7100");
        string outPath = Arg(args, "--out") ?? "mirror-out.h264";
        int seconds = int.Parse(Arg(args, "--seconds") ?? "60");
        var (seed, _) = Curve25519.Ed25519KeyPair();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Console.WriteLine($"MOCKTV ready mode={mode} port={port}");
        var stop = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        var outFile = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.Read);
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var c = await listener.AcceptTcpClientAsync(stop.Token);
                _ = Task.Run(() => new Conn(c, mode, seed, outFile).Run(stop.Token));
            }
        }
        catch (OperationCanceledException) { }
        lock (outFile) outFile.Dispose();
        return 0;
    }

    static string Arg(string[] a, string k) { int i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }

    sealed class Conn
    {
        readonly TcpClient _tcp; readonly string _mode; readonly byte[] _seed; readonly FileStream _out;
        PairingServer _pair; ChannelCipher _cipher; byte[] _secret; bool _authed;
        readonly string _nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        static readonly long ClockId = 0x1122334455667788;

        public Conn(TcpClient tcp, string mode, byte[] seed, FileStream o) { _tcp = tcp; _mode = mode; _seed = seed; _out = o; }

        public async Task Run(CancellationToken ct)
        {
            var s = _tcp.GetStream();
            var plain = new List<byte>();
            var buf = new byte[65536];
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    while (TryParse(plain, out var method, out var uri, out var headers, out var body))
                    {
                        var (status, respHeaders, respBody) = Handle(method, uri, headers, body);
                        var sb = new StringBuilder($"RTSP/1.0 {status} {(status == 200 ? "OK" : "Error")}\r\n");
                        if (headers.TryGetValue("CSeq", out var cs)) sb.Append($"CSeq: {cs}\r\n");
                        // Our "TV clock" = Unix time in ms, so the test can see timestamps are sensible
                        sb.Append($"X-Apple-RequestReceivedTimestamp: {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}\r\nX-Apple-ProcessingTime: 0\r\n");
                        foreach (var (k, v) in respHeaders) sb.Append($"{k}: {v}\r\n");
                        sb.Append($"Content-Length: {respBody?.Length ?? 0}\r\n\r\n");
                        var bytes = Encoding.ASCII.GetBytes(sb.ToString()).Concat(respBody ?? Array.Empty<byte>()).ToArray();
                        if (_cipher != null) bytes = _cipher.Encrypt(bytes);
                        await s.WriteAsync(bytes, ct);
                        if (_cipher == null && _pair?.SharedSecret != null)
                        {
                            _secret = _pair.SharedSecret;
                            _cipher = ChannelCipher.ForControlServer(_secret);
                        }
                    }
                    int n = await s.ReadAsync(buf, ct);
                    if (n <= 0) return;
                    if (_cipher != null) plain.AddRange(_cipher.Decrypt(buf.AsSpan(0, n))); else plain.AddRange(buf.AsSpan(0, n).ToArray());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { Console.WriteLine($"MOCKTV conn error: {ex.Message}"); }
        }

        (int, List<(string, string)>, byte[]) Handle(string method, string uri, Dictionary<string, string> h, byte[] body)
        {
            var none = new List<(string, string)>();
            Console.WriteLine($"MOCKTV <- {method} {uri}");
            switch (method, uri)
            {
                case ("POST", "/pair-pin-start"):
                    if (_mode != "pin") return (404, none, null);
                    Console.WriteLine($"PIN {Pin}");
                    return (200, none, null);
                case ("POST", "/pair-setup"):
                    {
                        var tlv = Tlv8.Decode(body);
                        bool transient = tlv.TryGetValue(Tlv8.Flags, out var f) && f.Length > 0 && (f[0] & 0x10) != 0;
                        if (tlv.TryGetValue(Tlv8.State, out var st) && st[0] == 1)
                        {
                            if (transient && _mode != "open") return (470, none, null);
                            _pair = new PairingServer("MOCK-TV-ID", _seed, _mode == "password" ? Password : transient ? "3939" : Pin);
                        }
                        return (200, none, _pair.Setup(body));
                    }
                case ("POST", "/pair-verify"):
                    _pair ??= new PairingServer("MOCK-TV-ID", _seed);
                    return (200, none, _pair.Verify(body));
                case ("POST", "/feedback"):
                    return (200, none, null);
                case ("TEARDOWN", _):
                    Console.WriteLine("TEARDOWN");
                    return (200, none, null);
            }
            if (_mode == "password" && !_authed)
            {
                // Digest check (RFC 2069, user AirPlay)
                if (!h.TryGetValue("Authorization", out var auth))
                    return (401, new() { ("WWW-Authenticate", $"Digest realm=\"airplay\", nonce=\"{_nonce}\"") }, null);
                string Md5(string x) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(x))).ToLowerInvariant();
                string expect = Md5($"{Md5($"AirPlay:airplay:{Password}")}:{_nonce}:{Md5($"{method}:{uri}")}");
                if (!auth.Contains($"response=\"{expect}\"")) return (401, new() { ("WWW-Authenticate", $"Digest realm=\"airplay\", nonce=\"{_nonce}\"") }, null);
                _authed = true;
            }
            switch (method)
            {
                case "SETUP":
                    {
                        var p = BinaryPlist.Read(body) as Dictionary<string, object>;
                        if (!p.ContainsKey("streams"))
                        {
                            Console.WriteLine($"SESSION name={p.GetString("name")} mirroring={p.GetValueOrDefault("isScreenMirroringSession")}");
                            var ev = new TcpListener(IPAddress.Loopback, 0);
                            ev.Start();
                            _ = Task.Run(() => Events(ev));
                            return (200, Plist, BinaryPlist.Write(new Dictionary<string, object>
                            {
                                ["eventPort"] = (long)((IPEndPoint)ev.LocalEndpoint).Port,
                                ["timingPeerInfo"] = new Dictionary<string, object> { ["ClockID"] = ClockId },
                            }));
                        }
                        var st = (p["streams"] as List<object>)[0] as Dictionary<string, object>;
                        long type = st.GetLong("type");
                        if (type == 110)
                        {
                            long id = st.GetLong("streamConnectionID");
                            var dl = new TcpListener(IPAddress.Loopback, 0);
                            dl.Start();
                            var key = ChannelCipher.Derive(_secret, "DataStream-Salt" + id, "DataStream-Output-Encryption-Key");
                            _ = Task.Run(() => Video(dl, key));
                            return (200, Plist, BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { new Dictionary<string, object> { ["type"] = 110L, ["dataPort"] = (long)((IPEndPoint)dl.LocalEndpoint).Port } } }));
                        }
                        if (type == 96)
                        {
                            var key = st["shk"] as byte[];
                            var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                            var cu = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                            _ = Task.Run(() => Audio(u, cu, key));
                            var resp = new Dictionary<string, object> { ["type"] = 96L };
                            if (st.ContainsKey("streamConnections"))
                                resp["streamConnections"] = new Dictionary<string, object>
                                {
                                    ["streamConnectionTypeRTP"] = new Dictionary<string, object> { ["streamConnectionKeyPort"] = (long)((IPEndPoint)u.Client.LocalEndPoint).Port },
                                    ["streamConnectionTypeRTCP"] = new Dictionary<string, object> { ["streamConnectionKeyPort"] = (long)((IPEndPoint)cu.Client.LocalEndPoint).Port },
                                };
                            else { resp["dataPort"] = (long)((IPEndPoint)u.Client.LocalEndPoint).Port; resp["controlPort"] = (long)((IPEndPoint)cu.Client.LocalEndPoint).Port; }
                            return (200, Plist, BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { resp } }));
                        }
                        return (400, none, null);
                    }
                case "RECORD":
                case "SET_PARAMETER":
                    return (200, none, null);
            }
            return (501, none, null);
        }

        static List<(string, string)> Plist => new() { ("Content-Type", "application/x-apple-binary-plist") };

        async Task Events(TcpListener l)
        {
            var c = await l.AcceptTcpClientAsync();
            var cipher = ChannelCipher.ForEventsServer(_secret);
            var info = BinaryPlist.Write(new Dictionary<string, object>
            {
                ["type"] = "updateInfo",
                ["value"] = new Dictionary<string, object>
                {
                    ["displays"] = new List<object> { new Dictionary<string, object> { ["widthPixels"] = 1920L, ["heightPixels"] = 1080L } },
                    ["supportedFormats"] = new Dictionary<string, object> { ["screenStream"] = 0x40000L },
                },
            });
            var req = Encoding.ASCII.GetBytes($"POST /command RTSP/1.0\r\nCSeq: 1\r\nContent-Type: application/x-apple-binary-plist\r\nContent-Length: {info.Length}\r\n\r\n").Concat(info).ToArray();
            await c.GetStream().WriteAsync(cipher.Encrypt(req));
            var buf = new byte[4096];
            int n = await c.GetStream().ReadAsync(buf);
            var resp = Encoding.ASCII.GetString(cipher.Decrypt(buf.AsSpan(0, n)));
            Console.WriteLine($"EVENTS reply {resp.Split("\r\n")[0]}");
        }

        int throttleKbps = _throttleKbps;
        async Task Video(TcpListener l, byte[] key)
        {
            using var c = await l.AcceptTcpClientAsync();
            if (throttleKbps > 0) c.ReceiveBufferSize = 64 * 1024;
            var throttleStart = System.Diagnostics.Stopwatch.StartNew();
            long throttleBytes = 0;
            int lateFrames = 0, gapErrors = 0;
            var s = c.GetStream();
            using var aead = new ChaCha20Poly1305(key);
            ulong nonce = 0;
            var header = new byte[128];
            int frames = 0, keyframes = 0, heartbeats = 0;
            long lastTs = 0;
            try
            {
                while (true)
                {
                    await s.ReadExactlyAsync(header);
                    int size = BinaryPrimitives.ReadInt32LittleEndian(header);
                    byte type = header[4];
                    if (type == 2) { heartbeats++; continue; }
                    var payload = new byte[size];
                    await s.ReadExactlyAsync(payload);
                    if (throttleKbps > 0)
                    {
                        throttleBytes += size + 128;
                        double due = throttleBytes * 8.0 / (throttleKbps * 1000.0);
                        double wait = due - throttleStart.Elapsed.TotalSeconds;
                        if (wait > 0) await Task.Delay(TimeSpan.FromSeconds(wait));
                    }
                    long ts = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
                    if (type == 1)
                    {
                        // avcC: write SPS/PPS as Annex B
                        int spsLen = payload[6] << 8 | payload[7];
                        var sps = payload.AsSpan(8, spsLen).ToArray();
                        int ppsLen = payload[9 + spsLen] << 8 | payload[10 + spsLen];
                        var pps = payload.AsSpan(11 + spsLen, ppsLen).ToArray();
                        float w = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(16)), h = BinaryPrimitives.ReadSingleLittleEndian(header.AsSpan(20));
                        Console.WriteLine($"CODEC {w}x{h} sps {spsLen} pps {ppsLen} ts {ts:X}");
                        lock (_out) { _out.Write(new byte[] { 0, 0, 0, 1 }); _out.Write(sps); _out.Write(new byte[] { 0, 0, 0, 1 }); _out.Write(pps); }
                        continue;
                    }
                    var nb = new byte[12];
                    BinaryPrimitives.WriteUInt64LittleEndian(nb.AsSpan(4), nonce++);
                    var plain = new byte[size - 16];
                    aead.Decrypt(nb, payload.AsSpan(0, size - 16), payload.AsSpan(size - 16), plain, header);
                    bool kf = header[5] == 0x10;
                    frames++; if (kf) keyframes++;
                    if (ts < lastTs) Console.WriteLine("ERR timestamps went backwards");
                    // After skipped frames the picture must restart from a keyframe, or the TV would show garbage
                    if (lastTs != 0 && !kf && ts - lastTs > (long)(0.05 * 4294967296.0)) { gapErrors++; Console.WriteLine($"ERR frames skipped without a keyframe after (gap {(ts - lastTs) / 4294967.296:0} ms)"); }
                    lastTs = ts;
                    // TV clock here is Unix ms: the frame's presentation time should be ~now + latency
                    double tsSec = (ts >> 32) + (ts & 0xffffffff) / 4294967296.0;
                    double lead = tsSec - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                    if (lead < 0) lateFrames++;
                    if (frames % 30 == 1) Console.WriteLine($"FRAME {frames} key={kf} bytes={plain.Length} lead={lead * 1000:0}ms clock={BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(40)):X}");
                    int p = 0;
                    lock (_out)
                        while (p + 4 <= plain.Length)
                        {
                            int nl = BinaryPrimitives.ReadInt32BigEndian(plain.AsSpan(p));
                            _out.Write(new byte[] { 0, 0, 0, 1 });
                            _out.Write(plain, p + 4, nl);
                            p += 4 + nl;
                        }
                }
            }
            catch (Exception ex) when (ex is EndOfStreamException or IOException or ObjectDisposedException) { }
            catch (CryptographicException) { Console.WriteLine("ERR video frame failed to decrypt"); }
            Console.WriteLine($"VIDEO frames={frames} keyframes={keyframes} late={lateFrames} gaps_without_keyframe={gapErrors}");
            lock (_out) _out.Flush();
            Console.WriteLine($"VIDEO done frames={frames} keyframes={keyframes} heartbeats={heartbeats}");
        }

        async Task Audio(UdpClient data, UdpClient control, byte[] key)
        {
            using var aead = new ChaCha20Poly1305(key);
            int ok = 0, bad = 0, syncs = 0;
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    var r = await control.ReceiveAsync();
                    if (r.Buffer.Length == 28 && r.Buffer[1] == 0xD7 && ++syncs <= 2) Console.WriteLine($"AUDIO sync first={r.Buffer[0] == 0x90}");
                }
            });
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try
            {
                while (true)
                {
                    var r = await data.ReceiveAsync(cts.Token);
                    var p = r.Buffer;
                    int n = p.Length;
                    var nonce = new byte[12];
                    Buffer.BlockCopy(p, n - 8, nonce, 4, 8);
                    var plain = new byte[n - 36];
                    try { aead.Decrypt(nonce, p.AsSpan(12, n - 36), p.AsSpan(n - 24, 16), plain, p.AsSpan(4, 8)); ok++; }
                    catch (CryptographicException) { bad++; }
                    if ((ok + bad) % 200 == 1) Console.WriteLine($"AUDIO packets ok={ok} bad={bad}");
                }
            }
            catch { }
        }

        static bool TryParse(List<byte> data, out string method, out string uri, out Dictionary<string, string> headers, out byte[] body)
        {
            method = uri = null; headers = new(StringComparer.OrdinalIgnoreCase); body = null;
            int end = -1;
            for (int i = 0; i + 3 < data.Count; i++)
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') { end = i; break; }
            if (end < 0) return false;
            var lines = Encoding.UTF8.GetString(data.GetRange(0, end).ToArray()).Split("\r\n");
            var first = lines[0].Split(' ');
            method = first[0]; uri = first.Length > 1 ? first[1] : "";
            foreach (var l in lines.Skip(1)) { int c = l.IndexOf(':'); if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim(); }
            int len = headers.TryGetValue("Content-Length", out var cl) ? int.Parse(cl) : 0;
            if (data.Count < end + 4 + len) return false;
            body = data.GetRange(end + 4, len).ToArray();
            data.RemoveRange(0, end + 4 + len);
            return true;
        }
    }
}
