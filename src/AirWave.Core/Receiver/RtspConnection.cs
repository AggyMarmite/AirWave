using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirWave.Core.Audio;
using AirWave.Core.Crypto;
using AirWave.Core.Plist;

namespace AirWave.Core.Receiver;

/// <summary>One RTSP control connection from a sender, and the audio stream it sets up.</summary>
internal sealed class RtspConnection : IDisposable
{
    private readonly AirPlayReceiver _rx;
    private readonly TcpClient _tcp;
    private readonly NetworkStream _net;
    private readonly IPEndPoint _remote;
    private readonly IPEndPoint _local;
    private readonly PairingServer _pairing;
    private ChannelCipher _cipher;
    private byte[] _sharedSecret;
    private readonly CancellationTokenSource _cts = new();
    private int _disposed;

    // Session state
    private string _clientName;
    private bool _ptp;
    private TcpListener _eventListener;
    private TcpClient _eventClient;
    private AudioStream _stream;

    public RtspConnection(AirPlayReceiver rx, TcpClient tcp)
    {
        _rx = rx;
        _tcp = tcp;
        _tcp.NoDelay = true;
        _net = tcp.GetStream();
        _remote = Norm((IPEndPoint)tcp.Client.RemoteEndPoint);
        _local = Norm((IPEndPoint)tcp.Client.LocalEndPoint);
        _pairing = new PairingServer(rx.Identity.DeviceId, rx.Identity.Seed);
    }

