using AirWave.Core.Audio;
using AirWave.Core.Timing;

namespace AirWave.Core.Receiver;

/// <summary>Somewhere received audio goes: this PC's speakers, or on to AirPlay speakers.</summary>
public interface IReceiverOutput : IDisposable
{
    void Start();
    void Stop();
}

/// <summary>
/// Hands received audio to the streaming engine so it can go on to AirPlay speakers ("relay").
/// The engine reads ahead by its own delay; we give it the audio the sender wanted heard now,
/// so speakers play it that much later than the sender intended (they're not part of the sender's group).
/// </summary>
public sealed class RelaySource : IAudioSource
{
    private readonly ReceiverPlayer _player;
    public RelaySource(ReceiverPlayer player) { _player = player; }
    public float Level => _player.Level;

    public void Read(Span<short> interleaved, int frames)
    {
        // Ask for audio due a little ahead of now: it has certainly arrived, and keeps the extra delay small.
        _player.Read(MonoClock.NowNs + 150_000_000L, interleaved, frames);
    }

    public void Start() { }
    public void Stop() { }
    public void Dispose() { }
}
