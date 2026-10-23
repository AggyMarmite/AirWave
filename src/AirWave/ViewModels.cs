using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AirWave.Core.Discovery;
using AirWave.Core.Streaming;

namespace AirWave;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public enum SpeakerUiState { Off, Connecting, Playing, Problem }

/// <summary>A speaker row in the list.</summary>
public sealed class SpeakerViewModel : Observable
{
    private readonly AppController _app;
    private bool _isOn;
    private double _volume;
    private SpeakerUiState _state;
    private string _status;
    private bool _isAvailable = true;

    public SpeakerInfo Info { get; private set; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Kind => Info.Kind;

    public SpeakerViewModel(AppController app, SpeakerInfo info, double volume)
    {
        _app = app;
        Info = info;
        _volume = volume;
        _status = DefaultStatus;
    }

    /// <summary>Icon glyph (Segoe Fluent Icons).</summary>
    public string Glyph => Info.IsAppleTv ? "\uE7F4" : "\uE7F5";

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!Set(ref _isOn, value)) return;
            _ = _app.SetSpeakerOnAsync(this, value);
        }
    }

    /// <summary>Changes the toggle without starting/stopping anything (used when the engine reports changes).</summary>
    internal void SetOnSilently(bool on)
    {
        if (_isOn == on) return;
        _isOn = on;
        Raise(nameof(IsOn));
    }

    public double Volume
    {
        get => _volume;
        set
        {
            if (!Set(ref _volume, Math.Clamp(value, 0, 1))) return;
            Raise(nameof(VolumePercent));
            _app.OnSpeakerVolumeChanged(this);
        }
    }

    public string VolumePercent => $"{Math.Round(_volume * 100)}%";

    public SpeakerUiState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsPlaying)); Raise(nameof(IsConnecting)); Raise(nameof(HasProblem)); Raise(nameof(ShowVolume));
        }
    }

    public bool IsPlaying => _state == SpeakerUiState.Playing;
    public bool IsConnecting => _state == SpeakerUiState.Connecting;
    public bool HasProblem => _state == SpeakerUiState.Problem;
    public bool ShowVolume => _state is SpeakerUiState.Playing or SpeakerUiState.Connecting;

    public string Status { get => _status; set => Set(ref _status, value); }

    /// <summary>False when the speaker has stopped announcing itself on the network.</summary>
    public bool IsAvailable
    {
        get => _isAvailable;
        set { if (Set(ref _isAvailable, value) && _state == SpeakerUiState.Off) Status = DefaultStatus; }
    }

    public string DefaultStatus => !_isAvailable ? "Not found on the network right now"
        : Info.PasswordRequired ? $"{Kind} · password protected"
        : Kind;

    public void Update(SpeakerInfo info)
    {
        Info = info;
        Raise(nameof(Name)); Raise(nameof(Kind)); Raise(nameof(Glyph));
        if (_state == SpeakerUiState.Off) Status = DefaultStatus;
    }

    public void ApplySession(SpeakerSession s)
    {
        switch (s.State)
        {
            case SpeakerState.Connecting:
                State = SpeakerUiState.Connecting;
                Status = "Connecting…";
                break;
            case SpeakerState.Ready:
            case SpeakerState.Streaming:
                State = SpeakerUiState.Playing;
                Status = $"Playing · {(s.UsesPtp ? "PTP" : "NTP")} sync";
                break;
            case SpeakerState.Failed:
                State = SpeakerUiState.Problem;
                Status = s.ProblemText ?? "Couldn't connect";
                SetOnSilently(false);
                break;
            default:
                State = SpeakerUiState.Off;
                Status = DefaultStatus;
                break;
        }
    }
}

public enum ScreenUiState { Off, Connecting, Mirroring, Problem }

/// <summary>A TV (or Mac) the screen can be mirrored to.</summary>
public sealed class ScreenViewModel : Observable
{
    private ScreenUiState _state;
    private string _status;
    public SpeakerInfo Info { get; private set; }
    public string Id => Info.Id;
    public string Name => Info.Name;
    public string Glyph => Info.IsMac ? "\uE7F8" : "\uE7F4";
    public string Kind => Info.IsAppleTv ? "Apple TV" : Info.IsMac ? "Mac" : string.IsNullOrEmpty(Info.Manufacturer) ? "AirPlay screen" : Info.Manufacturer;

    public ScreenViewModel(SpeakerInfo info) { Info = info; _status = Kind; }

    public ScreenUiState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value)) return;
            Raise(nameof(IsMirroring)); Raise(nameof(IsBusy)); Raise(nameof(HasProblem)); Raise(nameof(ButtonText));
        }
    }

    public bool IsMirroring => _state == ScreenUiState.Mirroring;
    public bool IsBusy => _state == ScreenUiState.Connecting;
    public bool HasProblem => _state == ScreenUiState.Problem;
    public string ButtonText => _state switch { ScreenUiState.Mirroring => "Stop", ScreenUiState.Connecting => "Cancel", _ => "Mirror" };
    public string Status { get => _status; set => Set(ref _status, value); }
    private string _details;
    /// <summary>Which capture and encoder are in use (shown as a tooltip while mirroring).</summary>
    public string Details { get => _details; set => Set(ref _details, value); }
    public string DefaultStatus => Kind;

    public void Update(SpeakerInfo info)
    {
        Info = info;
        Raise(nameof(Name)); Raise(nameof(Glyph)); Raise(nameof(Kind));
        if (_state == ScreenUiState.Off) Status = DefaultStatus;
    }
}

public sealed record DeviceChoice(string Id, string Name)
{
    public override string ToString() => Name;
}
