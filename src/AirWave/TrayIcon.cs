using System.Windows;
using WinForms = System.Windows.Forms;

namespace AirWave;

/// <summary>The icon in the notification area by the clock, with a small menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly App _app;

    public TrayIcon(App app)
    {
        _app = app;
        _icon = new WinForms.NotifyIcon
        {
            Text = "AirWave",
            Icon = LoadIcon(),
            Visible = true,
        };
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("Open AirWave", null, (_, _) => _app.Dispatcher.InvokeAsync(() => _app.ShowMainWindow()));
        menu.Items.Add("Stop playing", null, (_, _) => _app.Dispatcher.InvokeAsync(async () => await _app.Controller.StopAllAsync()));
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => _app.Dispatcher.InvokeAsync(() => _app.Quit()));
        _icon.ContextMenuStrip = menu;
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _app.Dispatcher.InvokeAsync(() => _app.ShowMainWindow());
        };
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/AirWave.ico"));
            if (res != null) return new System.Drawing.Icon(res.Stream, 32, 32);
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void ShowHint(string title, string text)
    {
        try { _icon.ShowBalloonTip(3000, title, text, WinForms.ToolTipIcon.None); } catch { }
    }

    public void SetPlaying(bool playing, int count)
    {
        _icon.Text = playing ? $"AirWave – playing on {count} speaker{(count == 1 ? "" : "s")}" : "AirWave";
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
