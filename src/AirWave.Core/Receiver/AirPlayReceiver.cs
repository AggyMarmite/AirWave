using System.Net;
using System.Net.Sockets;
using System.Text;
using AirWave.Core.Discovery;
using AirWave.Core.Timing;

namespace AirWave.Core.Receiver;

/// <summary>
/// Makes this PC an AirPlay 2 speaker: advertises itself, accepts connections from iPhones, iPads and Macs,
/// and plays what they send through <see cref="Player"/> in time with their clock.
/// Behaviour follows Shairport Sync, the widely used open-source AirPlay 2 receiver.
/// </summary>
public sealed class AirPlayReceiver : IAsyncDisposable
{
    // Same feature set Shairport Sync advertises (AirPlay 2 audio, buffered + realtime, PTP, transient pairing),
    // plus bit 50: now-playing info (title, artist, artwork, position) sent as plists in POST /command
    public const ulong Features = 0x0001C340401C4A00UL | (1UL << 50);
    public const uint StatusFlags = 0x4; // audio cable attached
    public const string SourceVersion = "366.0";
    public const string Model = "AirWave";

    private readonly ReceiverIdentity _id;
    private readonly CancellationTokenSource _cts = new();
    private readonly IServiceAdvertiser _advertiser;
    private TcpListener _listener;
    private readonly List<RtspConnection> _connections = new();
    private string _name;

    public ReceiverPlayer Player { get; }
    public PtpFollower Clock { get; }
    public int Port { get; private set; }
    public ReceiverIdentity Identity => _id;

    /// <summary>The device currently streaming to us (e.g. "Kieran's iPhone"), or null.</summary>
    public string ActiveClient { get; private set; }
    public event Action StateChanged;

    internal double Volume { get; set; } = -15;
    internal RtspConnection ActiveStream { get; private set; }

