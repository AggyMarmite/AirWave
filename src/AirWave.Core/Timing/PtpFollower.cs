using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;

namespace AirWave.Core.Timing;

/// <summary>
/// Follows other PTP clocks (the iPhone or Mac streaming to us) so their "network time" can be turned
/// into our <see cref="MonoClock"/> time. Works like NQPTP, the clock service behind Shairport Sync:
/// the offset comes from Follow_Up messages and is smoothed to track the least-delayed samples.
/// </summary>
public sealed class PtpFollower
{
    private sealed class Clock
    {
        public ulong SourceId;
        public ulong Grandmaster;
        public IPAddress Address;
        public long Offset;            // master ns - local ns (smoothed)
        public long LastRx;            // local ns of the last sample
        public long MastershipStart;
        public bool Valid;
        public byte Priority1 = 248, Priority2 = 248;
        public int AnnouncesWithoutFollowUp;
        public long FollowUps;
        public bool WokenOnce, WokenSilent;
    }

    private readonly ConcurrentDictionary<ulong, Clock> _clocks = new(); // keyed by source clock id

    private readonly PtpMaster _sockets;

    private readonly bool _wake;

    /// <param name="wake">Say hello to new clocks (the receiver does; a passive observer doesn't).</param>
    public PtpFollower(PtpMaster sockets, bool wake = true)
    {
        _sockets = sockets;
        _wake = wake;
        if (sockets != null) sockets.ForeignMessage += OnMessage;
    }

    /// <summary>Raised when we nudge a clock to start sending us its time (for tests and logs).</summary>
    public event Action<ulong, IPAddress> WokeClock;

    /// <summary>For tests/other transports: feed a raw PTP message received at local time rxNs.</summary>
    public void OnMessage(byte[] m, long rxNs, IPAddress from)
    {
        if (m.Length < 44) return;
        int type = m[0] & 0x0f;
        ulong source = BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(20));
        var c = _clocks.GetOrAdd(source, id => new Clock { SourceId = id, Grandmaster = id });
        c.Address = from;

        if (type == 0x0B && m.Length >= 64) // Announce: who is the grandmaster behind this source
        {
            c.Grandmaster = BinaryPrimitives.ReadUInt64BigEndian(m.AsSpan(53));
            c.Priority1 = m[47];
            c.Priority2 = m[52];
            // Apple devices only send their time to clocks they've heard from. When a HomePod or Apple TV joins
            // the group it can become the timekeeper, and it stays silent towards us unless we say hello first
            // (as NQPTP does): once when we first see a clock, and again if it keeps announcing without sending time.
            if (!c.WokenOnce) { c.WokenOnce = true; Wake(c, from); }
            else if (c.FollowUps == 0 && ++c.AnnouncesWithoutFollowUp == 3 && !c.WokenSilent) { c.WokenSilent = true; Wake(c, from); }
            return;
        }
        if (type != 0x08) return; // only Follow_Up carries the precise time
        c.FollowUps++;
        c.AnnouncesWithoutFollowUp = 0;

        long sec = ((long)BinaryPrimitives.ReadUInt16BigEndian(m.AsSpan(34)) << 32) | BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(36));
        long ns = BinaryPrimitives.ReadUInt32BigEndian(m.AsSpan(40));
        long correction = BinaryPrimitives.ReadInt64BigEndian(m.AsSpan(8)) / 65536;
        long origin = sec * 1_000_000_000L + ns + correction;
        long offset = origin - rxNs;

        lock (c)
        {
            if (!c.Valid || rxNs - c.LastRx > 5_000_000_000L)
            {
                // First sample, or a gap: start over
                c.Offset = offset;
                c.MastershipStart = rxNs;
                c.Valid = true;
            }
            else
            {
                long jitter = offset - c.Offset;
                bool early = rxNs - c.MastershipStart < 1_000_000_000L;
                if (jitter < 0)
                {
                    // A later-arriving (more delayed) sample: move down only very slowly
                    if (!early) c.Offset += Math.Max(jitter, -2_500_000) / 256;
                }
                else c.Offset += early ? jitter : jitter / 16;
            }
            c.LastRx = rxNs;
        }
    }

    /// <summary>
    /// NQPTP's "awakening" sequence: an Announce that looks slightly better than the clock, then one slightly worse,
    /// from our PTP port. The clock re-runs its choice of master, stays master, and starts sending us Sync/Follow_Up.
    /// </summary>
    private void Wake(Clock c, IPAddress to)
    {
        if (_sockets == null || !_sockets.IsRunning || !_wake) return;
        // Never to a speaker we're sending to: there we are the timekeeper, and mixed messages would confuse it
        if (_sockets.IsPeer(to)) return;
        byte p1 = c.Priority1, p2 = c.Priority2;
        var better = Announce(_sockets.ClockId, (byte)(p1 > 2 ? p1 - 1 : 248), p1 > 2 ? p2 : (byte)248);
        var worse = Announce(_sockets.ClockId, (byte)(p1 < 254 ? p1 + 1 : 250), p2);
        Log.Debug($"PTP: saying hello to clock {c.SourceId:X16} at {to}");
        _sockets.SendGeneral(to, better);
        _ = Task.Delay(150).ContinueWith(_ => _sockets.SendGeneral(to, worse));
        try { WokeClock?.Invoke(c.SourceId, to); } catch { }
    }

    private ushort _announceSeq;

    private byte[] Announce(ulong self, byte priority1, byte priority2)
    {
        var m = new byte[64];
        m[0] = 0x10 | 0x0B;                                           // transportSpecific 1, Announce
        m[1] = 0x02;                                                  // PTP v2
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(2), 64);
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(6), 0x0408);   // flags
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(20), self);    // clockIdentity
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(28), 32776);   // sourcePortID
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(30), _announceSeq++);
        m[32] = 0x05;                                                 // controlField
        m[33] = 0xFE;                                                 // logMessagePeriod
        BinaryPrimitives.WriteUInt16BigEndian(m.AsSpan(44), 37);      // currentUtcOffset
        m[47] = priority1;
        BinaryPrimitives.WriteUInt32BigEndian(m.AsSpan(48), 0xf8fe436a); // clock quality: an ordinary, free-running clock
        m[52] = priority2;
        BinaryPrimitives.WriteUInt64BigEndian(m.AsSpan(53), self);    // grandmasterIdentity
        m[63] = 160;                                                  // timeSource: internal oscillator
        return m;
    }

    /// <summary>
    /// Offset (remote ns - local ns) for the given timeline (grandmaster or source clock id), or null if
    /// we haven't been following it for long enough. timelineId 0 = whichever clock was heard most recently.
    /// </summary>
    public long? GetOffset(ulong timelineId, out long followedForNs)
    {
        followedForNs = 0;
        Clock best = null;
        foreach (var c in _clocks.Values)
        {
            if (!c.Valid) continue;
            if (timelineId != 0 && c.Grandmaster != timelineId && c.SourceId != timelineId) continue;
            if (best == null || c.LastRx > best.LastRx) best = c;
        }
        if (best == null) return null;
        long now = MonoClock.NowNs;
        if (now - best.LastRx > 10_000_000_000L) return null; // gone quiet
        followedForNs = now - best.MastershipStart;
        lock (best) return best.Offset;
    }

    public long? GetOffset(ulong timelineId) => GetOffset(timelineId, out _);
}
