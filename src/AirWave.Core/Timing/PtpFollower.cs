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
    }

    private readonly ConcurrentDictionary<ulong, Clock> _clocks = new(); // keyed by source clock id

    public PtpFollower(PtpMaster sockets)
    {
        if (sockets != null) sockets.ForeignMessage += OnMessage;
    }

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
            return;
        }
        if (type != 0x08) return; // only Follow_Up carries the precise time

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
