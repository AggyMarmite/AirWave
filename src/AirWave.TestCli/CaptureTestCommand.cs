using System.Runtime.Versioning;
using AirWave.Core;
using AirWave.Core.Audio.Windows;

/// <summary>
/// "capture-test": with something else playing sound, mutes the default output and checks what AirWave captures:
/// app capture must still hear it; device capture is reported for comparison. Restores the mute afterwards.
/// </summary>
[SupportedOSPlatform("windows")]
static class CaptureTestCommand
{
    public static int Run(string[] args)
    {
        var was = WindowsAudio.SetMute(null, false);
        try
        {
            double appUnmuted = Measure(true, "app capture, not muted");
            WindowsAudio.SetMute(null, true);
            Thread.Sleep(500);
            double appMuted = Measure(true, "app capture, PC muted");
            double devMuted = Measure(false, "device capture, PC muted");
            bool ok = appUnmuted > 0.05 && appMuted > 0.05;
            Console.WriteLine(ok ? "PASS: muting the PC doesn't silence what AirWave sends" : "FAIL: capture silent");
            return ok ? 0 : 1;
        }
        finally { WindowsAudio.SetMute(null, was ?? false); }
    }

    private static double Measure(bool apps, string label)
    {
        var src = new LoopbackSource { PreferAppCapture = apps };
        src.Start();
        var buf = new short[1024 * 2];
        double peak = 0;
        var until = DateTime.UtcNow.AddSeconds(4);
        Thread.Sleep(500);
        while (DateTime.UtcNow < until)
        {
            Thread.Sleep(23);
            src.Read(buf, 1024);
            foreach (var v in buf) peak = Math.Max(peak, Math.Abs(v / 32768.0));
        }
        bool viaApps = src.CapturingApps;
        src.Stop();
        Console.WriteLine($"CAPTURE {label}: peak {peak:F3} ({(viaApps ? "apps" : "device " + src.CurrentDeviceName)}){(src.LastError != null ? " error " + src.LastError : "")}");
        return peak;
    }
}
