using System.Collections.ObjectModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using AirWave.Core;
using AirWave.Core.Audio;
using AirWave.Core.Audio.Windows;
using AirWave.Core.Discovery;
using AirWave.Core.Receiver;
using AirWave.Core.Streaming;
using Microsoft.Win32;

namespace AirWave;

/// <summary>
/// Glue between the window and the engine: discovery, the speaker group, capture of the PC's
/// sound, muting the PC's own speakers while streaming, and settings.
/// </summary>
public sealed class AppController : Observable, IAsyncDisposable
{
    private readonly Dispatcher _ui;
    private readonly MdnsBrowser _browser = new();
    private readonly StreamEngine _engine;
    private readonly LoopbackSource _capture;
    private bool? _pcWasMuted;
    private string _mutedDeviceId;
    private readonly DispatcherTimer _meter;
    private readonly Dictionary<string, CancellationTokenSource> _volumeDebounce = new();
    private double _masterVolume;
    private double _level;
    private string _statusLine = "Looking for speakers…";
    private DeviceChoice _selectedDevice;
    private bool _searching = true;

    // AirPlay to this PC
    private AirPlayReceiver _receiver;
    private RenderOutput _render;
    private RelaySource _relay;
    private string _receiverClient;

    public AppSettings Settings { get; }
    public ObservableCollection<SpeakerViewModel> Speakers { get; } = new();
    public ObservableCollection<DeviceChoice> Devices { get; } = new();
    public string Version { get; } = typeof(AppController).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public AppController(Dispatcher ui)
    {
        _ui = ui;
        Settings = AppSettings.Load();
        _masterVolume = Settings.MasterVolume;

        _engine = new StreamEngine
        {
            ClientName = Environment.MachineName,
            DelayMs = Math.Clamp(Settings.DelayMs, 1000, 3000),
            ForceNtp = Settings.ForceNtp,
        };
        _capture = new LoopbackSource { DeviceId = Settings.CaptureDeviceId };
        _engine.Source = _capture;
        _engine.SpeakerChanged += s => _ui.InvokeAsync(() => OnSessionChanged(s));

        _browser.SpeakerFound += s => _ui.InvokeAsync(() => OnFound(s));
        _browser.SpeakerUpdated += s => _ui.InvokeAsync(() => OnFound(s));
        _browser.SpeakerLost += s => _ui.InvokeAsync(() => OnLost(s));

        EventsChannelHook.Attach();

        _meter = new DispatcherTimer(DispatcherPriority.Render, ui) { Interval = TimeSpan.FromMilliseconds(50) };
        _meter.Tick += (_, _) =>
        {
            Level = _engine.IsStreaming ? _engine.Source?.Level ?? 0 : 0;
            ReceiverLevel = _receiver?.Player.Level ?? 0;
        };
    }

    public void Start()
    {
        StartReceiver();
        RefreshDevices();
        foreach (var m in Settings.ManualSpeakers) AddManualSpeaker(m, save: false);
        _browser.Start();
        _meter.Start();
        _ = StopSearchingLaterAsync();
        if (_engine.Ptp != null && !_engine.Ptp.IsRunning)
            Log.Warn(_engine.Ptp.Error);
    }

    private async Task StopSearchingLaterAsync()
    {
        await Task.Delay(8000);
        Searching = false;
        UpdateStatusLine();
    }

    // ------------------------------------------------------------------ bindable state

    public bool Searching { get => _searching; private set { if (Set(ref _searching, value)) Raise(nameof(ShowEmpty)); } }
    public bool ShowEmpty => Speakers.Count == 0;
    public bool IsStreaming => Speakers.Any(s => s.IsOn);
    public double Level { get => _level; private set => Set(ref _level, value); }
    public string StatusLine { get => _statusLine; private set => Set(ref _statusLine, value); }

    public double MasterVolume
    {
        get => _masterVolume;
        set
        {
            if (!Set(ref _masterVolume, Math.Clamp(value, 0, 1))) return;
            Raise(nameof(MasterPercent));
            Settings.MasterVolume = _masterVolume;
            foreach (var s in Speakers.Where(x => x.IsOn)) OnSpeakerVolumeChanged(s, save: false);
            SaveSoon();
        }
    }