    private static IPEndPoint Norm(IPEndPoint ep) => ep.Address.IsIPv4MappedToIPv6 ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port) : ep;

    // ---------------------------------------------------------------- request loop

    private sealed class Request
    {
        public string Method, Path;
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public string Header(string k) => Headers.TryGetValue(k, out var v) ? v : null;
    }

    private sealed class Response
    {
        public int Status = 200;
        public string Text = "OK";
        public List<(string, string)> Headers = new();
        public byte[] Body;
        public string ContentType;
    }

    public async Task RunAsync(CancellationToken outer)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(outer, _cts.Token);
        var ct = linked.Token;
        var plain = new List<byte>();
        var buf = new byte[16384];
        while (!ct.IsCancellationRequested)
        {
            // Parse whatever complete requests we already have
            while (TryParse(plain, out var req))
            {
                var resp = Handle(req);
                await SendAsync(req, resp, ct);
                if (resp.Status == -1) return; // teardown of the whole connection
            }
            int n = await _net.ReadAsync(buf, ct);
            if (n <= 0) return;
            if (_cipher != null) plain.AddRange(_cipher.Decrypt(buf.AsSpan(0, n)));
            else plain.AddRange(buf.AsSpan(0, n).ToArray());
        }
    }

    private static bool TryParse(List<byte> data, out Request req)
    {
        req = null;
        int end = -1;
        for (int i = 0; i + 3 < data.Count; i++)
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n') { end = i; break; }
        if (end < 0) return false;
        var head = Encoding.UTF8.GetString(data.GetRange(0, end).ToArray()).Split("\r\n");
        var r = new Request();
        var first = head[0].Split(' ');
        if (first.Length < 2) { data.RemoveRange(0, end + 4); return false; }
        r.Method = first[0];
        r.Path = first[1];
        foreach (var line in head.Skip(1))
        {
            int c = line.IndexOf(':');
            if (c > 0) r.Headers[line[..c].Trim()] = line[(c + 1)..].Trim();
        }
        int len = int.TryParse(r.Header("Content-Length"), out var l) ? l : 0;
        if (data.Count < end + 4 + len) return false;
        r.Body = data.GetRange(end + 4, len).ToArray();
        data.RemoveRange(0, end + 4 + len);
        req = r;
        return true;
    }

    private async Task SendAsync(Request req, Response resp, CancellationToken ct)
    {
        var sb = new StringBuilder();
        int status = resp.Status == -1 ? 200 : resp.Status;
        sb.Append($"RTSP/1.0 {status} {(status == 200 ? "OK" : resp.Text)}\r\n");
        if (req.Header("CSeq") is string cseq) sb.Append($"CSeq: {cseq}\r\n");
        sb.Append($"Server: AirTunes/{AirPlayReceiver.SourceVersion}\r\n");
        foreach (var (k, v) in resp.Headers) sb.Append($"{k}: {v}\r\n");
        if (resp.Body != null && resp.Body.Length > 0)
        {
            sb.Append($"Content-Type: {resp.ContentType ?? "application/octet-stream"}\r\n");
            sb.Append($"Content-Length: {resp.Body.Length}\r\n");
        }
        else sb.Append("Content-Length: 0\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        if (resp.Body != null) bytes = bytes.Concat(resp.Body).ToArray();
        if (_cipher != null) bytes = _cipher.Encrypt(bytes);
        await _net.WriteAsync(bytes, ct);

        // Pairing finished with this response: everything after it is encrypted
        if (_cipher == null && _pairing.SharedSecret != null)
        {
            _sharedSecret = _pairing.SharedSecret;
            _cipher = ChannelCipher.ForControlServer(_sharedSecret);
            Log.Debug($"AirPlay: {_remote.Address} paired ({(_pairing.IsTransient ? "transient" : "verified")})");
        }
    }

    // ---------------------------------------------------------------- dispatch

    private Response Handle(Request r)
    {
        Log.Debug($"AirPlay <- {r.Method} {r.Path} ({r.Body.Length} bytes)");
        try
        {
            switch (r.Method)
            {
                case "OPTIONS":
                    return new Response { Headers = { ("Public", "ANNOUNCE, SETUP, RECORD, PAUSE, FLUSH, FLUSHBUFFERED, TEARDOWN, OPTIONS, POST, GET, PUT, SETPEERS, SETPEERSX, SETRATEANCHORTIME, GET_PARAMETER, SET_PARAMETER") } };
                case "GET" when r.Path == "/info":
                    return Plist(InfoPlist());
                case "POST":
                    return HandlePost(r);
                case "SETUP":
                    return HandleSetup(r);
                case "RECORD":
                    return new Response { Headers = { ("Audio-Latency", "0") } };
                case "GET_PARAMETER":
                    {
                        string body = Encoding.ASCII.GetString(r.Body);
                        if (body.Contains("volume"))
                            return new Response { Body = Encoding.ASCII.GetBytes($"volume: {_rx.Volume.ToString("0.000000", CultureInfo.InvariantCulture)}\r\n"), ContentType = "text/parameters" };
                        return new Response();
                    }
                case "SET_PARAMETER":
                    return HandleSetParameter(r);
                case "SETPEERS":
                case "SETPEERSX":
                    return new Response();
                case "SETRATEANCHORTIME":
                    return HandleAnchor(r);
                case "FLUSHBUFFERED":
                    return HandleFlushBuffered(r);
                case "FLUSH":
                    {
                        var info = r.Header("RTP-Info");
                        if (info != null && TryField(info, "rtptime", out uint rtp)) _rx.Player.FlushBefore(rtp);
                        return new Response();
                    }
                case "TEARDOWN":
                    return HandleTeardown(r);
                default:
                    return new Response { Status = 501, Text = "Not Implemented" };
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"AirPlay: error handling {r.Method} {r.Path}: {ex.Message}");
            return new Response { Status = 500, Text = "Internal Server Error" };
        }
    }

    private static Response Plist(object o) => new() { Body = BinaryPlist.Write(o), ContentType = "application/x-apple-binary-plist" };

    private Dictionary<string, object> InfoPlist() => new()
    {
        ["deviceID"] = _rx.Identity.DeviceId,
        ["features"] = (long)AirPlayReceiver.Features,
        ["featuresEx"] = Convert.ToBase64String(BitConverter.GetBytes(AirPlayReceiver.Features)).TrimEnd('='),
        ["statusFlags"] = (long)AirPlayReceiver.StatusFlags,
        ["model"] = AirPlayReceiver.Model,
        ["name"] = _rx.Name,
        ["pi"] = _rx.Identity.PairingIdentity,
        ["psi"] = _rx.Identity.PairingIdentity,
        ["pk"] = _rx.Identity.PublicKey,
        ["protocolVersion"] = "1.1",
        ["sourceVersion"] = AirPlayReceiver.SourceVersion,
        ["senderAddress"] = $"{_remote.Address}:{_remote.Port}",
        ["initialVolume"] = _rx.Volume,
        ["vv"] = 2L,
        ["volumeControlType"] = 4L,
        ["keepAliveSendStatsAsBody"] = false,
        ["canRecordScreenStream"] = false,
        ["screenDemoMode"] = false,
        ["playbackCapabilities"] = new Dictionary<string, object>
        {
            ["supportsInterstitials"] = false, ["supportsFPSSecureStop"] = false, ["supportsUIForAudioOnlyContent"] = false,
        },
        ["supportedFormats"] = new Dictionary<string, object>
        {
            ["audioStream"] = 0x40000L,               // ALAC 44.1 kHz 16-bit stereo (realtime)
            ["bufferStream"] = 0x400000L | 0x40000L,  // AAC-LC and ALAC, 44.1 kHz stereo (buffered)
        },
        ["txtAirPlay"] = _rx.AirPlayTxtBytes(),
    };

    private Response HandlePost(Request r)
    {
        switch (r.Path)
        {
            case "/pair-setup":
                return new Response { Body = _pairing.Setup(r.Body) };
            case "/pair-verify":
                return new Response { Body = _pairing.Verify(r.Body) };
            case "/pair-pin-start":
                return new Response();
            case "/fp-setup":
                var fp = FairPlaySetup.Respond(r.Body);
                return fp == null ? new Response { Status = 400, Text = "Bad Request" } : new Response { Body = fp };
            case "/feedback":
            case "/command":
            case "/audioMode":
                return new Response();
            default:
                return new Response { Status = 404, Text = "Not Found" };
        }
    }

    // ---------------------------------------------------------------- SETUP

    private Response HandleSetup(Request r)
    {
        var plist = r.Body.Length > 0 ? BinaryPlist.Read(r.Body) as Dictionary<string, object> : null;
        if (plist == null) return new Response { Status = 400, Text = "Bad Request" };
        if (plist.TryGetValue("name", out var n) && n is string name) _clientName = name;

        if (!plist.TryGetValue("streams", out var streamsObj))
        {
            // Session SETUP: timing and the event channel
            string timing = plist.GetString("timingProtocol");
            _ptp = timing == "PTP";
            if (timing == "NTP")
                Log.Warn("AirPlay: sender asked for NTP timing, which this receiver doesn't support (PTP only)");
            _eventListener?.Stop();
            _eventListener = Net.Listen(0);
            _ = Task.Run(EventAcceptLoop);
            var resp = new Dictionary<string, object>
            {
                ["eventPort"] = (long)((IPEndPoint)_eventListener.LocalEndpoint).Port,
                ["timingPort"] = 0L,
            };
            if (plist.ContainsKey("timingPeerInfo"))
            {
                var addrs = new List<object> { _local.Address.ToString() };
                foreach (var a in LocalAddresses()) if (!addrs.Contains(a)) addrs.Add(a);
                resp["timingPeerInfo"] = new Dictionary<string, object> { ["Addresses"] = addrs, ["ID"] = _local.Address.ToString() };
            }
            return Plist(resp);
        }

        // Stream SETUP
        var streams = streamsObj as List<object>;
        var s0 = streams?.FirstOrDefault() as Dictionary<string, object>;
        if (s0 == null) return new Response { Status = 400, Text = "Bad Request" };
        long type = s0.GetLong("type");
        byte[] shk = s0.TryGetValue("shk", out var k) ? k as byte[] : null;
        if (shk == null || shk.Length != 32) { Log.Warn("AirPlay: stream SETUP without a valid key"); return new Response { Status = 400, Text = "Bad Request" }; }

        _stream?.Dispose();
        _rx.SetActive(this, _clientName);
        _rx.Player.Reset();

        var streamResp = new Dictionary<string, object> { ["type"] = type };
        if (type == 96)
        {
            int spf = (int)s0.GetLong("spf", 352);
            var rt = new RealtimeStream(_rx.Player, shk, spf, _remote.Address);
            _stream = rt;
            streamResp["dataPort"] = (long)rt.DataPort;
            streamResp["controlPort"] = (long)rt.ControlPort;
            Log.Info($"AirPlay: realtime stream from {_clientName ?? _remote.Address.ToString()} (ALAC, {(_ptp ? "PTP" : "no PTP")} timing)");
        }
        else if (type == 103)
        {
            long fmt = s0.GetLong("audioFormat");
            var bs = new BufferedStream(_rx.Player, shk);
            _stream = bs;
            streamResp["dataPort"] = (long)bs.DataPort;
            streamResp["controlPort"] = (long)bs.ControlPort;
            streamResp["audioBufferSize"] = (long)BufferedStream.BufferSize;
            Log.Info($"AirPlay: buffered stream from {_clientName ?? _remote.Address.ToString()} (format 0x{fmt:X})");
        }
        else
        {
            Log.Warn($"AirPlay: unsupported stream type {type}");
            return new Response { Status = 400, Text = "Bad Request" };
        }
        _rx.Player.SetPlaying(type == 96); // realtime streams play as soon as anchors arrive; buffered waits for SETRATEANCHORTIME
        _rx.RaiseStateChanged();
        return Plist(new Dictionary<string, object> { ["streams"] = new List<object> { streamResp } });
    }

    private static IEnumerable<string> LocalAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var u in ni.GetIPProperties().UnicastAddresses)
            {
                if (IPAddress.IsLoopback(u.Address)) continue;
                yield return u.Address.ToString();
            }
        }
    }

    private async Task EventAcceptLoop()
    {
        try
        {
            var c = await _eventListener.AcceptTcpClientAsync(_cts.Token);
            _eventClient = c;
            var s = c.GetStream();
            var buf = new byte[4096];
            // We have nothing to send on it; just keep it open and drain it
            while (await s.ReadAsync(buf, _cts.Token) > 0) { }
        }
        catch { }
    }

    // ---------------------------------------------------------------- playback control

    private Response HandleSetParameter(Request r)
    {
        string ctype = r.Header("Content-Type") ?? "";
        if (ctype.StartsWith("text/parameters"))
        {
            foreach (var line in Encoding.UTF8.GetString(r.Body).Split('\n'))
            {
                var t = line.Trim();
                if (t.StartsWith("volume:", StringComparison.OrdinalIgnoreCase) &&
                    double.TryParse(t[7..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var vol))
                {
                    _rx.Volume = vol;
                    _rx.Player.SetAirPlayVolume(vol);
                    _rx.RaiseStateChanged();
                }
            }
        }
        return new Response();
    }

    private Response HandleAnchor(Request r)
    {
        var p = BinaryPlist.Read(r.Body) as Dictionary<string, object>;
        if (p == null) return new Response();
        if (p.ContainsKey("networkTimeSecs"))
        {
            ulong secs = (ulong)p.GetLong("networkTimeSecs");
            ulong frac = U64(p.GetValueOrDefault("networkTimeFrac"));
            ulong timeline = U64(p.GetValueOrDefault("networkTimeTimelineID"));
            long ns = (long)(secs * 1_000_000_000UL) + (long)((((frac >> 32) * 1_000_000_000UL) >> 32));
            uint rtp = (uint)p.GetLong("rtpTime");
            _rx.Player.SetAnchor(rtp, ns, timeline);
        }
        if (p.ContainsKey("rate"))
        {
            bool playing = (p.GetLong("rate") & 1) != 0;
            _rx.Player.SetPlaying(playing);
            if (!playing) _rx.Player.ClearAnchor();
            _rx.RaiseStateChanged();
        }
        return new Response();
    }

    private Response HandleFlushBuffered(Request r)
    {
        var p = BinaryPlist.Read(r.Body) as Dictionary<string, object>;
        if (p == null) return new Response();
        uint until = (uint)(p.GetLong("flushUntilSeq") & 0x7fffff);
        uint? from = p.ContainsKey("flushFromSeq") ? (uint)(p.GetLong("flushFromSeq") & 0x7fffff) : null;
        if (_stream is BufferedStream bs) bs.AddFlush(from, until);
        _rx.Player.FlushSeq(from, until);
        if (from == null)
        {
            _rx.Player.SetPlaying(false); // immediate flush stops the clock until the next anchor
            _rx.Player.ClearAnchor();
        }
        return new Response();
    }

    private Response HandleTeardown(Request r)
    {
        var p = r.Body.Length > 0 ? BinaryPlist.Read(r.Body) as Dictionary<string, object> : null;
        _stream?.Dispose();
        _stream = null;
        _rx.Player.Reset();
        if (p != null && p.ContainsKey("streams"))
        {
            // Just the stream: the sender may set up another on this connection
            _rx.SetActive(null, null);
            return new Response();
        }
        _rx.SetActive(null, null);
        return new Response { Status = -1 };
    }

    private static ulong U64(object o) => o switch
    {
        ulong u => u, long l => unchecked((ulong)l), int i => unchecked((ulong)i), uint ui => ui, _ => 0UL,
    };

    private static bool TryField(string header, string key, out uint value)
    {
        value = 0;
        foreach (var part in header.Split(';'))
        {
            var kv = part.Split('=');
            if (kv.Length == 2 && kv[0].Trim() == key && uint.TryParse(kv[1].Trim(), out value)) return true;
        }
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _stream?.Dispose();
        try { _eventListener?.Stop(); } catch { }
        try { _eventClient?.Dispose(); } catch { }
        try { _tcp.Dispose(); } catch { }
        _cipher?.Dispose();
    }
}

