using System.Security.Cryptography;
using System.Text.Json;
using AirWave.Core.Crypto;

namespace AirWave.Core.Receiver;

/// <summary>
/// Who this PC is as an AirPlay receiver: a stable device id and signing key, so iPhones see the same
/// device every time. Stored next to the settings.
/// </summary>
public sealed class ReceiverIdentity
{
    public string DeviceId { get; set; }      // AA:BB:CC:DD:EE:FF
    public string KeySeed { get; set; }       // Ed25519 seed, base64
    public string PairingIdentity { get; set; } // "pi" UUID

    public byte[] Seed => Convert.FromBase64String(KeySeed);
    public byte[] PublicKey => Curve25519.Ed25519Public(Seed);
    public string PublicKeyHex => Convert.ToHexString(PublicKey).ToLowerInvariant();
    public string DeviceIdCompact => DeviceId.Replace(":", "");

    public static ReceiverIdentity LoadOrCreate(string folder)
    {
        string path = Path.Combine(folder, "receiver.json");
        try
        {
            if (File.Exists(path))
            {
                var id = JsonSerializer.Deserialize<ReceiverIdentity>(File.ReadAllText(path));
                if (id?.DeviceId != null && id.KeySeed != null && id.PairingIdentity != null) return id;
            }
        }
        catch (Exception ex) { Log.Warn($"Receiver identity unreadable, making a new one: {ex.Message}"); }

        var mac = RandomNumberGenerator.GetBytes(6);
        mac[0] = (byte)((mac[0] & 0xFE) | 0x02); // locally administered, unicast
        var created = new ReceiverIdentity
        {
            DeviceId = string.Join(":", mac.Select(b => b.ToString("X2"))),
            KeySeed = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            PairingIdentity = Guid.NewGuid().ToString(),
        };
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, JsonSerializer.Serialize(created));
        }
        catch (Exception ex) { Log.Warn($"Couldn't save receiver identity: {ex.Message}"); }
        return created;
    }
}
