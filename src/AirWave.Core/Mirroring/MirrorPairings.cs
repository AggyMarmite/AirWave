using System.Text.Json;

namespace AirWave.Core.Mirroring;

/// <summary>A lasting pairing with one Apple TV (or other screen receiver), made once with its on-screen PIN or password.</summary>
public sealed class MirrorCredentials
{
    public string ClientId { get; set; }       // our id as the TV knows us
    public string ClientSeed { get; set; }     // our Ed25519 seed (base64)
    public string ReceiverId { get; set; }     // the TV's pairing id
    public string ReceiverKey { get; set; }    // the TV's Ed25519 public key (base64)
}

/// <summary>Saved pairings, keyed by the receiver's device id. Stored next to the settings.</summary>
public static class MirrorPairings
{
    private static readonly object Lock = new();
    private static string PathFor(string folder) => Path.Combine(folder, "mirror-pairings.json");

    public static MirrorCredentials Get(string folder, string deviceId)
    {
        lock (Lock) return Load(folder).TryGetValue(deviceId, out var c) ? c : null;
    }

    public static void Save(string folder, string deviceId, MirrorCredentials creds)
    {
        lock (Lock)
        {
            var all = Load(folder);
            if (creds == null) all.Remove(deviceId); else all[deviceId] = creds;
            try
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(PathFor(folder), JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Log.Warn($"Couldn't save the TV pairing: {ex.Message}"); }
        }
    }

    private static Dictionary<string, MirrorCredentials> Load(string folder)
    {
        try
        {
            if (File.Exists(PathFor(folder)))
                return JsonSerializer.Deserialize<Dictionary<string, MirrorCredentials>>(File.ReadAllText(PathFor(folder))) ?? new();
        }
        catch { }
        return new();
    }
}