    public AirPlayReceiver(ReceiverIdentity id, PtpMaster ptpSockets, string name, IServiceAdvertiser advertiser = null)
    {
        _id = id;
        _name = name;
        Clock = new PtpFollower(ptpSockets);
        Player = new ReceiverPlayer(Clock);
        Player.SetAirPlayVolume(Volume);
        _advertiser = advertiser ?? ServiceAdvertiser.Create();
    }

    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; if (_listener != null) Advertise(); }
    }

    public void Start(int preferredPort = 7000)
    {
        for (int port = preferredPort; port < preferredPort + 20; port++)
        {
            try
            {
                _listener = Net.Listen(port);
                Port = port;
                break;
            }
            catch (SocketException) { }
        }
        if (_listener == null) throw new InvalidOperationException("No free port for the AirPlay receiver");
        _ = Task.Run(AcceptLoop);
        Advertise();
        Log.Info($"AirPlay receiver \"{_name}\" listening on port {Port}");
    }

    private void Advertise()
    {
        string fexBytes = Convert.ToBase64String(BitConverter.GetBytes(Features)).TrimEnd('=');
        string ft = $"0x{Features & 0xffffffff:X},0x{Features >> 32:X}";
        var raop = new List<(string, string)>
        {
            ("cn", "0,1"), ("da", "true"), ("et", "0,1"), ("ft", ft), ("fv", "p20." + SourceVersion), ("sf", $"0x{StatusFlags:X}"),
            ("md", "0,1,2"), ("am", Model), ("pk", _id.PublicKeyHex), ("tp", "UDP"), ("vn", "65537"), ("vs", SourceVersion),
        };
        var airplay = AirPlayTxt();
        _advertiser.Publish(new[]
        {
            new ServiceRecord($"{_id.DeviceIdCompact}@{_name}", "_raop._tcp", Port, raop),
            new ServiceRecord(_name, "_airplay._tcp", Port, airplay),
        });
    }

    internal List<(string, string)> AirPlayTxt() => new()
    {
        ("acl", "0"), ("deviceid", _id.DeviceId), ("fex", Convert.ToBase64String(BitConverter.GetBytes(Features)).TrimEnd('=')),
        ("features", $"0x{Features & 0xffffffff:X},0x{Features >> 32:X}"), ("flags", $"0x{StatusFlags:X}"),
        ("gid", _id.PairingIdentity), ("igl", "0"), ("gcgl", "0"), ("model", Model), ("protovers", "1.1"),
        ("pi", _id.PairingIdentity), ("psi", _id.PairingIdentity), ("pk", _id.PublicKeyHex), ("srcvers", SourceVersion),
    };

    /// <summary>TXT record bytes, as embedded in the /info reply.</summary>
    internal byte[] AirPlayTxtBytes()
    {
        var ms = new MemoryStream();
        foreach (var (k, v) in AirPlayTxt())
        {
            var b = Encoding.UTF8.GetBytes($"{k}={v}");
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        return ms.ToArray();
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            var conn = new RtspConnection(this, c);
            lock (_connections) _connections.Add(conn);
            _ = Task.Run(async () =>
            {
                try { await conn.RunAsync(_cts.Token); }
                catch (Exception ex) { Log.Debug($"Receiver connection ended: {ex.Message}"); }
                finally
                {
                    lock (_connections) _connections.Remove(conn);
                    conn.Dispose();
                    if (ReferenceEquals(ActiveStream, conn)) SetActive(null, null);
                }
            });
        }
    }

    /// <summary>A connection is starting to stream audio: it takes over from any other (like a speaker would).</summary>
    internal void SetActive(RtspConnection conn, string clientName)
    {
        RtspConnection previous;
        lock (_connections)
        {
            previous = ActiveStream;
            ActiveStream = conn;
            ActiveClient = conn == null ? null : (clientName ?? "an AirPlay device");
        }
        // Only a *different* connection starting a stream takes over. A stream ending (conn == null) leaves the
        // connection open: senders end one stream and start another on it, e.g. when a speaker joins the group.
        if (conn != null && previous != null && !ReferenceEquals(previous, conn))
        {
            Log.Info("AirPlay: another device took over");
            previous.Dispose();
        }
        if (conn == null) Player.Reset();
        if (conn != null) Log.Info($"AirPlay: {ActiveClient} is playing to this PC");
        StateChanged?.Invoke();
        NowPlayingChanged?.Invoke();
    }

    internal void RaiseStateChanged() => StateChanged?.Invoke();

    /// <summary>A connection's stream ended: clear it if it's the one playing (another may already have taken over).</summary>
    internal void StreamEnded(RtspConnection conn)
    {
        if (ReferenceEquals(ActiveStream, conn)) SetActive(null, null);
    }

    /// <summary>Title, artist, artwork etc. of what's playing to us now (null when nothing is).</summary>
    public NowPlaying NowPlaying => ActiveStream?.NowPlaying;
    /// <summary>True if the sender offered remote control (play/pause, next, previous).</summary>
    public bool HasRemote => ActiveStream?.Remote != null;
    public event Action NowPlayingChanged;

    internal void RaiseNowPlaying(RtspConnection conn)
    {
        if (ActiveStream == null || ReferenceEquals(ActiveStream, conn)) NowPlayingChanged?.Invoke();
    }

    /// <summary>Asks the device playing to us to play/pause, skip, etc. Returns false if it couldn't be reached.</summary>
    public Task<bool> SendRemoteAsync(RemoteCommand command)
    {
        var remote = ActiveStream?.Remote;
        return remote == null ? Task.FromResult(false) : remote.SendAsync(command);
    }

    /// <summary>Stops whatever is playing (the sender sees the speaker disconnect).</summary>
    public void Disconnect()
    {
        ActiveStream?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _advertiser.Withdraw(); _advertiser.Dispose(); } catch { }
        _listener?.Stop();
        List<RtspConnection> all;
        lock (_connections) all = _connections.ToList();
        foreach (var c in all) c.Dispose();
        await Task.CompletedTask;
    }
}
