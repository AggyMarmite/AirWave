using System.Text.Json;

namespace AirWave.Core;

/// <summary>User settings, saved as JSON in %AppData%\AirWave\settings.json.</summary>
public sealed class AppSettings
{
    public string CaptureDeviceId { get; set; }          // null = follow Windows default output
    public bool MutePcWhileStreaming { get; set; } = true;
    public int DelayMs { get; set; } = 2000;
    public double MasterVolume { get; set; } = 0.6;
    public Dictionary<string, double> SpeakerVolumes { get; set; } = new();
    public List<string> LastSpeakers { get; set; } = new(); // reconnect these on launch if wanted
    public bool ReconnectOnLaunch { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimised { get; set; }
    public bool CloseToTray { get; set; } = true;
    public List<ManualSpeaker> ManualSpeakers { get; set; } = new();
    public bool ForceNtp { get; set; }

    // AirPlay to this PC
    public bool ReceiverEnabled { get; set; } = true;
    public string ReceiverName { get; set; }             // null = the PC's name
    public bool ReceiverPlaysOnSpeakers { get; set; }    // false = this PC's own output

    // Screen mirroring
    public bool MirrorWithSound { get; set; } = true;
    public bool UseGpuEncoder { get; set; } = true;      // graphics card capture + encoder: full resolution, 60 fps

    public sealed class ManualSpeaker
    {
        public string Name { get; set; }
        public string Address { get; set; }
        public int Port { get; set; } = 7000;
    }

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AirWave");
    private static string FilePath => Path.Combine(Folder, "settings.json");
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Opts) ?? new AppSettings();
        }
        catch (Exception ex) { Log.Warn($"Settings unreadable, using defaults: {ex.Message}"); }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex) { Log.Warn($"Couldn't save settings: {ex.Message}"); }
    }
}
