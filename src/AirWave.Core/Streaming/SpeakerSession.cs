using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using AirWave.Core.Crypto;
using AirWave.Core.Discovery;
using AirWave.Core.Plist;
using AirWave.Core.Rtsp;

namespace AirWave.Core.Streaming;

public enum SpeakerState { Idle, Connecting, Ready, Streaming, Failed }

/// <summary>Why a speaker couldn't be used, so the UI can say something helpful.</summary>
public enum SpeakerProblem { None, Unreachable, AccessRestricted, PasswordRequired, Rejected, Dropped }

/// <summary>
/// One AirPlay 2 speaker taking part in the stream: the encrypted RTSP control connection,
/// transient pairing, stream setup, the per-speaker audio key, volume and teardown.
/// </summary>
public sealed class SpeakerSession : IAsyncDisposable
{
    public const int SamplesPerPacket = 352;
    public const int SampleRate = 44100;

    private readonly StreamEngine _engine;
    private RtspClient _rtsp;
    private Socket _data;
    private byte[] _sharedSecret;
    private ChaCha20Poly1305 _audioCipher;
    private string _sessionUrl;
    private uint _sessionId;
    private EventsChannel _events;
    private Timer _keepAlive;
    private int _ptpPeerId = -1;
    private readonly SemaphoreSlim _cmdLock = new(1, 1);

    public SpeakerInfo Info { get; }
    public SpeakerState State { get; private set; } = SpeakerState.Idle;
    public SpeakerProblem Problem { get; private set; }
    public string ProblemText { get; private set; }
    public bool UsesPtp { get; private set; }
    public IPEndPoint ControlEndPoint { get; private set; }
    public IPAddress Address { get; private set; }
    public double Volume { get; private set; } = 0.5;

    /// <summary>Set when the speaker has just been set up and needs a "start" sync packet and a marker packet.</summary>
    internal volatile bool NeedsStart;

    public event Action<SpeakerSession> StateChanged;

    internal SpeakerSession(StreamEngine engine, SpeakerInfo info, double volume)
    {
        _engine = engine;
        Info = info;
        Volume = volume;
    }

    private void SetState(SpeakerState s, SpeakerProblem problem = SpeakerProblem.None, string text = null)
    {
        State = s;
        Problem = problem;
        ProblemText = text;
        try { StateChanged?.Invoke(this); } catch { }
    }

    // ------------------------------------------------------------------ startup

