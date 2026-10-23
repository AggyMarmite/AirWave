using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirWave.Core.Crypto;
using AirWave.Core.Plist;
using AirWave.Core.Rtsp;
using AirWave.Core.Streaming;
using AirWave.Core.Timing;

namespace AirWave.Core.Mirroring;

public enum MirrorProblem { None, NeedsPin, NeedsPassword, WrongPassword, PairingLost, Refused, Network }

public sealed class MirrorException : Exception
{
    public MirrorProblem Problem { get; }
    public MirrorException(MirrorProblem p, string message) : base(message) { Problem = p; }
}

/// <summary>
/// One AirPlay 2 screen-mirroring session to an Apple TV (no FairPlay). Sequence and formats follow
/// AirPlayDroid (GPLv3), which verified them on an Apple TV 4K with tvOS 26.6:
///  1. pair-verify with the stored pairing (or transient pairing for TVs that allow it), then encrypted control;
///  2. control SETUP (PTP timing named, but no PTP client needed), Digest auth for password-protected TVs;
///  3. the TV's event channel (reports its display size);
///  4. RECORD, optional type-96 screen audio, then a type-110 screen stream giving a data port;
///  5. on the data port: avcC codec packets and 128-byte-header H.264 frames sealed with ChaCha20-Poly1305
///     under HKDF(pairing secret, "DataStream-Salt"+streamId, "DataStream-Output-Encryption-Key").
/// Frames are stamped with capture time + <see cref="LatencyMs"/> on the TV's own clock.
/// </summary>
public sealed class MirrorSession : IDisposable
{
    public const long LatencyMs = 250;
    private const string SourceVersion = "980.71.1";
    private const int HeaderSize = 128;

    private readonly RtspClient _rtsp;
    private readonly string _url;
    private readonly TcpClient _data;
    private readonly NetworkStream _dataStream;
    private readonly ChaCha20Poly1305 _videoCipher;
    private readonly ReceiverClock _clock;
    private readonly EventsChannel _events;
    private readonly ScreenAudio _audio;
    private readonly string _password;
    private readonly object _write = new();
    private readonly CancellationTokenSource _cts = new();
    private ulong _nonce;
    private long _lastTs;
    private byte[] _pendingConfig;
    private volatile bool _firstFrameSent;
    private int _closed;
    private (string realm, string nonce)? _digest;
    private long _clockId;

    public string Name { get; }
    /// <summary>The TV's screen size, if it told us.</summary>
    public (int Width, int Height)? Display { get; private set; }
    public bool HasAudio => _audio != null;
    public string AudioProblem { get; private set; }
    public bool IsOpen => _closed == 0;
    /// <summary>Raised once if the session ends on its own (TV turned off, network gone...).</summary>
    public event Action<string> Ended;

    private MirrorSession(string name, RtspClient rtsp, string url, TcpClient data, byte[] videoKey, ReceiverClock clock,
        EventsChannel events, ScreenAudio audio, string password, (string, string)? digest, long clockId)
    {
        Name = name;
        _rtsp = rtsp; _url = url; _data = data; _dataStream = data.GetStream();
        _videoCipher = new ChaCha20Poly1305(videoKey);
        _clock = clock; _events = events; _audio = audio; _password = password; _digest = digest; _clockId = clockId;
    }

    // ================================================================= pairing

    private static Dictionary<string, string> PairHeaders(string hkp, string clientId, string clientName) => new()
    {
        ["X-Apple-HKP"] = hkp,
        ["X-Apple-Client-ID"] = clientId,
        ["X-Apple-Client-Name"] = clientName,
    };