// =====================================================================================================
// Audio streams
// =====================================================================================================

internal abstract class AudioStream : IDisposable
{
    protected readonly ReceiverPlayer Player;
    protected readonly ChaCha20Poly1305 Cipher;
    protected readonly CancellationTokenSource Cts = new();
    public int DataPort { get; protected set; }
    public int ControlPort { get; protected set; }

    protected AudioStream(ReceiverPlayer player, byte[] key)
    {
        Player = player;
        Cipher = new ChaCha20Poly1305(key);
    }

    /// <summary>Decrypts an AirPlay 2 audio payload: [ciphertext][16-byte tag][8-byte nonce], AAD = 8 header bytes.</summary>
    protected byte[] Decrypt(ReadOnlySpan<byte> aad, ReadOnlySpan<byte> payloadWithTagAndNonce)
    {
        int n = payloadWithTagAndNonce.Length;
        if (n < 24) return null;
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        payloadWithTagAndNonce[(n - 8)..].CopyTo(nonce[4..]);
        var plain = new byte[n - 24];
        try
        {
            lock (Cipher) Cipher.Decrypt(nonce, payloadWithTagAndNonce[..(n - 24)], payloadWithTagAndNonce.Slice(n - 24, 16), plain, aad);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    public virtual void Dispose()
    {
        Cts.Cancel();
        Cipher.Dispose();
    }
}

/// <summary>Realtime (type 96): ALAC over UDP, timing from sync packets on the control port.</summary>
internal sealed class RealtimeStream : AudioStream
{
    private readonly UdpClient _data, _control;
    private readonly AlacDecoder _alac;
    private readonly int _spf;
    private IPEndPoint _senderControl;
    private int _expectedSeq = -1;

    public RealtimeStream(ReceiverPlayer player, byte[] key, int spf, IPAddress sender) : base(player, key)
    {
        _spf = spf;
        _alac = new AlacDecoder(spf);
        _data = Net.Udp();
        _control = Net.Udp();
        DataPort = ((IPEndPoint)_data.Client.LocalEndPoint).Port;
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint).Port;
        _ = Task.Run(() => Loop(_data, isControl: false));
        _ = Task.Run(() => Loop(_control, isControl: true));
    }

    private async Task Loop(UdpClient u, bool isControl)
    {
        while (!Cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await u.ReceiveAsync(Cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            try
            {
                var p = r.Buffer;
                if (!isControl) { Audio(p, 0); continue; }
                if (_senderControl == null) _senderControl = r.RemoteEndPoint;
                if (p.Length >= 28 && (p[1] & 0x7f) == 0x57) // 0xd7: time announce
                {
                    uint frame1 = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(4));
                    long remoteNs = (long)BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(8));
                    ulong clockId = BinaryPrimitives.ReadUInt64BigEndian(p.AsSpan(20));
                    // As Shairport Sync: the announced frame (which includes the sender's latency) less 11025
                    Player.SetAnchor(frame1 - 11025, remoteNs, clockId);
                }
                else if (p.Length > 16 && (p[1] & 0x7f) == 0x56) // 0xd6: retransmitted audio packet
                    Audio(p, 4);
            }
            catch (Exception ex) { Log.Debug($"AirPlay realtime packet: {ex.Message}"); }
        }
    }

