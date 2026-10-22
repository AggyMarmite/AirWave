using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AirWave.Core;

namespace AirWave;

public partial class App : Application
{
    private Mutex _single;
    private EventWaitHandle _showSignal;
    private StreamWriter _logFile;
    private readonly object _logLock = new();

    public AppController Controller { get; private set; }
    public MainWindow Window { get; private set; }
    public TrayIcon Tray { get; private set; }

    public static string LogPath => Path.Combine(AppSettings.Folder, "log.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        string selfTestDir = null;
        int st = Array.IndexOf(args, "--self-test");
        if (st >= 0) selfTestDir = st + 1 < args.Length ? args[st + 1] : Path.Combine(Path.GetTempPath(), "airwave-selftest");

        if (selfTestDir == null)
        {
            _single = new Mutex(true, "AirWave.SingleInstance", out bool first);
            _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "AirWave.ShowWindow");
            if (!first)
            {
                _showSignal.Set(); // ask the running copy to show itself
                Shutdown();
                return;
            }
            new Thread(() =>
            {
                while (_showSignal.WaitOne()) Dispatcher.InvokeAsync(() => ShowMainWindow());
            }) { IsBackground = true, Name = "AirWave show signal" }.Start();
        }

        StartLogFile();
        DispatcherUnhandledException += (_, ex) =>
        {
            Log.Error("Unhandled: " + ex.Exception);
            ex.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ex) => Log.Error("Fatal: " + ex.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, ex) => { Log.Debug("Unobserved: " + ex.Exception?.GetBaseException().Message); ex.SetObserved(); };

        // Finer timer resolution for steady audio packet timing
        timeBeginPeriod(1);

        Log.Info($"AirWave {typeof(App).Assembly.GetName().Version?.ToString(3)} starting on {Environment.OSVersion}");
        Controller = new AppController(Dispatcher);
        Window = new MainWindow(Controller);
        Tray = new TrayIcon(this);

        if (selfTestDir != null)
        {
            Window.Show();
            _ = SelfTest.RunAsync(this, selfTestDir);
            return;
        }

        bool toTray = args.Contains("--tray") || Controller.Settings.StartMinimised;
        if (!toTray) Window.Show();
        Controller.Start();
    }

    public void ShowMainWindow()
    {
        if (Window == null) return;
        Window.Show();
        if (Window.WindowState == WindowState.Minimized) Window.WindowState = WindowState.Normal;
        Window.Activate();
        Window.Topmost = true;
        Window.Topmost = false;
        Window.Focus();
    }

    private bool _quitStarted;

    public async void Quit()
    {
        if (_quitStarted) return;
        _quitStarted = true;
        Window?.MarkQuitting();
        Tray?.Dispose();
        if (Controller != null) await Controller.DisposeAsync();
        timeEndPeriod(1);
        lock (_logLock) { _logFile?.Dispose(); _logFile = null; }
        Shutdown();
    }

    private void StartLogFile()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 2_000_000)
                File.Move(LogPath, LogPath + ".old", true);
            _logFile = new StreamWriter(new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
            Log.Written += (_, line) => { lock (_logLock) { try { _logFile?.WriteLine(line); } catch { } } };
        }
        catch { }
    }

    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint ms);
}
