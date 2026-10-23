using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace AirWave.Core.Updates;

public sealed class UpdateInfo
{
    public Version Version { get; init; }
    public string Tag { get; init; }
    public string Notes { get; init; }
    public string PageUrl { get; init; }
    public UpdateAsset Installer { get; init; }
    public UpdateAsset Portable { get; init; }
}

public sealed record UpdateAsset(string Name, string Url, long Size, string Sha256);

/// <summary>
/// In-app updates from the GitHub releases: checks for a newer version, downloads the right file (the installer
/// for an installed copy, the portable .exe for a portable one), checks it, and swaps it in after AirWave closes.
/// </summary>
public static class Updater
{
    public const string Repo = "AggyMarmite/AirWave";
    public static string ApiUrl = $"https://api.github.com/repos/{Repo}/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("AirWave-updater");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    /// <summary>The latest release, or null if there's nothing newer than <paramref name="current"/>.</summary>
    public static async Task<UpdateInfo> CheckAsync(Version current, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(ApiUrl, ct);
        var info = Parse(json);
        return info != null && info.Version > current ? info : null;
    }

    /// <summary>Reads a GitHub "latest release" response.</summary>
    public static UpdateInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
        if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
        string tag = r.GetProperty("tag_name").GetString() ?? "";
        if (!TryVersion(tag, out var version)) return null;
        UpdateAsset installer = null, portable = null;
        if (r.TryGetProperty("assets", out var assets))
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.GetProperty("name").GetString() ?? "";
                string digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
                var asset = new UpdateAsset(name, a.GetProperty("browser_download_url").GetString(), a.GetProperty("size").GetInt64(),
                    digest != null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..].ToLowerInvariant() : null);
                if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (name.StartsWith("AirWave-Setup", StringComparison.OrdinalIgnoreCase)) installer = asset;
                else if (name.Contains("portable", StringComparison.OrdinalIgnoreCase)) portable = asset;
            }
        }
        return new UpdateInfo
        {
            Version = version, Tag = tag,
            Notes = r.TryGetProperty("body", out var b) ? b.GetString()?.Trim() : null,
            PageUrl = r.TryGetProperty("html_url", out var h) ? h.GetString() : $"https://github.com/{Repo}/releases",
            Installer = installer, Portable = portable,
        };
    }

    public static bool TryVersion(string s, out Version v)
    {
        s = (s ?? "").Trim().TrimStart('v', 'V');
        int dash = s.IndexOfAny(new[] { '-', '+' });
        if (dash >= 0) s = s[..dash];
        if (Version.TryParse(s, out var parsed))
        {
            v = new Version(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build));
            return true;
        }
        v = null;
        return false;
    }

    /// <summary>True when this copy was put there by the installer (rather than being the portable .exe).</summary>
    public static bool IsInstalled(string exePath) =>
        File.Exists(Path.Combine(Path.GetDirectoryName(exePath) ?? "", "unins000.exe"));

    public static UpdateAsset AssetFor(UpdateInfo info, string exePath) =>
        IsInstalled(exePath) ? info.Installer ?? info.Portable : info.Portable ?? info.Installer;

    /// <summary>Downloads and checks an update file; returns its path.</summary>
    public static async Task<string> DownloadAsync(UpdateAsset asset, IProgress<double> progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "AirWave-update");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Path.GetFileName(asset.Name));
        using (var resp = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(path);
            var buf = new byte[81920];
            long got = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                got += n;
                if (total > 0) progress?.Report(Math.Min(1.0, got / (double)total));
            }
        }
        Verify(path, asset);
        return path;
    }

    public static void Verify(string path, UpdateAsset asset)
    {
        var len = new FileInfo(path).Length;
        if (asset.Size > 0 && len != asset.Size) throw new IOException($"The download was incomplete ({len} of {asset.Size} bytes)");
        if (asset.Sha256 != null)
        {
            using var f = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            if (hash != asset.Sha256) throw new IOException("The download didn't match the release (checksum)");
        }
    }

    /// <summary>
    /// Arranges for the update to be put in place once this process (<paramref name="pid"/>) has exited, then
    /// AirWave to start again. The caller should quit straight after.
    /// </summary>
    public static void ScheduleInstall(string downloaded, string exePath, int pid)
    {
        string script;
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        if (downloaded.Contains("Setup", StringComparison.OrdinalIgnoreCase))
        {
            // Installed copy: run the installer quietly; it restarts AirWave itself (/RELAUNCH=1)
            string scope = exePath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase) ? "/ALLUSERS" : "/CURRENTUSER";
            script = $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; " +
                     $"Start-Process -FilePath {Q(downloaded)} -ArgumentList '/SILENT','/SUPPRESSMSGBOXES','/NORESTART','/NOCANCEL','{scope}','/RELAUNCH=1'";
        }
        else
        {
            // Portable: swap the .exe (keeping its name and folder), then start it
            string old = exePath + ".old";
            script = $"Wait-Process -Id {pid} -Timeout 60 -ErrorAction SilentlyContinue; Start-Sleep -Milliseconds 300; " +
                     $"try {{ Move-Item -LiteralPath {Q(exePath)} -Destination {Q(old)} -Force -ErrorAction Stop; " +
                     $"Copy-Item -LiteralPath {Q(downloaded)} -Destination {Q(exePath)} -Force -ErrorAction Stop }} " +
                     $"catch {{ if (-not (Test-Path -LiteralPath {Q(exePath)})) {{ Move-Item -LiteralPath {Q(old)} -Destination {Q(exePath)} -Force }} }}; " +
                     $"Start-Process -FilePath {Q(exePath)} -ArgumentList '--after-update'";
        }
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-Command", script }) psi.ArgumentList.Add(a);
        Process.Start(psi);
        Log.Info($"Update scheduled: {Path.GetFileName(downloaded)}");
    }

    /// <summary>Tidies up after a portable update (the previous .exe) — call at startup.</summary>
    public static void CleanUp(string exePath)
    {
        try { File.Delete(exePath + ".old"); } catch { }
    }

    /// <summary>Checks the folder the portable .exe is in can be written to (otherwise only the installer/download page will do).</summary>
    public static bool CanReplace(string exePath)
    {
        try
        {
            string probe = Path.Combine(Path.GetDirectoryName(exePath) ?? ".", $".airwave-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