    public string MasterPercent => $"{Math.Round(_masterVolume * 100)}%";

    public DeviceChoice SelectedDevice
    {
        get => _selectedDevice;
        set
        {
            if (value == null || !Set(ref _selectedDevice, value)) return;
            string id = value.Id;
            if (Settings.CaptureDeviceId == id) return;
            Settings.CaptureDeviceId = id;
            Settings.Save();
            // Moving mute to the new device
            RestorePcMute();
            _capture.DeviceId = id;
            if (_engine.IsStreaming) { _capture.Restart(); ApplyPcMute(); }
        }
    }

    public bool MutePcWhileStreaming
    {
        get => Settings.MutePcWhileStreaming;
        set
        {
            if (Settings.MutePcWhileStreaming == value) return;
            Settings.MutePcWhileStreaming = value;
            Settings.Save();
            Raise();
            if (value) { if (IsStreaming) ApplyPcMute(); }
            else RestorePcMute();
        }
    }

    public double DelaySeconds
    {
        get => Settings.DelayMs / 1000.0;
        set
        {
            int ms = (int)Math.Round(Math.Clamp(value, 1.0, 3.0) * 10) * 100;
            if (Settings.DelayMs == ms) return;
            Settings.DelayMs = ms;
            _engine.DelayMs = ms;
            Raise(); Raise(nameof(DelayText));
            SaveSoon();
            // Speakers pick up the new delay from the next start; restart any that are playing
            _ = RestartPlayingAsync();
        }
    }

    public string DelayText => $"{Settings.DelayMs / 1000.0:0.0} s";

    public bool ReconnectOnLaunch
    {
        get => Settings.ReconnectOnLaunch;
        set { Settings.ReconnectOnLaunch = value; Settings.Save(); Raise(); }
    }

    public bool CloseToTray
    {
        get => Settings.CloseToTray;
        set { Settings.CloseToTray = value; Settings.Save(); Raise(); }
    }

    public bool StartMinimised
    {
        get => Settings.StartMinimised;
        set { Settings.StartMinimised = value; Settings.Save(); Raise(); }
    }