    private void Audio(byte[] p, int off)
    {
        if (p.Length - off < 12 + 24) return;
        uint seq = BinaryPrimitives.ReadUInt16BigEndian(p.AsSpan(off + 2));
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(p.AsSpan(off + 4));
        var plain = Decrypt(p.AsSpan(off + 4, 8), p.AsSpan(off + 12));
        if (plain == null) return;
        var pcm = new short[_spf * 2 * 2];
        int frames = _alac.Decode(plain, pcm);
        if (frames > 0) Player.Put(ts, seq, pcm, frames);
        if (off == 0) CheckGap((int)seq);
    }

    private void CheckGap(int seq)
    {
        if (_expectedSeq >= 0)
        {
            int missing = (seq - _expectedSeq) & 0xffff;
            if (missing > 0 && missing < 200 && _senderControl != null)
            {
                var req = new byte[8];
                req[0] = 0x80; req[1] = 0xd5;
                BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(2), 1);
                BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(4), (ushort)_expectedSeq);
                BinaryPrimitives.WriteUInt16BigEndian(req.AsSpan(6), (ushort)missing);
                try { _control.Client.SendTo(req, Net.For(_control.Client, _senderControl)); } catch { }
            }
            if (missing >= 0x8000) return; // late/resent packet: keep expecting the newer one
        }
        _expectedSeq = (seq + 1) & 0xffff;
    }

    public override void Dispose()
    {
        base.Dispose();
        _data.Dispose();
        _control.Dispose();
    }
}

