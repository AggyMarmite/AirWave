namespace AirWave.Core.Mirroring;

/// <summary>
/// Maps our clock onto the TV's clock (the timeline video timestamps are on), without running PTP.
/// Every RTSP reply carries X-Apple-RequestReceivedTimestamp + X-Apple-ProcessingTime (ms on the TV's
/// clock); each is a lower bound on the true offset, so we follow the maximum of recent samples:
/// up at once, down only slowly. Approach and constants from AirPlayDroid (GPLv3), verified on Apple TV.
/// </summary>
public sealed class ReceiverClock
{
    private readonly Queue<long> _window = new();
    private long _offsetNs;
    private const int Window = 8;
    private const long MaxSlewNs = 2_000_000;

    public ReceiverClock(long receiverMs, long localNs) { _offsetNs = receiverMs * 1_000_000 - localNs; }

    public long ReceiverNs(long localNs) => localNs + Interlocked.Read(ref _offsetNs);

    public void Observe(long receiverMs, long localNs)
    {
        lock (_window)
        {
            _window.Enqueue(receiverMs * 1_000_000 - localNs);
            while (_window.Count > Window) _window.Dequeue();
            long target = _window.Max();
            long off = _offsetNs;
            off += target > off ? target - off : Math.Max(target - off, -MaxSlewNs);
            Interlocked.Exchange(ref _offsetNs, off);
        }
    }

    public static long? ReceiverMsOf(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("X-Apple-RequestReceivedTimestamp", out var r) || !long.TryParse(r, out long received)) return null;
        long processing = headers.TryGetValue("X-Apple-ProcessingTime", out var p) && long.TryParse(p, out var pp) ? pp : 0;
        return received + processing;
    }

    /// <summary>Nanoseconds as the 32.32 fixed-point seconds video headers carry.</summary>
    public static long ToFixed32(long ns)
    {
        long sec = ns / 1_000_000_000L;
        long frac = ((ns % 1_000_000_000L) << 32) / 1_000_000_000L;
        return (sec << 32) | frac;
    }
}