    /// <summary>
    /// Pairs with a TV for good. With no password, asks the TV to show a PIN and waits for <paramref name="askPin"/>
    /// (return null to give up). With a password (TVs set to require one), uses that instead.
    /// </summary>
    public static async Task<MirrorCredentials> PairAsync(IPEndPoint tv, string clientName, Func<Task<string>> askPin, string password, CancellationToken ct = default)
    {
        using var rtsp = await RtspClient.ConnectAsync(tv, 5000, ct);
        rtsp.DefaultHeaders["User-Agent"] = "AirPlay/" + SourceVersion;
        string clientId = Guid.NewGuid().ToString().ToUpperInvariant();
        var hdr = PairHeaders("3", clientId, clientName);

        string pin = password;
        if (pin == null)
        {
            var start = await rtsp.SendAsync("POST", "/pair-pin-start", headers: hdr, ct: ct);
            if (!start.Ok) throw new MirrorException(MirrorProblem.Refused, $"The TV wouldn't show a code ({start})");
            pin = await askPin();
            if (string.IsNullOrWhiteSpace(pin)) throw new OperationCanceledException();
        }

        var (seed, _) = Curve25519.Ed25519KeyPair();
        var cli = new PairingClient(clientId, seed);
        var r2 = await rtsp.SendAsync("POST", "/pair-setup", cli.SetupM1(), "application/x-apple-binary-plist", hdr, 60000, ct);
        if (!r2.Ok) throw new MirrorException(MirrorProblem.Refused, $"Pairing refused ({r2})");
        var r4 = await rtsp.SendAsync("POST", "/pair-setup", cli.SetupM3(r2.Body, pin.Trim()), "application/x-apple-binary-plist", hdr, ct: ct);
        try { cli.SetupCheckM4(r4.Body); }
        catch (InvalidOperationException)
        {
            throw new MirrorException(password == null ? MirrorProblem.NeedsPin : MirrorProblem.WrongPassword,
                password == null ? "That code wasn't right. Try again – the TV will show a new one." : "The TV didn't accept that password.");
        }
        var r6 = await rtsp.SendAsync("POST", "/pair-setup", cli.SetupM5(), "application/x-apple-binary-plist", hdr, ct: ct);
        cli.SetupCheckM6(r6.Body);
        return new MirrorCredentials
        {
            ClientId = clientId,
            ClientSeed = Convert.ToBase64String(seed),
            ReceiverId = cli.DeviceId,
            ReceiverKey = Convert.ToBase64String(cli.DeviceLtpk),
        };
    }

    // ================================================================= session