/// <summary>Buffered (type 103): AAC (or ALAC) over TCP, sent well ahead; timing from SETRATEANCHORTIME.</summary>
internal sealed class BufferedStream : AudioStream
{
    public const int BufferSize = 8 * 1024 * 1024;
    private const double MaxSecondsAhead = 20;
    private readonly TcpListener _listener;
    private readonly UdpClient _control;
    private TcpClient _client;
    private IAacDecoder _aac;
    private AlacDecoder _alac;
    private uint? _flushUntil;
    private readonly List<(uint from, uint until)> _deferred = new();
    private readonly object _flushLock = new();
    private bool _warnedFormat;

    public BufferedStream(ReceiverPlayer player, byte[] key) : base(player, key)
    {
        _listener = Net.Listen(0);
        DataPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _control = Net.Udp();
        ControlPort = ((IPEndPoint)_control.Client.LocalEndPoint).Port;
        _ = Task.Run(AcceptAndRead);
    }

    public void AddFlush(uint? from, uint until)
    {
        lock (_flushLock)
        {
            if (from == null) { _flushUntil = until; _deferred.Clear(); _aac?.Reset(); }
            else _deferred.Add((from.Value, until));
        }
    }

    private bool Flushed(uint seq)
    {
        lock (_flushLock)
        {
            if (_flushUntil is uint u)
            {
                if (ReceiverPlayer.SeqBefore(seq, u)) return true;
                _flushUntil = null; // reached the end of the flush
            }
            foreach (var (f, t) in _deferred)
                if (!ReceiverPlayer.SeqBefore(seq, f) && ReceiverPlayer.SeqBefore(seq, t)) return true;
            _deferred.RemoveAll(d => !ReceiverPlayer.SeqBefore(seq, d.until) && ((seq - d.until) & 0x7fffff) < 0x10000);
            return false;
        }
    }