    public bool ForceNtp
    {
        get => Settings.ForceNtp;
        set { Settings.ForceNtp = value; _engine.ForceNtp = value; Settings.Save(); Raise(); }
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool StartWithWindows
    {
        get
        {
            try { using var k = Registry.CurrentUser.OpenSubKey(RunKey); return k?.GetValue("AirWave") != null; }
            catch { return false; }
        }
        set
        {
            try
            {
                using var k = Registry.CurrentUser.CreateSubKey(RunKey);
                if (value) k.SetValue("AirWave", $"\"{Environment.ProcessPath}\" --tray");
                else k.DeleteValue("AirWave", false);
            }
            catch (Exception ex) { Log.Warn($"Start with Windows: {ex.Message}"); }
            Raise();
        }
    }

    // ------------------------------------------------------------------ devices

    public void RefreshDevices()
    {
        Devices.Clear();
        Devices.Add(new DeviceChoice(null, "Default output (follows Windows)"));
        try
        {
            foreach (var d in WindowsAudio.ListOutputs()) Devices.Add(new DeviceChoice(d.Id, d.Name + (d.IsDefault ? "  (default)" : "")));
        }
        catch (Exception ex) { Log.Warn($"Couldn't list sound devices: {ex.Message}"); }
        _selectedDevice = Devices.FirstOrDefault(d => d.Id == Settings.CaptureDeviceId) ?? Devices[0];
        Raise(nameof(SelectedDevice));
    }

    // ------------------------------------------------------------------ discovery

    /// <summary>Set once this PC's own AirPlay receiver has been seen on the network (self-test check).</summary>
    internal bool SawOwnReceiver { get; private set; }

    private void OnFound(SpeakerInfo info)
    {
        // Don't offer this PC's own receiver as a speaker (it would play the PC to itself)
        if (_receiver != null && string.Equals(info.Id, _receiver.Identity.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            SawOwnReceiver = true;
            return;
        }
        var vm = Speakers.FirstOrDefault(s => s.Id == info.Id)
                 ?? Speakers.FirstOrDefault(s => s.Info.Txt.ContainsKey("__manual") && info.Addresses.Contains(s.Info.PreferredAddress));
        if (vm != null)
        {
            if (vm.Info.Txt.ContainsKey("__manual")) { /* keep the manual entry but refresh what we know */ }
            vm.Update(info);
            vm.IsAvailable = true;
            return;
        }
        double vol = Settings.SpeakerVolumes.TryGetValue(info.Id, out var v) ? v : 0.7;
        vm = new SpeakerViewModel(this, info, vol);
        InsertSorted(vm);
        Raise(nameof(ShowEmpty));
        UpdateStatusLine();

        if (Settings.ReconnectOnLaunch && Settings.LastSpeakers.Contains(info.Id) && !_autoConnected.Contains(info.Id))
        {
            _autoConnected.Add(info.Id);
            vm.IsOn = true;
        }
    }

    private readonly HashSet<string> _autoConnected = new();

    private void InsertSorted(SpeakerViewModel vm)
    {
        int i = 0;
        while (i < Speakers.Count && string.Compare(Speakers[i].Name, vm.Name, StringComparison.CurrentCultureIgnoreCase) < 0) i++;
        Speakers.Insert(i, vm);
    }

    private void OnLost(SpeakerInfo info)
    {
        var vm = Speakers.FirstOrDefault(s => s.Id == info.Id);
        if (vm == null) return;
        if (vm.IsOn) return; // still streaming to it; the session itself will tell us if it really went away
        vm.IsAvailable = false;
    }

    public void Rescan()
    {
        Searching = true;
        StatusLine = "Looking for speakers…";
        _browser.Refresh();
        _ = StopSearchingLaterAsync();
    }

    public bool AddManualSpeaker(string address, int port, string name)
    {
        if (!IPAddress.TryParse(address.Trim(), out _)) return false;
        var m = new AppSettings.ManualSpeaker { Address = address.Trim(), Port = port, Name = string.IsNullOrWhiteSpace(name) ? address.Trim() : name.Trim() };
        AddManualSpeaker(m, save: true);
        return true;
    }

    private void AddManualSpeaker(AppSettings.ManualSpeaker m, bool save)
    {
        if (!IPAddress.TryParse(m.Address, out var ip)) return;
        var info = new SpeakerInfo
        {
            Id = "manual:" + m.Address + ":" + m.Port,
            Name = m.Name,
            Port = m.Port,
            // Assume an AirPlay 2 speaker with PTP; the speaker's /info refines what we know
            Features = (1UL << 9) | (1UL << 48) | (1UL << 41),
        };
        info.Addresses.Add(ip);
        info.Txt["__manual"] = "1";
        if (save)
        {
            Settings.ManualSpeakers.RemoveAll(x => x.Address == m.Address && x.Port == m.Port);
            Settings.ManualSpeakers.Add(m);
            Settings.Save();
        }
        OnFound(info);
    }

    public void RemoveManualSpeaker(SpeakerViewModel vm)
    {
        if (!vm.Info.Txt.ContainsKey("__manual")) return;
        vm.IsOn = false;
        Settings.ManualSpeakers.RemoveAll(x => "manual:" + x.Address + ":" + x.Port == vm.Id);
        Settings.Save();
        Speakers.Remove(vm);
        Raise(nameof(ShowEmpty));
    }

    // ------------------------------------------------------------------ group

    internal async Task SetSpeakerOnAsync(SpeakerViewModel vm, bool on)
    {
        if (on)
        {
            vm.State = SpeakerUiState.Connecting;
            vm.Status = "Connecting…";
            ApplyPcMute();
            var s = await _engine.AddSpeakerAsync(vm.Info, EffectiveVolume(vm));
            _ui.Invoke(() => vm.ApplySession(s));
            if (s.State == SpeakerState.Failed) vm.SetOnSilently(false);
        }
        else
        {
            await _engine.RemoveSpeakerAsync(vm.Id);
            vm.State = SpeakerUiState.Off;
            vm.Status = vm.DefaultStatus;
        }
        RememberGroup();
        Raise(nameof(IsStreaming));
        if (!IsStreaming) RestorePcMute();
        UpdateStatusLine();
    }

    public async Task StopAllAsync()
    {
        foreach (var vm in Speakers.Where(s => s.IsOn).ToList()) vm.SetOnSilently(false);
        await _engine.RemoveAllAsync();
        foreach (var vm in Speakers) { if (vm.State != SpeakerUiState.Problem) { vm.State = SpeakerUiState.Off; vm.Status = vm.DefaultStatus; } }
        RememberGroup();
        RestorePcMute();
        Raise(nameof(IsStreaming));
        UpdateStatusLine();
    }

    private async Task RestartPlayingAsync()
    {
        var on = Speakers.Where(s => s.IsOn).ToList();
        if (on.Count == 0) return;
        await _engine.RemoveAllAsync();
        foreach (var vm in on) _ = SetSpeakerOnAsync(vm, true);
    }

    private void OnSessionChanged(SpeakerSession s)
    {
        var vm = Speakers.FirstOrDefault(x => x.Id == s.Info.Id);
        if (vm == null) return;
        if (s.State == SpeakerState.Idle && !vm.IsOn) return;
        vm.ApplySession(s);
        if (s.State == SpeakerState.Failed)
        {
            Raise(nameof(IsStreaming));
            if (!IsStreaming) RestorePcMute();
        }
        UpdateStatusLine();
    }

    private double EffectiveVolume(SpeakerViewModel vm) => vm.Volume * _masterVolume;

    internal void OnSpeakerVolumeChanged(SpeakerViewModel vm, bool save = true)
    {
        if (save)
        {
            Settings.SpeakerVolumes[vm.Id] = vm.Volume;
            SaveSoon();
        }
        var session = _engine.GetSession(vm.Id);
        if (session == null) return;
        // Debounce: a slider drag fires many changes, send at most every 80 ms
        if (_volumeDebounce.TryGetValue(vm.Id, out var old)) old.Cancel();
        var cts = new CancellationTokenSource();
        _volumeDebounce[vm.Id] = cts;
        double v = EffectiveVolume(vm);
        _ = Task.Delay(80, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _ = session.SetVolumeAsync(v); }, TaskScheduler.Default);
    }

    private void RememberGroup()
    {
        Settings.LastSpeakers = Speakers.Where(s => s.IsOn).Select(s => s.Id).ToList();
        Settings.Save();
    }

    private void UpdateStatusLine()
    {
        int playing = Speakers.Count(s => s.IsPlaying);
        int connecting = Speakers.Count(s => s.IsConnecting);
        if (playing > 0)
            StatusLine = $"Playing on {playing} speaker{(playing == 1 ? "" : "s")}" + (connecting > 0 ? $" · {connecting} connecting" : "") + $" · {Settings.DelayMs / 1000.0:0.0} s delay";
        else if (connecting > 0)
            StatusLine = "Connecting…";
        else if (Speakers.Count == 0)
            StatusLine = Searching ? "Looking for speakers…" : "No speakers found yet";
        else
            StatusLine = "Pick the speakers to play your PC's sound on";
        Raise(nameof(IsStreaming));
        Raise(nameof(ReceiverSubtitle));
    }

    // ------------------------------------------------------------------ PC mute

    private void ApplyPcMute()
    {
        if (!Settings.MutePcWhileStreaming || _pcWasMuted != null) return;
        _mutedDeviceId = Settings.CaptureDeviceId;
        _pcWasMuted = WindowsAudio.SetMute(_mutedDeviceId, true);
    }

    private void RestorePcMute()
    {
        if (_pcWasMuted == null) return;
        if (_pcWasMuted == false) WindowsAudio.SetMute(_mutedDeviceId, false);
        _pcWasMuted = null;
    }

    // ------------------------------------------------------------------ AirPlay to this PC

    public string ReceiverDefaultName
    {
        get
        {
            string m = Environment.MachineName;
            // Windows' auto-generated names (DESKTOP-4F2K9QX) look odd in an AirPlay list
            if (System.Text.RegularExpressions.Regex.IsMatch(m, @"^(DESKTOP|LAPTOP|WIN)-[A-Z0-9]{5,}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string u = Environment.UserName;
                if (!string.IsNullOrWhiteSpace(u)) return char.ToUpper(u[0]) + u[1..] + "'s PC";
            }
            return m;
        }
    }

    public bool ReceiverEnabled
    {
        get => Settings.ReceiverEnabled;
        set
        {
            if (Settings.ReceiverEnabled == value) return;
            Settings.ReceiverEnabled = value;
            Settings.Save();
            if (value) StartReceiver(); else _ = StopReceiverAsync();
            Raise(); Raise(nameof(ReceiverHint));
        }
    }

    public string ReceiverName
    {
        get => string.IsNullOrWhiteSpace(Settings.ReceiverName) ? ReceiverDefaultName : Settings.ReceiverName;
        set
        {
            var v = (value ?? "").Trim();
            Settings.ReceiverName = v.Length == 0 || v == ReceiverDefaultName ? null : v;
            SaveSoon();
            if (_receiver != null) _receiver.Name = ReceiverName;
            Raise(); Raise(nameof(ReceiverHint));
        }
    }

    public bool ReceiverPlaysOnSpeakers
    {
        get => Settings.ReceiverPlaysOnSpeakers;
        set
        {
            if (Settings.ReceiverPlaysOnSpeakers == value) return;
            Settings.ReceiverPlaysOnSpeakers = value;
            Settings.Save();
            ApplyReceiverOutput();
            Raise(); Raise(nameof(ReceiverPlaysOnPc)); Raise(nameof(ReceiverSubtitle));
        }
    }

    public bool ReceiverPlaysOnPc
    {
        get => !ReceiverPlaysOnSpeakers;
        set => ReceiverPlaysOnSpeakers = !value;
    }

    public int ReceiverOutputIndex
    {
        get => ReceiverPlaysOnSpeakers ? 1 : 0;
        set { ReceiverPlaysOnSpeakers = value == 1; Raise(); }
    }

    public bool ReceiverActive => _receiverClient != null;
    public string ReceiverTitle => _receiverClient == null ? "" : $"Playing from {_receiverClient}";
    public string ReceiverSubtitle => !ReceiverPlaysOnSpeakers
        ? "On this PC's speakers"
        : Speakers.Any(s => s.IsOn) ? "Passing it on to your speakers below" : "Switch on a speaker below to hear it";
    public string ReceiverHint => ReceiverEnabled
        ? $"Shows up as “{ReceiverName}” in AirPlay on your iPhone, iPad and Mac."
        : "Turned off: this PC won't show up as an AirPlay speaker.";

    private double _receiverLevel;
    public double ReceiverLevel { get => _receiverLevel; private set => Set(ref _receiverLevel, value); }

    private void StartReceiver()
    {
        if (_receiver != null || !Settings.ReceiverEnabled) return;
        try
        {
            var id = ReceiverIdentity.LoadOrCreate(AppSettings.Folder);
            _receiver = new AirPlayReceiver(id, _engine.Ptp, ReceiverName);
            _receiver.StateChanged += () => _ui.InvokeAsync(OnReceiverState);
            _receiver.Start();
            _relay = new RelaySource(_receiver.Player);
            _render = new RenderOutput(_receiver.Player) { DeviceId = Settings.CaptureDeviceId };
            if (_engine.Ptp == null || !_engine.Ptp.IsRunning)
                Log.Warn("AirPlay to this PC: PTP ports are busy, so iPhones may not be able to play to this PC");
        }
        catch (Exception ex)
        {
            Log.Warn($"AirPlay to this PC couldn't start: {ex.Message}");
            _receiver = null;
        }
    }

    private async Task StopReceiverAsync()
    {
        var r = _receiver;
        _receiver = null;
        _render?.Stop();
        _render = null;
        if (_engine.Source == _relay) _engine.Source = _capture;
        _relay = null;
        if (r != null) await r.DisposeAsync();
        _receiverClient = null;
        RaiseReceiver();
    }

    private void OnReceiverState()
    {
        if (_receiver == null) return;
        _receiverClient = _receiver.ActiveClient;
        ApplyReceiverOutput();
        RaiseReceiver();
    }

    private void ApplyReceiverOutput()
    {
        bool active = _receiverClient != null;
        // This PC's speakers: only keep an output open while something is playing (an open stream can stop the PC sleeping)
        if (active && !ReceiverPlaysOnSpeakers) _render?.Start(); else _render?.Stop();
        // Speakers: received audio replaces the PC's sound for whichever speakers are switched on
        var wanted = active && ReceiverPlaysOnSpeakers ? (IAudioSource)_relay : _capture;
        if (wanted != null && !ReferenceEquals(_engine.Source, wanted)) _engine.Source = wanted;
    }

    private void RaiseReceiver()
    {
        Raise(nameof(ReceiverActive)); Raise(nameof(ReceiverTitle)); Raise(nameof(ReceiverSubtitle));
    }

    public void DisconnectReceiver() => _receiver?.Disconnect();

    // ------------------------------------------------------------------ self-test

    /// <summary>Sample speakers in each state, for the self-test screenshots only (nothing is connected).</summary>
    internal void AddDemoSpeakers()
    {
        SpeakerViewModel Make(string name, string model, string maker, SpeakerUiState state, string status, double vol, bool on)
        {
            var info = new SpeakerInfo { Id = "demo:" + name, Name = name, Model = model, Manufacturer = maker, Features = (1UL << 9) | (1UL << 41) | (1UL << 48) };
            info.Addresses.Add(IPAddress.Loopback);
            var vm = new SpeakerViewModel(this, info, vol);
            vm.SetOnSilently(on);
            vm.State = state;
            vm.Status = status ?? vm.DefaultStatus;
            InsertSorted(vm);
            return vm;
        }
        Make("Living Room", "AudioAccessory5,1", null, SpeakerUiState.Playing, "Playing · PTP sync", 0.62, true);
        Make("Kitchen", "AudioAccessory6,1", null, SpeakerUiState.Playing, "Playing · PTP sync", 0.45, true);
        Make("Office", "Era 100", "Sonos", SpeakerUiState.Connecting, "Connecting…", 0.5, true);
        Make("Bedroom", "AudioAccessory5,1", null, SpeakerUiState.Problem,
            "This speaker only lets certain people stream to it. In the Home app go to Home Settings → Speakers & TV → Allow Speaker & TV Access and choose \"Everyone on the same network\".", 0.5, false);
        Make("Lounge TV", "AppleTV11,1", null, SpeakerUiState.Off, null, 0.5, false);
        Searching = false;
        Raise(nameof(ShowEmpty));
        StatusLine = $"Playing on 2 speakers · 1 connecting · {Settings.DelayMs / 1000.0:0.0} s delay";
        Raise(nameof(IsStreaming));
        Level = 0.35;
        _receiverClient = "Sam's iPhone";
        ReceiverLevel = 0.5;
        RaiseReceiver();
        _meter.Stop();
    }

    // ------------------------------------------------------------------ misc

    private CancellationTokenSource _saveCts;
    private void SaveSoon()
    {
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;
        _ = Task.Delay(600, token).ContinueWith(t => { if (!t.IsCanceled) Settings.Save(); }, TaskScheduler.Default);
    }

    public async ValueTask DisposeAsync()
    {
        _meter.Stop();
        Settings.Save();
        RestorePcMute();
        try { await StopReceiverAsync(); } catch { }
        try { await _engine.DisposeAsync(); } catch { }
        _browser.Dispose();
    }
}

/// <summary>
/// Speakers such as HomePods send play/pause/next/previous when you tap or squeeze them.
/// AirWave passes those on to Windows as media keys, so whatever is playing on the PC responds.
/// </summary>
internal static class EventsChannelHook
{
    private static bool _attached;

    public static void Attach()
    {
        if (_attached) return;
        _attached = true;
        SpeakerCommands.RemoteCommand += (name, cmd) =>
        {
            byte vk = cmd switch
            {
                "paus" or "play" or "plpa" => 0xB3, // VK_MEDIA_PLAY_PAUSE
                "nitm" => 0xB0,                     // VK_MEDIA_NEXT_TRACK
                "pitm" => 0xB1,                     // VK_MEDIA_PREV_TRACK
                "stop" => 0xB2,                     // VK_MEDIA_STOP
                _ => 0
            };
            if (vk == 0) return;
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 2, UIntPtr.Zero);
        };
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
}
