using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace AirWave.Core.Timing;

/// <summary>
/// A minimal PTP (IEEE 1588v2, unicast) grandmaster in the style AirPlay 2 senders use.
/// It always announces itself as a very good clock so speakers follow it, sends two-step
/// Sync/Follow_Up to every peer and answers Delay_Req. Its time is <see cref="MonoClock"/>.
/// Message layouts follow OwnTone's libairptp (MIT licence).
/// </summary>
public sealed class PtpMaster : IDisposable
{
    public const int EventPort = 319;
    public const int GeneralPort = 320;
    private const int StaleSeconds = 15;

    private readonly UdpClient _event;
    private readonly UdpClient _general;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, Peer> _peers = new();
    private int _nextPeerId = 1;
    private ushort _announceSeq, _signalingSeq, _syncSeq;

    public ulong ClockId { get; }

    /// <summary>
    /// Let the speakers keep time instead of us (as a Mac does with HomePods): announce ourselves as an ordinary clock
    /// that loses to theirs, and stop sending Sync. The speaker becomes the timekeeper; we follow it.
    /// </summary>
    public bool Yield { get; set; }

    /// <summary>PTP messages from other clocks (e.g. an iPhone that is streaming to us), with our receive time.</summary>
    public event Action<byte[], long, IPAddress> ForeignMessage;
    public bool IsRunning { get; }
    public string Error { get; }

    // Diagnostics: what each other device has sent our PTP ports
    private sealed class Seen { public int DelayReq, PdelayReq, Announce, Sync, FollowUp, Signaling, Other; public string LastAnnounce; }
    private readonly ConcurrentDictionary<IPAddress, Seen> _seen = new();

    /// <summary>A one-line summary of the PTP messages a device has sent us (for diagnostics).</summary>
    public string DescribePeer(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (!_seen.TryGetValue(a, out var s)) return "no PTP messages from it";
        return $"Delay_Req {s.DelayReq}, Pdelay_Req {s.PdelayReq}, Announce {s.Announce}{(s.LastAnnounce != null ? $" ({s.LastAnnounce})" : "")}, Sync {s.Sync}, Follow_Up {s.FollowUp}, Signaling {s.Signaling}, other {s.Other}";
    }

    public bool IsPeer(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        foreach (var p in _peers.Values) if (p.Address.Equals(a)) return true;
        return false;
    }

    private void Count(IPAddress from, byte[] msg)
    {
        var s = _seen.GetOrAdd(from, _ => new Seen());
        int type = msg[0] & 0x0f;
        int n = type switch
        {
            0x01 => ++s.DelayReq, 0x02 => ++s.PdelayReq, 0x0B => ++s.Announce, 0x00 => ++s.Sync, 0x08 => ++s.FollowUp, 0x0C => ++s.Signaling, _ => ++s.Other,
        };
        if (type == 0x0B && msg.Length >= 64)
            s.LastAnnounce = $"clock {BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(20)):X16}, grandmaster {BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(53)):X16}, priority {msg[47]}/{msg[52]}, class {msg[48]}";
        if (n == 1 && Log.MinLevel <= LogLevel.Debug)
            Log.Debug($"PTP: first message type 0x{type:X} from {from} (clock {BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(20)):X16}){(type == 0x0B ? ": " + s.LastAnnounce : "")}");
    }

    private sealed class Peer
    {
        public IPAddress Address;
        public DateTime LastSeen;
        public bool HelloSeen;
        public bool Yield;   // this peer keeps time; we only follow it
    }

    /// <summary>
    /// Behave like an Apple device that has just become the timekeeper: announce to peers, but only send them
    /// time (Sync/Follow_Up) once they've said hello (<see cref="MarkHello"/>). Used by tests.
    /// </summary>
    public bool SilentUntilHello { get; set; }

