using System.Buffers.Binary;
using System.Text;

namespace AirWave.Core.Receiver;

/// <summary>What the iPhone/Mac says is playing: title, artist, album, artwork and position.</summary>
public sealed class NowPlaying
{
    public string Title { get; private set; }
    public string Artist { get; private set; }
    public string Album { get; private set; }
    /// <summary>JPEG/PNG bytes, or null.</summary>
    public byte[] Artwork { get; private set; }
    /// <summary>Track length in seconds (0 = unknown, e.g. live radio).</summary>
    public double Duration { get; private set; }
    /// <summary>Playback speed the sender reported (1 = playing, 0 = paused).</summary>
    public double Rate { get; private set; } = 1;

    private double _elapsedAtStamp;
    private DateTime _stampUtc = DateTime.UtcNow;

    /// <summary>Changes every time anything is updated (so the UI knows when to refresh the artwork).</summary>
    public int Version { get; private set; }

    public bool HasInfo => Title != null || Artist != null || Album != null || Artwork != null;

    /// <summary>Position in the track, in seconds, as of now.</summary>
    public double Elapsed
    {
        get
        {
            double e = _elapsedAtStamp + (Rate > 0 ? (DateTime.UtcNow - _stampUtc).TotalSeconds * Rate : 0);
            return Duration > 0 ? Math.Clamp(e, 0, Duration) : Math.Max(0, e);
        }
    }

    internal void SetPlaying(bool playing)
    {
        // Freeze/unfreeze the position when the stream itself pauses or resumes
        double e = Elapsed;
        _elapsedAtStamp = e;
        _stampUtc = DateTime.UtcNow;
        Rate = playing ? (Rate > 0 ? Rate : 1) : 0;
        Version++;
    }

    /// <summary>
    /// AirPlay 2 "POST /command" with type updateMRNowPlayingInfo (feature bit 50), as Shairport Sync handles it.
    /// Returns true if anything changed.
    /// </summary>
    public bool ApplyCommand(Dictionary<string, object> cmd)
    {
        if (cmd == null || cmd.GetValueOrDefault("type") as string != "updateMRNowPlayingInfo") return false;
        if (cmd.GetValueOrDefault("params") is not Dictionary<string, object> p) return false;
        if (p.GetValueOrDefault("type") as string != "npi-text" && p.ContainsKey("type")) return false;
        bool replace = p.GetValueOrDefault("mergePolicy") as string == "replace";
        if (p.GetValueOrDefault("params") is not Dictionary<string, object> npi) return false;

        if (replace)
        {
            Title = Artist = Album = null;
            Artwork = null;
            Duration = 0;
        }
        static string S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) && v is string s && s.Length > 0 ? s : null;
        static double? D(Dictionary<string, object> d, string k) => d.TryGetValue(k, out var v) ? v switch { double x => x, long l => l, ulong u => u, _ => null } : null;

        if (npi.ContainsKey("kMRMediaRemoteNowPlayingInfoTitle")) Title = S(npi, "kMRMediaRemoteNowPlayingInfoTitle");
        if (npi.ContainsKey("kMRMediaRemoteNowPlayingInfoArtist")) Artist = S(npi, "kMRMediaRemoteNowPlayingInfoArtist");
        if (npi.ContainsKey("kMRMediaRemoteNowPlayingInfoAlbum")) Album = S(npi, "kMRMediaRemoteNowPlayingInfoAlbum");
        if (npi.TryGetValue("kMRMediaRemoteNowPlayingInfoArtworkData", out var art)) Artwork = art is byte[] b && b.Length > 0 ? b : null;
        if (D(npi, "kMRMediaRemoteNowPlayingInfoDuration") is double dur) Duration = Math.Max(0, dur);
        if (npi.ContainsKey("kMRMediaRemoteNowPlayingInfoPlaybackRate") || replace)
            Rate = D(npi, "kMRMediaRemoteNowPlayingInfoPlaybackRate") ?? (replace ? 0 : Rate);
        if (D(npi, "kMRMediaRemoteNowPlayingInfoElapsedTime") is double el)
        {
            _elapsedAtStamp = Math.Max(0, el);
            // The elapsed time is as of the timestamp (sender's clock, close enough to ours over NTP)
            _stampUtc = npi.GetValueOrDefault("kMRMediaRemoteNowPlayingInfoTimestamp") is DateTime ts && Math.Abs((DateTime.UtcNow - ts).TotalSeconds) < 30
                ? ts.ToUniversalTime() : DateTime.UtcNow;
        }
        Version++;
        return true;
    }

    /// <summary>Classic AirPlay metadata (SET_PARAMETER application/x-dmap-tagged).</summary>
    public bool ApplyDmap(ReadOnlySpan<byte> d)
    {
        // Usually wrapped in an 'mlit' container
        if (d.Length >= 8 && Encoding.ASCII.GetString(d[..4]) == "mlit") d = d.Slice(8, Math.Min((int)BinaryPrimitives.ReadUInt32BigEndian(d[4..]), d.Length - 8));
        bool any = false;
        while (d.Length >= 8)
        {
            string tag = Encoding.ASCII.GetString(d[..4]);
            int len = (int)BinaryPrimitives.ReadUInt32BigEndian(d[4..]);
            if (len < 0 || 8 + len > d.Length) break;
            var v = d.Slice(8, len);
            switch (tag)
            {
                case "minm": Title = Str(v); any = true; break;
                case "asar": Artist = Str(v); any = true; break;
                case "asal": Album = Str(v); any = true; break;
            }
            d = d[(8 + len)..];
        }
        if (any) Version++;
        return any;
        static string Str(ReadOnlySpan<byte> v) => v.Length == 0 ? null : Encoding.UTF8.GetString(v);
    }

    /// <summary>Classic AirPlay artwork (SET_PARAMETER image/*).</summary>
    public void ApplyArtwork(byte[] image) { Artwork = image is { Length: > 0 } ? image : null; Version++; }

    /// <summary>Classic AirPlay "progress: start/current/end" (RTP times at 44.1 kHz).</summary>
    public bool ApplyProgress(string value)
    {
        var parts = value.Split('/');
        if (parts.Length != 3 || !uint.TryParse(parts[0], out var start) || !uint.TryParse(parts[1], out var cur) || !uint.TryParse(parts[2], out var end)) return false;
        Duration = (uint)(end - start) / 44100.0;
        _elapsedAtStamp = (int)(cur - start) / 44100.0;
        _stampUtc = DateTime.UtcNow;
        Version++;
        return true;
    }
}
