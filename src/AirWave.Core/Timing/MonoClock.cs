using System.Diagnostics;

namespace AirWave.Core.Timing;

/// <summary>
/// The single clock everything in AirWave is timed against: audio scheduling, RTP sync packets,
/// the NTP timing replies and the PTP master clock all read this, so every speaker in a group
/// ends up following exactly the same timeline.
/// </summary>
public static class MonoClock
{
    private static readonly long Start = Stopwatch.GetTimestamp();
    private static readonly double TicksToNs = 1_000_000_000.0 / Stopwatch.Frequency;

    // Start somewhere well above zero so the clock never looks "unset" to a receiver.
    private const long BaseNs = 1_000_000_000L * 1000;

    /// <summary>Nanoseconds on the shared monotonic timeline.</summary>
    public static long NowNs => BaseNs + (long)((Stopwatch.GetTimestamp() - Start) * TicksToNs);

    public const uint NtpEpochDelta = 0x83aa7e80; // 1900 -> 1970

    /// <summary>Converts a timeline value to a 64-bit NTP timestamp (seconds since 1900 . fraction).</summary>
    public static ulong ToNtp(long ns)
    {
        ulong sec = (ulong)(ns / 1_000_000_000L) + NtpEpochDelta;
        ulong frac = (ulong)((ns % 1_000_000_000L) * 4294967296.0 / 1e9);
        return (sec << 32) | (frac & 0xffffffffUL);
    }
}