    public async Task<bool> StartAsync(CancellationToken ct)
    {
        SetState(SpeakerState.Connecting);
        try
        {
            await ConnectAsync(ct);
            await PairTransientAsync(ct);
            await SetupAsync(ct);
            NeedsStart = true;
            SetState(SpeakerState.Ready);
            Log.Info($"{Info.Name}: ready ({(UsesPtp ? "PTP" : "NTP")} timing)");
            _keepAlive = new Timer(_ => _ = KeepAliveAsync(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
            return true;
        }
        catch (SpeakerException ex)
        {
            Log.Warn($"{Info.Name}: {ex.Message}");
            await CleanupAsync();
            SetState(SpeakerState.Failed, ex.Problem, ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or TimeoutException)
        {
            Log.Warn($"{Info.Name}: couldn't connect: {ex.Message}");
            await CleanupAsync();
            SetState(SpeakerState.Failed, SpeakerProblem.Unreachable, "Couldn't reach the speaker. Check it's switched on and on the same network as this PC.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error($"{Info.Name}: setup failed: {ex}");
            await CleanupAsync();
            SetState(SpeakerState.Failed, SpeakerProblem.Rejected, "The speaker didn't accept the stream (" + ex.Message + ").");
            return false;
        }
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        Exception last = null;
        // Prefer IPv4: some speakers misbehave on IPv6
        foreach (var addr in Info.Addresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            try
            {
                _rtsp = await RtspClient.ConnectAsync(new IPEndPoint(addr, Info.Port), 5000, ct);
                Address = addr;
                break;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                last = ex;
            }
        }
        if (_rtsp == null) throw last ?? new SocketException((int)SocketError.HostUnreachable);

        _rtsp.DefaultHeaders["User-Agent"] = "AirPlay/665.13.1";
        _rtsp.DefaultHeaders["X-Apple-Client-Name"] = _engine.ClientName;
        _rtsp.DefaultHeaders["Client-Instance"] = _engine.DacpId;
        _rtsp.DefaultHeaders["DACP-ID"] = _engine.DacpId;
        _rtsp.DefaultHeaders["Active-Remote"] = _engine.ActiveRemote.ToString(CultureInfo.InvariantCulture);

        // /info tells us whether the speaker restricts access (plain text, before pairing)
        try
        {
            var info = await _rtsp.SendAsync("GET", "/info", ct: ct);
            if (info.Ok && info.Body.Length > 0 && BinaryPlist.Read(info.Body) is Dictionary<string, object> d)
            {
                Info.StatusFlags = (ulong)d.GetLong("statusFlags", (long)Info.StatusFlags);
                if (d.GetString("manufacturer") is string m && string.IsNullOrEmpty(Info.Manufacturer)) Info.Manufacturer = m;
                if (d.GetString("model") is string mo && string.IsNullOrEmpty(Info.Model)) Info.Model = mo;
            }
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            Log.Debug($"{Info.Name}: /info not usable ({ex.Message})");
        }
        if (Info.PasswordRequired)
            throw new SpeakerException(SpeakerProblem.PasswordRequired,
                "This speaker is protected by a password, which AirWave doesn't support yet. Turn the password off in the speaker's settings (Home app → Home Settings → Speakers & TV).");
    }

    private async Task PairTransientAsync(CancellationToken ct)
    {
        var hkp = new Dictionary<string, string> { ["X-Apple-HKP"] = "4" };

        // M1: start, transient flag
        var m1 = Tlv8.Encode((Tlv8.State, new byte[] { 1 }), (Tlv8.Method, new byte[] { 0 }), (Tlv8.Flags, new byte[] { 0x10 }));
        var r1 = await _rtsp.SendAsync("POST", "/pair-setup", m1, "application/octet-stream", hkp, ct: ct);
        if (r1.Status == 470 || r1.Status == 401 || r1.Status == 403)
            throw new SpeakerException(SpeakerProblem.AccessRestricted, AccessHelp);
        if (!r1.Ok) throw new SpeakerException(SpeakerProblem.Rejected, $"Pairing refused ({r1})");
        var t1 = Tlv8.Decode(r1.Body);
        if (t1.TryGetValue(Tlv8.Error, out var e1) && e1.Length > 0)
            throw new SpeakerException(SpeakerProblem.AccessRestricted, AccessHelp);
        if (!t1.TryGetValue(Tlv8.Salt, out var salt) || !t1.TryGetValue(Tlv8.PublicKey, out var pkB))
            throw new SpeakerException(SpeakerProblem.Rejected, "Pairing reply was missing data");

        // M3: our public key and proof
        var srp = new Srp6aClient("Pair-Setup", "3939");
        srp.ProcessChallenge(salt, pkB);
        var m3 = Tlv8.Encode((Tlv8.State, new byte[] { 3 }), (Tlv8.PublicKey, srp.A), (Tlv8.Proof, srp.M1));
        var r3 = await _rtsp.SendAsync("POST", "/pair-setup", m3, "application/octet-stream", hkp, ct: ct);
        if (!r3.Ok) throw new SpeakerException(SpeakerProblem.Rejected, $"Pairing step 2 refused ({r3})");
        var t3 = Tlv8.Decode(r3.Body);
        if (t3.TryGetValue(Tlv8.Error, out var e3) && e3.Length > 0)
            throw new SpeakerException(SpeakerProblem.AccessRestricted, AccessHelp);
        if (!t3.TryGetValue(Tlv8.Proof, out var m2) || !srp.VerifyServerProof(m2))
            throw new SpeakerException(SpeakerProblem.Rejected, "The speaker's pairing proof didn't check out");

        _sharedSecret = srp.SessionKey; // 64 bytes; first 32 are the audio key
        _rtsp.EnableEncryption(_sharedSecret);
        _audioCipher = new ChaCha20Poly1305(_sharedSecret.AsSpan(0, 32));
        Log.Debug($"{Info.Name}: paired (transient)");
    }

    private const string AccessHelp =
        "This speaker only lets certain people stream to it. In the Home app go to Home Settings → Speakers & TV → Allow Speaker & TV Access and choose \"Everyone on the same network\" (for Sonos, make sure AirPlay is turned on in the Sonos app).";

    private async Task SetupAsync(CancellationToken ct)
    {
        var local = _rtsp.Local.Address;
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        _sessionId = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4));
        _sessionUrl = local.AddressFamily == AddressFamily.InterNetworkV6
            ? $"rtsp://[{local}]/{_sessionId}" : $"rtsp://{local}/{_sessionId}";

        UsesPtp = Info.SupportsPtp && _engine.Ptp?.IsRunning == true && !_engine.ForceNtp;
        string sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();

        // SETUP (session): timing
        Dictionary<string, object> setup;
        if (UsesPtp)
        {
            var addrs = new List<object> { local.ToString() };
            var peerInfo = new Dictionary<string, object>
            {
                ["Addresses"] = addrs,
                ["ID"] = _engine.PtpClockUuid,
                ["DeviceType"] = 0L,
                ["ClockID"] = unchecked((long)_engine.Ptp.ClockId),
                ["SupportsClockPortMatchingOverride"] = false,
            };
            var peerInfoCopy = new Dictionary<string, object>(peerInfo) { ["Addresses"] = new List<object>(addrs) };
            setup = new Dictionary<string, object>
            {
                ["name"] = _engine.ClientName,
                ["deviceID"] = _engine.DeviceIdColon,
                ["sessionUUID"] = sessionUuid,
                ["timingProtocol"] = "PTP",
                ["macAddress"] = LocalMac(local),
                ["groupUUID"] = _engine.GroupUuid,
                ["groupContainsGroupLeader"] = false,
                ["timingPeerInfo"] = peerInfo,
                ["timingPeerList"] = new List<object> { peerInfoCopy },
            };
        }
        else
        {
            setup = new Dictionary<string, object>
            {
                ["deviceID"] = _engine.DeviceIdColon,
                ["sessionUUID"] = sessionUuid,
                ["timingPort"] = (long)_engine.Ntp.Port,
                ["timingProtocol"] = "NTP",
            };
        }

        var rs = await _rtsp.SendAsync("SETUP", _sessionUrl, BinaryPlist.Write(setup), "application/x-apple-binary-plist", ct: ct);
        if (!rs.Ok)
        {
            if (rs.Status == 401) throw new SpeakerException(SpeakerProblem.PasswordRequired, "This speaker asked for a password, which AirWave doesn't support yet.");
            throw new SpeakerException(SpeakerProblem.Rejected, $"Session setup refused ({rs})");
        }
        var sresp = rs.Body.Length > 0 ? BinaryPlist.Read(rs.Body) as Dictionary<string, object> : null;
        long eventPort = sresp.GetLong("eventPort");

        if (UsesPtp)
        {
            var peerAddr = Address;
            if (sresp != null && sresp.TryGetValue("timingPeerInfo", out var tpi) && tpi is Dictionary<string, object> tp
                && tp.TryGetValue("Addresses", out var al) && al is List<object> list)
            {
                foreach (var a in list.OfType<string>())
                    if (IPAddress.TryParse(a.Split('%')[0], out var ip) && ip.AddressFamily == Address.AddressFamily) { peerAddr = ip; break; }
            }
            _ptpPeerId = _engine.Ptp.AddPeer(peerAddr);
        }

        if (eventPort > 0)
        {
            try { _events = await EventsChannel.ConnectAsync(Info.Name, new IPEndPoint(Address, (int)eventPort), _sharedSecret, ct); }
            catch (Exception ex) { Log.Debug($"{Info.Name}: events channel not connected ({ex.Message})"); }
        }

        // RECORD
        var rr = await _rtsp.SendAsync("RECORD", _sessionUrl, ct: ct);
        if (!rr.Ok) throw new SpeakerException(SpeakerProblem.Rejected, $"Speaker refused to start ({rr})");

        // SETPEERS (PTP only): who takes part in timing
        if (UsesPtp)
        {
            var peers = new List<object> { Address.ToString(), local.ToString() };
            var rp = await _rtsp.SendAsync("SETPEERS", _sessionUrl, BinaryPlist.Write(peers), "/peer-list-changed", ct: ct);
            if (!rp.Ok) Log.Debug($"{Info.Name}: SETPEERS answered {rp}");
        }

        // SETUP (stream): realtime ALAC 44.1kHz/16/2, with this speaker's audio key
        var stream = new Dictionary<string, object>
        {
            ["audioFormat"] = 0x40000L,          // ALAC/44100/16/2
            ["audioMode"] = "default",
            ["controlPort"] = (long)_engine.ControlPort,
            ["ct"] = 2L,                          // compression: ALAC
            ["isMedia"] = true,
            ["latencyMax"] = 88200L,
            ["latencyMin"] = 11025L,
            ["shk"] = _sharedSecret.AsSpan(0, 32).ToArray(),
            ["spf"] = (long)SamplesPerPacket,
            ["sr"] = (long)SampleRate,
            ["type"] = 0x60L,                      // realtime audio
            ["supportsDynamicStreamID"] = false,
            ["streamConnectionID"] = (long)_sessionId,
        };
        var root = new Dictionary<string, object> { ["streams"] = new List<object> { stream } };
        var rst = await _rtsp.SendAsync("SETUP", _sessionUrl, BinaryPlist.Write(root), "application/x-apple-binary-plist", ct: ct);
        if (!rst.Ok) throw new SpeakerException(SpeakerProblem.Rejected, $"Audio setup refused ({rst})");
        var stResp = BinaryPlist.Read(rst.Body) as Dictionary<string, object>;
        var st = (stResp?["streams"] as List<object>)?.FirstOrDefault() as Dictionary<string, object>;
        int dataPort = (int)st.GetLong("dataPort");
        int controlPort = (int)st.GetLong("controlPort");
        if (dataPort == 0 || controlPort == 0) throw new SpeakerException(SpeakerProblem.Rejected, "Speaker didn't give us its audio ports");

        ControlEndPoint = new IPEndPoint(Address, controlPort);
        _data = new Socket(Address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        _data.Connect(new IPEndPoint(Address, dataPort));

        // Volume last: some speakers (e.g. Sonos) ignore it otherwise
        await SendVolumeAsync(Volume, ct);
        Log.Debug($"{Info.Name}: data port {dataPort}, control port {controlPort}, events {eventPort}");
    }

    private static string LocalMac(IPAddress local)
    {
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local)))
                {
                    var b = ni.GetPhysicalAddress().GetAddressBytes();
                    if (b.Length == 6) return string.Join(":", b.Select(x => x.ToString("x2")));
                }
            }
        }
        catch { }
        return "02:00:00:00:00:01";
    }

    // ------------------------------------------------------------------ audio

    /// <summary>Encrypts and sends one RTP audio packet. Header: 12 bytes; payload: ALAC.</summary>
    internal void SendAudio(ReadOnlySpan<byte> header12, ReadOnlySpan<byte> payload, ushort seq)
    {
        var sock = _data;
        var cipher = _audioCipher;
        if (sock == null || cipher == null) return;

        Span<byte> pkt = stackalloc byte[12 + payload.Length + 16 + 8];
        header12.CopyTo(pkt);
        Span<byte> nonce = stackalloc byte[12];
        nonce.Clear();
        nonce[4] = (byte)seq;          // little-endian sequence number, so a resend is byte-identical
        nonce[5] = (byte)(seq >> 8);
        cipher.Encrypt(nonce, payload, pkt.Slice(12, payload.Length), pkt.Slice(12 + payload.Length, 16), header12.Slice(4, 8));
        nonce.Slice(4, 8).CopyTo(pkt.Slice(12 + payload.Length + 16));
        try { sock.Send(pkt); }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionRefused or SocketError.ConnectionReset)
        {
            // ICMP port unreachable: the speaker dropped the session
            _ = DropAsync("The speaker stopped accepting audio.");
        }
        catch (ObjectDisposedException) { }
    }

    internal void MarkStreaming()
    {
        if (State == SpeakerState.Ready) SetState(SpeakerState.Streaming);
    }

    // ------------------------------------------------------------------ control

    public async Task SetVolumeAsync(double volume)
    {
        Volume = Math.Clamp(volume, 0, 1);
        if (State is SpeakerState.Ready or SpeakerState.Streaming)
        {
            try { await SendVolumeAsync(Volume, CancellationToken.None); }
            catch (Exception ex) { Log.Debug($"{Info.Name}: volume: {ex.Message}"); }
        }
    }

    private async Task SendVolumeAsync(double v, CancellationToken ct)
    {
        // AirPlay volume: -30..0 dB, -144 = mute
        double db = v <= 0.0001 ? -144.0 : -30.0 + 30.0 * v;
        var body = Encoding.ASCII.GetBytes($"volume: {db.ToString("0.000000", CultureInfo.InvariantCulture)}\r\n");
        await _cmdLock.WaitAsync(ct);
        try { await _rtsp.SendAsync("SET_PARAMETER", _sessionUrl, body, "text/parameters", ct: ct); }
        finally { _cmdLock.Release(); }
    }

    private async Task KeepAliveAsync()
    {
        if (State is not (SpeakerState.Ready or SpeakerState.Streaming) || _rtsp == null) return;
        if (!await _cmdLock.WaitAsync(0)) return;
        try
        {
            var r = await _rtsp.SendAsync("POST", "/feedback", timeoutMs: 5000);
            if (r.Status != 200) Log.Debug($"{Info.Name}: feedback answered {r}");
        }
        catch (Exception ex)
        {
            Log.Warn($"{Info.Name}: lost connection ({ex.Message})");
            _cmdLock.Release();
            await DropAsync("Lost the connection to the speaker.");
            return;
        }
        _cmdLock.Release();
    }

    private int _dropping;
    private async Task DropAsync(string why)
    {
        if (Interlocked.Exchange(ref _dropping, 1) == 1) return;
        await CleanupAsync();
        SetState(SpeakerState.Failed, SpeakerProblem.Dropped, why);
        _engine.OnSpeakerDropped(this);
    }

    public async Task StopAsync()
    {
        if (_rtsp != null && State is SpeakerState.Ready or SpeakerState.Streaming)
        {
            try
            {
                await _cmdLock.WaitAsync(2000);
                await _rtsp.SendAsync("TEARDOWN", _sessionUrl, BinaryPlist.Write(new Dictionary<string, object>()),
                    "application/x-apple-binary-plist", timeoutMs: 2000);
            }
            catch { }
        }
        await CleanupAsync();
        SetState(SpeakerState.Idle);
    }

    private Task CleanupAsync()
    {
        _keepAlive?.Dispose(); _keepAlive = null;
        if (_ptpPeerId >= 0) { _engine.Ptp?.RemovePeer(_ptpPeerId); _ptpPeerId = -1; }
        try { _data?.Dispose(); } catch { }
        _data = null;
        _events?.Dispose(); _events = null;
        _rtsp?.Dispose(); _rtsp = null;
        _audioCipher?.Dispose(); _audioCipher = null;
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}

public sealed class SpeakerException : Exception
{
    public SpeakerProblem Problem { get; }
    public SpeakerException(SpeakerProblem p, string msg) : base(msg) { Problem = p; }
}