    public void MarkHello(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var p in _peers.Values) if (p.Address.Equals(address)) p.HelloSeen = true;
    }

    public void RemovePeer(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var kv in _peers) if (kv.Value.Address.Equals(address)) _peers.TryRemove(kv.Key, out _);
    }

    /// <param name="standardPorts">false = send from any free ports (lets a test sender run beside a receiver on one PC).</param>
    public PtpMaster(bool standardPorts = true)
    {
        Span<byte> id = stackalloc byte[8];
        RandomNumberGenerator.Fill(id);
        ClockId = BinaryPrimitives.ReadUInt64BigEndian(id) & 0x7fffffffffffffffUL;
        try
        {
            _event = UdpHelpers.CreateDualMode(standardPorts ? EventPort : 0);
            _general = UdpHelpers.CreateDualMode(standardPorts ? GeneralPort : 0);
            IsRunning = true;
        }
        catch (SocketException ex)
        {
            _event?.Dispose();
            Error = $"PTP ports 319/320 unavailable ({ex.SocketErrorCode}); speakers that need PTP timing will fall back to NTP";
            Log.Warn(Error);
            return;
        }
        _ = Task.Run(() => ReceiveLoop(_event));
        _ = Task.Run(() => ReceiveLoop(_general));
        _ = Task.Run(SendLoop);
    }

    /// <param name="yieldTo">Let this peer keep time (as a Mac does with a HomePod): we announce ourselves as a weaker
    /// clock to it and send it no time of our own; the engine follows its clock instead.</param>
    public int AddPeer(IPAddress address, bool yieldTo = false)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        foreach (var kv in _peers)
            if (kv.Value.Address.Equals(address)) { kv.Value.LastSeen = DateTime.UtcNow; kv.Value.Yield = yieldTo; return kv.Key; }
        int id = Interlocked.Increment(ref _nextPeerId);
        _peers[id] = new Peer { Address = address, LastSeen = DateTime.UtcNow, Yield = yieldTo };
        Log.Debug($"PTP: added peer {address}{(yieldTo ? " (it keeps time)" : "")}");
        // Get the new speaker locked on quickly instead of waiting for the next timer tick
        SendAnnounce();
        SendSync();
        return id;
    }

    public void RemovePeer(int id) => _peers.TryRemove(id, out _);

    /// <summary>Sends a message from our general (320) port to another clock.</summary>
    public void SendGeneral(IPAddress address, byte[] msg)
    {
        if (!IsRunning) return;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        SendTo(_general, address, GeneralPort, msg);
    }

    // ---------------------------------------------------------------- loops

    private async Task SendLoop()
    {
        long nextAnnounce = 0, nextSync = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!_cts.IsCancellationRequested)
        {
            long now = sw.ElapsedMilliseconds;
            if (now >= nextAnnounce)
            {
                SendAnnounce();
                SendSignaling();
                nextAnnounce = now + 1000;
            }
            if (now >= nextSync)
            {
                SendSync();
                nextSync = now + 125;
            }
            try { await Task.Delay(25, _cts.Token); } catch { break; }
        }
    }

    private async Task ReceiveLoop(UdpClient udp)
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(_cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            long rxNs = MonoClock.NowNs;

            var from = UdpHelpers.Normalize(r.RemoteEndPoint);
            foreach (var p in _peers.Values)
                if (p.Address.Equals(from.Address)) p.LastSeen = DateTime.UtcNow;

            var msg = r.Buffer;
            if (msg.Length < 34) continue;
            int type = msg[0] & 0x0f;
            if (BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(20)) != ClockId) Count(from.Address, msg);
            switch (type)
            {
                case 0x01: // Delay_Req -> Delay_Resp on the general port
                    SendTo(_general, from.Address, GeneralPort, MakeDelayResp(msg, rxNs));
                    break;
                case 0x02: // Pdelay_Req
                    SendTo(_event, from.Address, EventPort, MakePdelay(0x03, msg, rxNs));
                    SendTo(_general, from.Address, GeneralPort, MakePdelay(0x0A, msg, MonoClock.NowNs));
                    break;
                default:
                    // Announce/Sync/Follow_Up/Signaling from other clocks: we stay master for our own
                    // speakers, but the receiver may be following this clock.
                    if (BinaryPrimitives.ReadUInt64BigEndian(msg.AsSpan(20)) != ClockId)
                        ForeignMessage?.Invoke(msg, rxNs, from.Address);
                    break;
            }
        }
    }

    // ---------------------------------------------------------------- sending

    private bool Yields(Peer p) => Yield || p.Yield;

    /// <param name="yielding">null = every peer; true/false = only peers we yield to / keep time for.</param>
    private void SendAll(UdpClient udp, int port, byte[] msg, bool? yielding = null)
    {
        var now = DateTime.UtcNow;
        bool announce = (msg[0] & 0x0f) == 0x0B;
        foreach (var p in _peers.Values)
        {
            if ((now - p.LastSeen).TotalSeconds > StaleSeconds) continue;
            if (yielding is bool y && Yields(p) != y) continue;
            if (SilentUntilHello && !p.HelloSeen && !announce) continue;
            SendTo(udp, p.Address, port, msg);
        }
    }

    private static void SendTo(UdpClient udp, IPAddress addr, int port, byte[] msg)
    {
        try { udp.Client.SendTo(msg, UdpHelpers.ForSocket(udp.Client, new IPEndPoint(addr, port))); }
        catch (Exception ex) { Log.Debug($"PTP send to {addr}: {ex.Message}"); }
    }

    private void SendAnnounce()
    {
        if (!IsRunning) return;
        ushort seq = _announceSeq++;
        SendAll(_general, GeneralPort, MakeAnnounce(false, seq), yielding: false);
        SendAll(_general, GeneralPort, MakeAnnounce(true, seq), yielding: true);
    }

    private byte[] MakeAnnounce(bool yield, ushort seq)
    {
        var m = new byte[76];
        Header(m, 0x0B, seq, 0, 0x0408);
        // originTimestamp 0 (as iOS)
        // currentUtcOffset(2)=0, reserved(1)=0
        m[47] = yield ? (byte)250 : (byte)128; // priority1 (250 = a Mac's, which loses to a HomePod's 248)
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(48), yield ? 0xF8FE436Au : 0x06210000 | 0x436A); // clock quality
        m[52] = yield ? (byte)248 : (byte)128; // priority2
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(53), ClockId); // grandmasterIdentity
        // stepsRemoved(2)=0
        m[63] = yield ? (byte)0xA0 : (byte)0x20; // timeSource: internal oscillator / GPS
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(64), 0x0008); // path trace TLV
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(66), 8);
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(68), ClockId);
        return m;
    }

    private void SendSignaling()
    {
        if (!IsRunning) return;
        var m = new byte[106];
        Header(m, 0x0C, _signalingSeq++, -128, 0x0408);
        m[32] = 0x05; // controlField: other
        // targetPortIdentity (10 bytes) zero
        int p = 44;
        WriteOrgTlv(m, ref p, 22, new byte[] { 0x00, 0x0d, 0x93 }, new byte[] { 0, 0, 1 }, new byte[] { 0, 0, 3, 1 });
        WriteOrgTlv(m, ref p, 32, new byte[] { 0x00, 0x0d, 0x93 }, new byte[] { 0, 0, 5 }, new byte[] { 0, 0, 3, 1 });
        SendAll(_general, GeneralPort, m);
    }

    private void SendSync()
    {
        if (!IsRunning) return;
        ushort seq = _syncSeq++;
        var sync = new byte[44];
        Header(sync, 0x00, seq, -3, 0x0608);
        long ts = MonoClock.NowNs;
        WriteTimestamp(sync.AsSpan(34), ts); // approximate (two-step: Follow_Up has the precise one), as OwnTone does
        SendAll(_event, EventPort, sync, yielding: false);

        var fu = new byte[96];
        Header(fu, 0x08, seq, -3, 0x0408);
        WriteTimestamp(fu.AsSpan(34), ts);
        int p = 44;
        WriteOrgTlv(fu, ref p, 28, new byte[] { 0x00, 0x80, 0xc2 }, new byte[] { 0, 0, 1 }, Array.Empty<byte>());
        var cid = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(cid, ClockId);
        WriteOrgTlv(fu, ref p, 16, new byte[] { 0x00, 0x0d, 0x93 }, new byte[] { 0, 0, 4 }, cid);
        SendAll(_general, GeneralPort, fu, yielding: false);
    }

    private byte[] MakeDelayResp(byte[] req, long rxNs)
    {
        var m = new byte[54];
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(30));
        Header(m, 0x09, seq, -3, 0x0608);
        WriteTimestamp(m.AsSpan(34), rxNs);
        Buffer.BlockCopy(req, 20, m, 44, 10); // requestingPortIdentity = request's sourcePortIdentity
        return m;
    }

    private byte[] MakePdelay(byte type, byte[] req, long ns)
    {
        var m = new byte[54];
        ushort seq = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(30));
        Header(m, type, seq, -3, type == 0x03 ? (ushort)0x0608 : (ushort)0x0408);
        WriteTimestamp(m.AsSpan(34), ns);
        Buffer.BlockCopy(req, 20, m, 44, 10);
        return m;
    }

    private void Header(byte[] m, byte type, ushort seq, sbyte logInterval, ushort flags)
    {
        m[0] = (byte)(type | 0x10); // transportSpecific = 1 (expected by some receivers)
        m[1] = 0x02;                // PTPv2
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), (ushort)m.Length);
        m[4] = 0;                   // domain
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), flags);
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(20), ClockId);
        m[28] = 0x80; m[29] = 0x05; // port number, same as iOS
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(30), seq);
        m[32] = 0x00;
        m[33] = (byte)logInterval;
    }

    private static void WriteTimestamp(Span<byte> dst, long ns)
    {
        long sec = ns / 1_000_000_000L;
        BinaryPrimitives.WriteUInt16BigEndian(dst, (ushort)(sec >> 32));
        BinaryPrimitives.WriteUInt32BigEndian(dst[2..], (uint)sec);
        BinaryPrimitives.WriteUInt32BigEndian(dst[6..], (uint)(ns % 1_000_000_000L));
    }

    private static void WriteOrgTlv(byte[] m, ref int p, int len, byte[] org, byte[] subtype, byte[] data)
    {
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(p), 0x0003);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(p + 2), (ushort)len);
        Buffer.BlockCopy(org, 0, m, p + 4, 3);
        Buffer.BlockCopy(subtype, 0, m, p + 7, 3);
        Buffer.BlockCopy(data, 0, m, p + 10, data.Length);
        p += 4 + len;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _event?.Dispose();
        _general?.Dispose();
    }
}