    /// <param name="creds">A stored pairing, or null to try transient pairing (TVs open to everyone).</param>
    /// <param name="password">The TV's AirPlay password, if it has one.</param>
    public static async Task<MirrorSession> OpenAsync(IPEndPoint tv, string tvName, MirrorCredentials creds, string password,
        string senderName, bool withAudio, CancellationToken ct = default)
    {
        var rtsp = await RtspClient.ConnectAsync(tv, 5000, ct);
        EventsChannel events = null;
        ScreenAudio audio = null;
        TcpClient data = null;
        try
        {
            rtsp.DefaultHeaders["User-Agent"] = "AirPlay/" + SourceVersion;
            byte[] secret;
            string clientId = creds?.ClientId ?? StableTransientId(senderName);
            if (creds != null)
            {
                var hdr = PairHeaders("3", clientId, senderName);
                var cli = new PairingClient(clientId, Convert.FromBase64String(creds.ClientSeed));
                var v2 = await rtsp.SendAsync("POST", "/pair-verify", cli.VerifyM1(), "application/x-apple-binary-plist", hdr, ct: ct);
                if (!v2.Ok) throw new MirrorException(MirrorProblem.PairingLost, $"The TV no longer recognises this PC ({v2})");
                byte[] m3;
                try { m3 = cli.VerifyM3(v2.Body, Convert.FromBase64String(creds.ReceiverKey)); }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                { throw new MirrorException(MirrorProblem.PairingLost, "The TV no longer recognises this PC"); }
                var v4 = await rtsp.SendAsync("POST", "/pair-verify", m3, "application/x-apple-binary-plist", hdr, ct: ct);
                if (!v4.Ok) throw new MirrorException(MirrorProblem.PairingLost, "The TV no longer recognises this PC");
                cli.VerifyCheckM4(v4.Body);
                secret = cli.SharedSecret;
            }
            else
            {
                var hdr = PairHeaders("4", clientId, senderName);
                var cli = new PairingClient(clientId, Curve25519.Ed25519KeyPair().seed);
                var r2 = await rtsp.SendAsync("POST", "/pair-setup", cli.SetupM1(transient: true), "application/x-apple-binary-plist", hdr, ct: ct);
                if (r2.Status == 470 || r2.Status == 401 || r2.Status == 403)
                    throw new MirrorException(MirrorProblem.NeedsPin, "The TV wants this PC paired first");
                try
                {
                    var r4 = await rtsp.SendAsync("POST", "/pair-setup", cli.SetupM3(r2.Body, "3939"), "application/x-apple-binary-plist", hdr, ct: ct);
                    cli.SetupCheckM4(r4.Body, transient: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
                { throw new MirrorException(MirrorProblem.NeedsPin, "The TV wants this PC paired first"); }
                secret = cli.SharedSecret;
            }
            rtsp.EnableEncryption(secret);

            var local = rtsp.Local.Address.IsIPv4MappedToIPv6 ? rtsp.Local.Address.MapToIPv4() : rtsp.Local.Address;
            long controlId = RandomId();
            string url = $"rtsp://{tv.Address}:{tv.Port}/{controlId}";
            var idSeed = SHA256.HashData(Encoding.UTF8.GetBytes(clientId));
            string deviceId = $"02:AD:D5:{idSeed[0]:X2}:{idSeed[1]:X2}:{idSeed[2]:X2}";
            var peer = new Dictionary<string, object>
            {
                ["ID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["SupportsClockPortMatchingOverride"] = true,
                ["DeviceType"] = 0L,
                ["Addresses"] = new List<object> { local.ToString() },
            };
            var setupBody = new Dictionary<string, object>
            {
                ["deviceID"] = deviceId, ["macAddress"] = deviceId,
                ["sessionUUID"] = Guid.NewGuid().ToString().ToUpperInvariant(),
                ["sourceVersion"] = SourceVersion,
                ["isScreenMirroringSession"] = true,
                ["timingProtocol"] = "PTP",
                ["timingPeerInfo"] = peer,
                ["timingPeerList"] = new List<object> { new Dictionary<string, object>(peer) },
                ["osBuildVersion"] = "13F69",
                ["model"] = "AirWave",
                ["name"] = senderName,
                ["updateSessionRequest"] = false,
                ["combinedGetInfoWithControlSetup"] = true,
            };

            (string, string)? digest = null;
            async Task<RtspResponse> Req(string method, string uri, byte[] body = null, string ctype = null, Dictionary<string, string> extra = null)
            {
                var h = new Dictionary<string, string>(extra ?? new());
                if (digest is (string realm, string nonce) && password != null) h["Authorization"] = Digest(realm, nonce, method, uri, password);
                var r = await rtsp.SendAsync(method, uri, body, ctype, h, ct: ct);
                if (r.Status == 401 && password != null && digest == null && r.Headers.TryGetValue("WWW-Authenticate", out var wa) && ParseDigest(wa) is { } d)
                {
                    digest = d;
                    h["Authorization"] = Digest(d.realm, d.nonce, method, uri, password);
                    r = await rtsp.SendAsync(method, uri, body, ctype, h, ct: ct);
                }
                return r;
            }

            long firstAt = MonoClock.NowNs;
            var first = await Req("SETUP", url, BinaryPlist.Write(setupBody), "application/x-apple-binary-plist");
            if (first.Status == 401)
                throw new MirrorException(password == null ? MirrorProblem.NeedsPassword : MirrorProblem.WrongPassword,
                    password == null ? "This TV needs its AirPlay password" : "The TV didn't accept that password");
            if (!first.Ok) throw new MirrorException(MirrorProblem.Refused, $"The TV refused screen mirroring ({first})");
            var setup = BinaryPlist.Read(first.Body) as Dictionary<string, object> ?? new();
            var clock = new ReceiverClock(ReceiverClock.ReceiverMsOf(first.Headers) ?? 0, firstAt);
            void Observe(RtspResponse r) { if (ReceiverClock.ReceiverMsOf(r.Headers) is long ms) clock.Observe(ms, MonoClock.NowNs); }
            long clockId = setup.TryGetValue("timingPeerInfo", out var tpi) && tpi is Dictionary<string, object> tp ? tp.GetLong("ClockID") : 0;

            (int, int)? display = null;
            long screenFormats = -1;
            var infoArrived = new TaskCompletionSource();
            long eventPort = setup.GetLong("eventPort");
            if (eventPort > 0)
            {
                events = await EventsChannel.ConnectAsync(tvName, new IPEndPoint(tv.Address, (int)eventPort), secret, ct);
                events.Command += cmd =>
                {
                    if (cmd.GetValueOrDefault("value") is not Dictionary<string, object> v) return;
                    switch (cmd.GetString("type"))
                    {
                        case "updateInfo":
                            if (v.GetValueOrDefault("displays") is List<object> ds && ds.FirstOrDefault() is Dictionary<string, object> d0)
                            {
                                int w = (int)d0.GetLong("widthPixels"), hgt = (int)d0.GetLong("heightPixels");
                                if (w > 0 && hgt > 0) display = (w, hgt);
                            }
                            if (v.GetValueOrDefault("supportedFormats") is Dictionary<string, object> f) screenFormats = f.GetLong("screenStream", -1);
                            infoArrived.TrySetResult();
                            break;
                        case "updateTimingPeerInfo":
                            long cid = v.GetLong("ClockID");
                            if (cid != 0) Interlocked.Exchange(ref clockId, cid);
                            break;
                    }
                };
            }

            if (!(setup.TryGetValue("skipRecord", out var sr) && sr is true))
            {
                var rec = await Req("RECORD", url, extra: new() { ["Range"] = "npt=0-", ["RTP-Info"] = "seq=0;rtptime=0" });
                if (!rec.Ok) throw new MirrorException(MirrorProblem.Refused, $"The TV refused to start ({rec})");
                Observe(rec);
            }
            await Task.WhenAny(infoArrived.Task, Task.Delay(1500, ct));

            string audioProblem = null;
            if (withAudio)
            {
                if (screenFormats >= 0 && (screenFormats & 0x40000) == 0) audioProblem = "the TV doesn't take sound with mirroring";
                else
                {
                    try { audio = await ScreenAudio.SetupAsync(Req, url, controlId, tv.Address, clock, () => Interlocked.Read(ref clockId)); }
                    catch (Exception ex) when (ex is not OperationCanceledException) { audioProblem = ex.Message; }
                }
            }

            long videoId = RandomId();
            var stream = new Dictionary<string, object>
            {
                ["type"] = 110L,
                ["streamConnectionID"] = videoId,
                ["latencyMs"] = LatencyMs,
                ["timestampInfo"] = new List<object>(new[] { "SubSu", "BePxT", "AfPxT", "BefEn", "EmEnc" }.Select(n => (object)new Dictionary<string, object> { ["name"] = n })),
                ["shk"] = ChannelCipher.Derive(secret, "Control-Salt", "Control-Write-Encryption-Key").AsSpan(0, 16).ToArray(),
                ["shiv"] = ChannelCipher.Derive(secret, "Control-Salt", "Control-Read-Encryption-Key").AsSpan(0, 16).ToArray(),
            };
            var vr = await Req("SETUP", $"rtsp://{tv.Address}:{tv.Port}/{videoId}", BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { stream } }), "application/x-apple-binary-plist");
            if (!vr.Ok) throw new MirrorException(MirrorProblem.Refused, $"The TV refused the screen stream ({vr})");
            Observe(vr);
            var vs = ((BinaryPlist.Read(vr.Body) as Dictionary<string, object>)?.GetValueOrDefault("streams") as List<object>)?.FirstOrDefault() as Dictionary<string, object>;
            long dataPort = vs?.GetLong("dataPort") ?? 0;
            if (dataPort <= 0) throw new MirrorException(MirrorProblem.Refused, "The TV didn't give a port for the picture");

            data = new TcpClient(tv.AddressFamily) { NoDelay = true, SendBufferSize = 1 << 20 };
            await data.ConnectAsync(tv.Address, (int)dataPort, ct);

            if (audio != null)
                for (int i = 0; i < 2; i++) Observe(await Req("SET_PARAMETER", url, Encoding.ASCII.GetBytes("volume: 0.000000\r\n"), "text/parameters"));

            var videoKey = ChannelCipher.Derive(secret, "DataStream-Salt" + videoId, "DataStream-Output-Encryption-Key");
            var session = new MirrorSession(tvName, rtsp, url, data, videoKey, clock, events, audio, password, digest, clockId)
            {
                Display = display,
                AudioProblem = audioProblem,
            };
            if (events != null)
                events.Command += cmd =>
                {
                    if (cmd.GetString("type") == "updateTimingPeerInfo" && cmd.GetValueOrDefault("value") is Dictionary<string, object> v && v.GetLong("ClockID") is long c && c != 0)
                        Interlocked.Exchange(ref session._clockId, c);
                };
            session.Start();
            Log.Info($"Mirroring to {tvName}: screen {(display is (int w, int h) ? $"{w}x{h}" : "size unknown")}, sound {(audio != null ? "on" : "off" + (audioProblem != null ? $" ({audioProblem})" : ""))}");
            return session;
        }
        catch
        {
            try { data?.Dispose(); } catch { }
            audio?.Dispose();
            events?.Dispose();
            rtsp.Dispose();
            throw;
        }
    }

    private static string StableTransientId(string name) =>
        new Guid(MD5.HashData(Encoding.UTF8.GetBytes("airwave-mirror:" + Environment.MachineName + name))).ToString().ToUpperInvariant();

    private static long RandomId() => BinaryPrimitives.ReadInt64LittleEndian(RandomNumberGenerator.GetBytes(8)) & long.MaxValue;

    private static (string realm, string nonce)? ParseDigest(string header)
    {
        if (!header.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return null;
        var m = System.Text.RegularExpressions.Regex.Matches(header[6..], "(\\w+)\\s*=\\s*(?:\"([^\"]*)\"|([^,\\s]+))");
        string realm = null, nonce = null;
        foreach (System.Text.RegularExpressions.Match x in m)
        {
            string v = x.Groups[2].Success && x.Groups[2].Value.Length > 0 ? x.Groups[2].Value : x.Groups[3].Value;
            if (x.Groups[1].Value.Equals("realm", StringComparison.OrdinalIgnoreCase)) realm = v;
            if (x.Groups[1].Value.Equals("nonce", StringComparison.OrdinalIgnoreCase)) nonce = v;
        }
        return realm != null && nonce != null ? (realm, nonce) : null;
    }

    private static string Digest(string realm, string nonce, string method, string uri, string password)
    {
        static string Md5(string s) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
        string ha1 = Md5($"AirPlay:{realm}:{password}");
        string ha2 = Md5($"{method}:{uri}");
        return $"Digest username=\"AirPlay\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{uri}\", response=\"{Md5($"{ha1}:{nonce}:{ha2}")}\"";
    }

    // ================================================================= running

    private void Start()
    {
        // The TV never writes on the data channel: a read that returns means it hung up
        _ = Task.Run(async () =>
        {
            try { var b = new byte[16]; await _dataStream.ReadAsync(b, _cts.Token); } catch { }
            End("the TV closed the picture connection");
        });
        // Heartbeat once a second once video flows
        _ = Task.Run(async () =>
        {
            var hb = new byte[HeaderSize];
            hb[4] = 0x02; hb[6] = 0x1e;
            while (IsOpen && !_firstFrameSent) await Task.Delay(50);
            while (IsOpen)
            {
                try { lock (_write) _dataStream.Write(hb); }
                catch (Exception ex) { End("picture connection failed: " + ex.Message); break; }
                try { await Task.Delay(1000, _cts.Token); } catch { break; }
            }
        });
        // /feedback every 2 s: keeps the session alive and refreshes the TV clock mapping
        _ = Task.Run(async () =>
        {
            int failures = 0;
            while (IsOpen)
            {
                try { await Task.Delay(2000, _cts.Token); } catch { break; }
                try
                {
                    var r = await _rtsp.SendAsync("POST", "/feedback", headers: AuthFor("POST", "/feedback"), ct: _cts.Token);
                    if (r.Ok) { failures = 0; if (ReceiverClock.ReceiverMsOf(r.Headers) is long ms) _clock.Observe(ms, MonoClock.NowNs); }
                    else failures++;
                }
                catch (OperationCanceledException) { break; }
                catch { failures++; }
                if (failures >= 3) { End("the TV stopped answering"); break; }
            }
        });
    }

    /// <summary>Sets the H.264 parameter sets (an avcC record). Sent just before the next keyframe.</summary>
    public void SetCodecConfig(byte[] avcC, int width, int height)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, avcC.Length);
        header[4] = 0x01; header[6] = 0x16; header[7] = 0x01;
        foreach (int off in new[] { 16, 40, 56 })
        {
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(off), width);
            BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(off + 4), height);
        }
        lock (_write) _pendingConfig = header.Concat(avcC).ToArray();
    }

    /// <summary>Sends one frame in AVCC form (4-byte big-endian NAL lengths), captured at MonoClock time captureNs.</summary>
    public void SendFrame(ReadOnlySpan<byte> avcc, bool keyframe, long captureNs)
    {
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(header, avcc.Length + 16);
        header[4] = 0x00;
        header[5] = keyframe ? (byte)0x10 : (byte)0x00;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(40), Interlocked.Read(ref _clockId));
        var sealedData = new byte[avcc.Length + 16];
        lock (_write)
        {
            if (!IsOpen) throw new IOException("mirroring session is closed");
            long ts = Math.Max(ReceiverClock.ToFixed32(_clock.ReceiverNs(captureNs) + LatencyMs * 1_000_000), _lastTs);
            _lastTs = ts;
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), ts);
            if (keyframe && _pendingConfig != null)
            {
                BinaryPrimitives.WriteInt64LittleEndian(_pendingConfig.AsSpan(8), ts);
                _dataStream.Write(_pendingConfig);
                _pendingConfig = null;
            }
            Span<byte> nonce = stackalloc byte[12];
            nonce.Clear();
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _nonce++);
            _videoCipher.Encrypt(nonce, avcc, sealedData.AsSpan(0, avcc.Length), sealedData.AsSpan(avcc.Length, 16), header);
            _dataStream.Write(header);
            _dataStream.Write(sealedData);
            _firstFrameSent = true;
        }
    }

    /// <summary>352 stereo frames of 44.1 kHz PCM whose first sample was captured at MonoClock time captureNs.</summary>
    public void SendAudio(ReadOnlySpan<short> pcm, long captureNs)
    {
        if (IsOpen) _audio?.Send(pcm, captureNs);
    }

    private Dictionary<string, string> AuthFor(string method, string uri) =>
        _digest is (string realm, string nonce) && _password != null
            ? new() { ["Authorization"] = Digest(realm, nonce, method, uri, _password) }
            : null;

    private void End(string reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Log.Info($"Mirroring to {Name} ended: {reason}");
        Shutdown(teardown: false);
        Ended?.Invoke(reason);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Shutdown(teardown: true);
    }

    private void Shutdown(bool teardown)
    {
        _cts.Cancel();
        if (teardown) { try { _rtsp.SendAsync("TEARDOWN", _url, headers: AuthFor("TEARDOWN", _url), timeoutMs: 1500).Wait(2000); } catch { } }
        _audio?.Dispose();
        try { _data.Dispose(); } catch { }
        _events?.Dispose();
        _rtsp.Dispose();
        _videoCipher.Dispose();
    }

    // ================================================================= screen audio (type 96)

    /// <summary>
    /// PC sound alongside the picture: uncompressed ALAC over RTP/UDP, sealed with a random key, timed with
    /// TimeAnnounce packets on the TV's clock so it plays together with the frames captured at the same moment.
    /// </summary>
    private sealed class ScreenAudio : IDisposable
    {
        public const int Frames = 352;
        private const int Rate = 44100;
        private static readonly long LatencySamples = LatencyMs * Rate / 1000;
        private readonly IPEndPoint _dataEp, _controlEp;
        private readonly Socket _dataSock, _controlSock;
        private readonly ChaCha20Poly1305 _cipher;
        private readonly ReceiverClock _clock;
        private readonly Func<long> _clockId;
        private readonly byte[][] _history = new byte[512][];
        private readonly object _lock = new();
        private readonly CancellationTokenSource _cts = new();
        private ushort _seq = 1;
        private uint _rtp = (uint)RandomNumberGenerator.GetInt32(int.MaxValue);
        private ulong _nonce;
        private long _lastSync = long.MinValue, _expected = long.MinValue;
        private readonly byte[] _enc = new byte[AlacEncoder.MaxSize(Frames)];

        private ScreenAudio(IPAddress host, int dataPort, int controlPort, Socket control, byte[] key, ReceiverClock clock, Func<long> clockId)
        {
            _dataEp = new IPEndPoint(host, dataPort);
            _controlEp = new IPEndPoint(host, controlPort);
            _controlSock = control;
            _dataSock = new Socket(host.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _cipher = new ChaCha20Poly1305(key);
            _clock = clock;
            _clockId = clockId;
            _ = Task.Run(ServeControl);
        }

        public static async Task<ScreenAudio> SetupAsync(Func<string, string, byte[], string, Dictionary<string, string>, Task<RtspResponse>> req,
            string url, long streamId, IPAddress host, ReceiverClock clock, Func<long> clockId)
        {
            var control = new Socket(host.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            control.Bind(new IPEndPoint(host.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
            int localControl = ((IPEndPoint)control.LocalEndPoint).Port;
            var key = RandomNumberGenerator.GetBytes(32);
            try
            {
                RtspResponse r = null;
                foreach (bool modern in new[] { true, false })
                {
                    var s = new Dictionary<string, object>
                    {
                        ["type"] = 96L, ["streamConnectionID"] = streamId, ["ct"] = 2L, ["spf"] = (long)Frames, ["sr"] = (long)Rate,
                        ["audioFormat"] = 0x40000L, ["audioMode"] = "default", ["usingScreen"] = true,
                        ["latencyMin"] = 0L, ["latencyMax"] = LatencySamples, ["shk"] = key,
                    };
                    if (modern)
                    {
                        s["isMedia"] = false;
                        s["supportsDynamicStreamID"] = true;
                        s["streamConnections"] = new Dictionary<string, object>
                        {
                            ["streamConnectionTypeRTP"] = new Dictionary<string, object> { ["streamConnectionKeyUseStreamEncryptionKey"] = true },
                            ["streamConnectionTypeRTCP"] = new Dictionary<string, object> { ["streamConnectionKeyPort"] = (long)localControl },
                        };
                    }
                    else s["controlPort"] = (long)localControl;
                    r = await req("SETUP", url, BinaryPlist.Write(new Dictionary<string, object> { ["streams"] = new List<object> { s } }), "application/x-apple-binary-plist", null);
                    if (r.Ok || r.Status is not (400 or 406 or 415 or 455 or 500 or 501)) break;
                }
                if (!r.Ok) throw new MirrorException(MirrorProblem.Refused, $"the TV refused sound ({r})");
                var st = ((BinaryPlist.Read(r.Body) as Dictionary<string, object>)?.GetValueOrDefault("streams") as List<object>)?.FirstOrDefault() as Dictionary<string, object>
                         ?? throw new MirrorException(MirrorProblem.Refused, "no sound stream in the TV's reply");
                long dataPort = st.GetLong("dataPort"), ctrlPort = st.GetLong("controlPort");
                if (st.GetValueOrDefault("streamConnections") is Dictionary<string, object> conns)
                {
                    long P(string t) => conns.GetValueOrDefault(t) is Dictionary<string, object> c ? c.GetLong("streamConnectionKeyPort") : 0;
                    if (P("streamConnectionTypeRTP") is long p1 && p1 > 0) dataPort = p1;
                    if (P("streamConnectionTypeRTCP") is long p2 && p2 > 0) ctrlPort = p2;
                }
                if (dataPort <= 0 || ctrlPort <= 0) throw new MirrorException(MirrorProblem.Refused, "no sound ports in the TV's reply");
                return new ScreenAudio(host, (int)dataPort, (int)ctrlPort, control, key, clock, clockId);
            }
            catch { control.Dispose(); throw; }
        }

        public void Send(ReadOnlySpan<short> pcm, long captureNs)
        {
            lock (_lock)
            {
                long frameNs = Frames * 1_000_000_000L / Rate;
                if (_expected != long.MinValue)
                {
                    long gap = captureNs - _expected;
                    if (gap > 100_000_000L) { _rtp += (uint)(gap * Rate / 1_000_000_000L); _lastSync = long.MinValue; }
                    else if (gap < -100_000_000L) _lastSync = long.MinValue;
                }
                _expected = captureNs + frameNs;
                if (_lastSync == long.MinValue || captureNs - _lastSync >= 1_000_000_000L)
                {
                    SendSync(first: _nonce == 0, captureNs);
                    _lastSync = captureNs;
                }

                int len = AlacEncoder.EncodeStereo16(pcm, Frames, Frames, _enc);
                var pkt = new byte[12 + len + 16 + 8];
                pkt[0] = 0x80; pkt[1] = 0x60;
                BinaryPrimitives.WriteUInt16BigEndian(pkt.AsSpan(2), _seq);
                BinaryPrimitives.WriteUInt32BigEndian(pkt.AsSpan(4), _rtp);
                Span<byte> nonce = stackalloc byte[12];
                nonce.Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _nonce++);
                _cipher.Encrypt(nonce, _enc.AsSpan(0, len), pkt.AsSpan(12, len), pkt.AsSpan(12 + len, 16), pkt.AsSpan(4, 8));
                nonce[4..].CopyTo(pkt.AsSpan(12 + len + 16));
                _history[_seq & 511] = pkt;
                try { _dataSock.SendTo(pkt, _dataEp); } catch { }
                _seq++;
                _rtp += Frames;
            }
        }

        private void SendSync(bool first, long captureNs)
        {
            var p = new byte[28];
            p[0] = first ? (byte)0x90 : (byte)0x80; p[1] = 0xD7;
            BinaryPrimitives.WriteUInt16BigEndian(p.AsSpan(2), 6);
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(4), (uint)(_rtp - LatencySamples));
            BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(8), _clock.ReceiverNs(captureNs));
            BinaryPrimitives.WriteUInt32BigEndian(p.AsSpan(16), _rtp);
            BinaryPrimitives.WriteInt64BigEndian(p.AsSpan(20), _clockId());
            try { _controlSock.SendTo(p, _controlEp); } catch { }
        }

        private async Task ServeControl()
        {
            var buf = new byte[2048];
            while (!_cts.IsCancellationRequested)
            {
                SocketReceiveFromResult r;
                try { r = await _controlSock.ReceiveFromAsync(buf, new IPEndPoint(IPAddress.Any, 0), _cts.Token); }
                catch { break; }
                if (r.ReceivedBytes < 8 || (buf[1] & 0x7f) != 0x55) continue;
                ushort reqSeq = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(2));
                int firstSeq = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(4)), count = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(6));
                for (int i = 0; i < Math.Min(count, 512); i++)
                {
                    int want = (firstSeq + i) & 0xffff;
                    byte[] orig;
                    lock (_lock) orig = _history[want & 511];
                    if (orig == null || BinaryPrimitives.ReadUInt16BigEndian(orig.AsSpan(2)) != want) continue;
                    var reply = new byte[4 + orig.Length];
                    reply[0] = 0x80; reply[1] = 0xD6;
                    BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), reqSeq);
                    orig.CopyTo(reply, 4);
                    try { _controlSock.SendTo(reply, r.RemoteEndPoint); } catch { }
                }
            }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _controlSock.Dispose();
            _dataSock.Dispose();
            _cipher.Dispose();
        }
    }
}
