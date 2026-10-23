using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AirWave.Core;
using AirWave.Core.Audio.Windows;
using AirWave.Core.Discovery;

namespace AirWave;

/// <summary>
/// "--self-test &lt;folder&gt;": opens the app, fills it with sample speakers in every state, visits each
/// page and saves screenshots, checks the sound capture and engine start up, then writes result.txt.
/// Used by the build on GitHub so the app is seen working on a real Windows machine.
/// </summary>
internal static class SelfTest
{
    public static async Task RunAsync(App app, string dir)
    {
        var notes = new List<string>();
        bool ok = true;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "started.txt"), DateTime.Now.ToString("O"));
            var w = app.Window;
            w.Left = 80; w.Top = 60;
            await Task.Delay(1500);
            Snap(w, Path.Combine(dir, "01-empty.png"));

            app.Controller.AddDemoSpeakers();
            await Task.Delay(900);
            Snap(w, Path.Combine(dir, "02-speakers.png"));

            w.ShowAddPanel(true);
            await Task.Delay(400);
            Snap(w, Path.Combine(dir, "03-add-speaker.png"));
            w.ShowAddPanel(false);

            w.ShowSettings(true);
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "04-settings.png"));
            w.GpuEncoderSwitch.BringIntoView();
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "08-settings-mirroring.png"));
            if (w.GpuEncoderSwitch.IsChecked != true) { ok = false; notes.Add("graphics card encoder should be on by default"); }
            w.ShowSettings(false);

            // Sound devices and loopback capture (runners may have no sound card: that's reported, not failed)
            try
            {
                var outs = WindowsAudio.ListOutputs();
                notes.Add($"outputs: {outs.Count} ({string.Join(", ", outs.Select(o => o.Name))})");
                if (outs.Count > 0)
                {
                    var cap = new LoopbackSource();
                    cap.Start();
                    await Task.Delay(1500);
                    var buf = new short[352 * 2];
                    cap.Read(buf, 352);
                    notes.Add($"capture: {(cap.LastError == null ? "running on " + cap.CurrentDeviceName : "error " + cap.LastError)}");
                    cap.Stop();
                }
            }
            catch (Exception ex) { notes.Add("audio: " + ex.Message); }

            // Sound-source dropdown
            var combo = FindChild<System.Windows.Controls.ComboBox>(w);
            notes.Add($"source list: {combo?.Items.Count} items, selected '{combo?.SelectedItem}', box '{combo?.SelectionBoxItem}', vm '{app.Controller.SelectedDevice}'");
            if (combo == null || combo.SelectedItem == null) { ok = false; notes.Add("sound source dropdown is empty"); }

            // Discovery and engine sockets came up
            notes.Add($"mDNS speakers seen: {app.Controller.Speakers.Count(s => !s.Id.StartsWith("demo"))}");

            // AirPlay to this PC: advertised through Windows and visible on the network
            for (int i = 0; i < 20 && !app.Controller.SawOwnReceiver; i++) await Task.Delay(500);
            notes.Add($"own AirPlay receiver advertised and discovered: {app.Controller.SawOwnReceiver}");
            if (!app.Controller.SawOwnReceiver) { ok = false; notes.Add("this PC's AirPlay receiver wasn't discoverable"); }

            // Screens to mirror to, and the TV code prompt
            w.ScrollSpeakersToEnd();
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "06-screens.png"));
            app.Controller.ShowDemoPrompt();
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "07-tv-code.png"));
            app.Controller.CancelPrompt();

            // In-app updates: the banner, and a real check against GitHub
            app.Controller.ShowDemoUpdate();
            w.ScrollSpeakersToEnd();
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "09-update.png"));
            app.Controller.HideDemoUpdate();
            try
            {
                var latest = await AirWave.Core.Updates.Updater.CheckAsync(new Version(0, 0, 1));
                notes.Add($"update check: latest release {latest?.Version} installer={latest?.Installer?.Name} ({latest?.Installer?.Size} bytes, sha256 {(latest?.Installer?.Sha256 != null ? "given" : "not given")}) portable={latest?.Portable?.Name}");
                if (latest?.Installer == null || latest.Portable == null) { ok = false; notes.Add("update check didn't find the release files"); }
            }
            catch (Exception ex) { notes.Add("update check: couldn't reach GitHub (" + ex.Message + ")"); }
            w.ShowSettings(true);
            w.UpdatesCard.BringIntoView();
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "10-settings-updates.png"));
            w.ShowSettings(false);

            // Bigger window, to check the layout stretches nicely
            w.Width = 560; w.Height = 820;
            await Task.Delay(500);
            Snap(w, Path.Combine(dir, "05-wide.png"));
        }
        catch (Exception ex)
        {
            ok = false;
            File.WriteAllText(Path.Combine(dir, "crash.txt"), ex.ToString());
        }

        var errors = Log.Snapshot().Where(l => l.Contains("[E]")).ToList();
        if (errors.Count > 0) { ok = false; notes.Add("errors in log:\n" + string.Join("\n", errors)); }
        File.WriteAllText(Path.Combine(dir, "result.txt"), string.Join("\n", notes) + "\n" + (ok ? "PASSED" : "FAILED"));
        File.WriteAllLines(Path.Combine(dir, "log.txt"), Log.Snapshot());
        app.Quit();
    }

    private static T FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            var r = FindChild<T>(c);
            if (r != null) return r;
        }
        return null;
    }

    private static void Snap(Window w, string path)
    {
        w.UpdateLayout();
        var root = (FrameworkElement)w.Content;
        int width = (int)Math.Ceiling(root.ActualWidth), height = (int)Math.Ceiling(root.ActualHeight);
        if (width <= 0 || height <= 0) return;
        var rtb = new RenderTargetBitmap(width * 2, height * 2, 192, 192, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle((Brush)w.Background, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, width, height));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
