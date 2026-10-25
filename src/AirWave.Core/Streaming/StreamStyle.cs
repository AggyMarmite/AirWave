namespace AirWave.Core.Streaming;

/// <summary>How audio is sent to a speaker.</summary>
public enum StreamStyle
{
    /// <summary>Realtime ALAC over UDP, sent the full delay ahead with sync packets (third-party speakers).</summary>
    RealtimeAhead,
    /// <summary>Realtime ALAC, each packet sent as it falls due, "this packet is now" sync packets.</summary>
    RealtimeJustInTime,
    /// <summary>Buffered AAC over TCP with an explicit play-at time (what iPhones send to HomePods).</summary>
    Buffered,
    /// <summary>As <see cref="Buffered"/>, but the play-at time is only sent once clocks have settled (3 s).</summary>
    BufferedLateAnchor,
}
