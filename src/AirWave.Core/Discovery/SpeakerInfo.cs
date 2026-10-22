using System.Globalization;
using System.Net;

namespace AirWave.Core.Discovery;

/// <summary>An AirPlay 2 receiver found on the network (or entered by hand).</summary>
public sealed class SpeakerInfo
{
    public string Id { get; init; }            // "AA:BB:CC:DD:EE:FF" device id
    public string Name { get; set; }
    public string Model { get; set; }          // e.g. AudioAccessory5,1
    public string Manufacturer { get; set; }
    public List<IPAddress> Addresses { get; } = new();
    public int Port { get; set; } = 7000;
    public ulong Features { get; set; }
    public ulong StatusFlags { get; set; }
    public Dictionary<string, string> Txt { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;

    public bool SupportsAudio => Bit(9);
    public bool SupportsPtp => Bit(41);
    public bool SupportsAirPlay2Pairing => Bit(48) || Bit(38) || Bit(46) || Bit(43);
    public bool SupportsBufferedAudio => Bit(40);
    public bool IsHomePod => Model?.StartsWith("AudioAccessory", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsAppleTv => Model?.StartsWith("AppleTV", StringComparison.OrdinalIgnoreCase) == true;
    public bool IsSonos => (Manufacturer ?? "").Contains("Sonos", StringComparison.OrdinalIgnoreCase)
                           || (Model ?? "").Contains("Sonos", StringComparison.OrdinalIgnoreCase);
    public bool PasswordRequired => (StatusFlags & (1 << 7)) != 0 || Txt.TryGetValue("pw", out var pw) && pw == "true";
    public bool PinRequired => (StatusFlags & (1 << 3)) != 0;

    private bool Bit(int n) => (Features & (1UL << n)) != 0;

    public IPAddress PreferredAddress =>
        Addresses.FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        ?? Addresses.FirstOrDefault();

    /// <summary>A friendly description of the kind of speaker, for the UI.</summary>
    public string Kind
    {
        get
        {
            if (IsHomePod) return "HomePod";
            if (IsAppleTv) return "Apple TV";
            if (IsSonos) return "Sonos";
            if (!string.IsNullOrEmpty(Manufacturer)) return Manufacturer;
            return "AirPlay speaker";
        }
    }

    /// <summary>Parses the AirPlay "features" TXT value: "0x5A7FFFF7,0x1E" (low,high) or a single hex number.</summary>
    public static ulong ParseFeatures(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var parts = s.Split(',');
        ulong lo = ParseHex(parts[0]);
        ulong hi = parts.Length > 1 ? ParseHex(parts[1]) : 0;
        return lo | (hi << 32);
    }

    private static ulong ParseHex(string p)
    {
        p = p.Trim();
        if (p.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) p = p[2..];
        return ulong.TryParse(p, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public static ulong ParseFlags(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        return ParseHex(s);
    }

    public override string ToString() => $"{Name} ({Kind}, {PreferredAddress}:{Port})";
}