    private async Task AcceptAndRead()
    {
        try
        {
            _client = await _listener.AcceptTcpClientAsync(Cts.Token);
            _client.ReceiveBufferSize = 1 << 20;
            var s = _client.GetStream();
            var lenBuf = new byte[2];
            var pkt = new byte[65536];
            while (!Cts.IsCancellationRequested)
            {
                // Back-pressure: don't decode more than ~20 s ahead of what's playing
                while (Player.SecondsQueuedAhead > MaxSecondsAhead && !Cts.IsCancellationRequested)
                    await Task.Delay(50, Cts.Token);

                await s.ReadExactlyAsync(lenBuf, Cts.Token);
                int len = BinaryPrimitives.ReadUInt16BigEndian(lenBuf) - 2;
                if (len <= 0) continue;
                await s.ReadExactlyAsync(pkt.AsMemory(0, len), Cts.Token);
                Packet(pkt.AsSpan(0, len));
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException) { }
        catch (Exception ex) { Log.Debug($"AirPlay buffered stream ended: {ex.Message}"); }
    }

    private void Packet(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12 + 24) return;
        uint seq = BinaryPrimitives.ReadUInt32BigEndian(p) & 0x7fffff;
        uint ts = BinaryPrimitives.ReadUInt32BigEndian(p[4..]);
        uint ssrc = BinaryPrimitives.ReadUInt32BigEndian(p[8..]);
        if (ssrc == 0) return; // filler
        if (Flushed(seq)) return;
        var plain = Decrypt(p.Slice(4, 8), p[12..]);
        if (plain == null) return;

        switch (ssrc)
        {
            case 0x16000000: // AAC-LC 44.1 kHz stereo
                if (_aac == null)
                {
                    _aac = AacDecoder.Create(44100, 2);
                    _aac.Decoded += (rtp, sq, pcm, frames) => Player.Put(rtp, sq, pcm, frames);
                }
                _aac.Submit(ts, seq, plain);
                break;
            case 0x0000FACE: // ALAC 44.1 kHz 16-bit
                _alac ??= new AlacDecoder(352);
                var buf = new short[352 * 2 * 2];
                int n = _alac.Decode(plain, buf);
                if (n > 0) Player.Put(ts, seq, buf, n);
                break;
            default:
                if (!_warnedFormat) { Log.Warn($"AirPlay: unsupported buffered audio format 0x{ssrc:X8}"); _warnedFormat = true; }
                break;
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        try { _listener.Stop(); } catch { }
        try { _client?.Dispose(); } catch { }
        _control.Dispose();
        _aac?.Dispose();
    }
}
