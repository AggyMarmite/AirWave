using System.Text.Json;

namespace AirWave.Core.Mirroring;

/// <summary>A lasting pairing with one Apple TV (or other screen receiver), made once with its on-screen PIN or password.</summary>
public sealed class MirrorCredentials
{
    public string ClientId { get; set; }       // our id as the TV knows us
    public string ClientSeed { get; set; }     // our Ed25519 seed (base64)
    public string ReceiverId { get; set; }     // the TV's pairing id
    public string ReceiverKey { get; set; }    // the TV's Ed25519 public key (base64)
    public string Password { get; set; }       // the TV's AirPlay password if it has one, protected for this Windows user
}

/// <summary>Protects small secrets for the current Windows user (DPAPI); plain base64 elsewhere (tests).</summary>
public static class UserSecret
{
    public static string Protect(string plain)
    {
        if (plain == null) return null;
        var data = System.Text.Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows() && Dpapi.Protect(data) is byte[] p) return "dpapi:" + Convert.ToBase64String(p);
        return "plain:" + Convert.ToBase64String(data);
    }

    public static string Unprotect(string stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            if (stored.StartsWith("dpapi:") && OperatingSystem.IsWindows())
                return Dpapi.Unprotect(Convert.FromBase64String(stored[6..])) is byte[] d ? System.Text.Encoding.UTF8.GetString(d) : null;
            if (stored.StartsWith("plain:")) return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        }
        catch { }
        return null;
    }

    private static class Dpapi
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct Blob { public int Size; public IntPtr Data; }

        [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool CryptProtectData(ref Blob input, string desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref Blob input, IntPtr desc, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr p);

        public static byte[] Protect(byte[] d) => Run(d, true);
        public static byte[] Unprotect(byte[] d) => Run(d, false);

        private static byte[] Run(byte[] data, bool protect)
        {
            var h = System.Runtime.InteropServices.GCHandle.Alloc(data, System.Runtime.InteropServices.GCHandleType.Pinned);
            try
            {
                var input = new Blob { Size = data.Length, Data = h.AddrOfPinnedObject() };
                bool ok = protect ? CryptProtectData(ref input, "AirWave", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out var o)
                                  : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out o);
                if (!ok) return null;
                var r = new byte[o.Size];
                System.Runtime.InteropServices.Marshal.Copy(o.Data, r, 0, o.Size);
                LocalFree(o.Data);
                return r;
            }
            finally { h.Free(); }
        }
    }
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
