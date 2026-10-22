using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using AirWave.Core.Receiver;
using AirWave.Core.Timing;

namespace AirWave.Core.Audio.Windows;

/// <summary>
/// Plays received AirPlay audio on a Windows output device (WASAPI shared mode, 44.1 kHz 16-bit with
/// Windows doing any conversion). Uses the device clock to know exactly when each sample will be heard,
/// so playback lines up with the sender and any other speakers it is playing to.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class RenderOutput : IReceiverOutput
{
    private const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
    private const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    private const int CLSCTX_ALL = 23;
    private static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    private static readonly Guid IID_IAudioClock = new("CD63314F-3FBA-4a1b-812C-EF96358728E7");

    private readonly ReceiverPlayer _player;
    private Thread _thread;
    private volatile bool _run;
    public string DeviceId { get; set; } // null = default output
    public string LastError { get; private set; }

    public RenderOutput(ReceiverPlayer player) { _player = player; }

    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "AirWave playback", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        _thread?.Join(1000);
        _thread = null;
    }

    private void Loop()
    {
        while (_run)
        {
            try { Session(); }
            catch (Exception ex) { LastError = ex.Message; Log.Warn($"Playback: {ex.Message} – retrying"); }
            for (int i = 0; i < 10 && _run; i++) Thread.Sleep(100);
        }
    }

    private void Session()
    {
        var dev = WindowsAudio.GetDevice(DeviceId);
        IAudioClient client = null;
        IAudioRenderClient render = null;
        IAudioClock clock = null;
        IntPtr fmtPtr = IntPtr.Zero;
        try
        {
            var iid = CoreAudioIds.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o));
            client = (IAudioClient)o;

            var fmt = new WaveFormatEx { wFormatTag = 1, nChannels = 2, nSamplesPerSec = 44100, wBitsPerSample = 16, nBlockAlign = 4, nAvgBytesPerSec = 44100 * 4, cbSize = 0 };
            fmtPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
            Marshal.StructureToPtr(fmt, fmtPtr, false);
            // 60 ms buffer, Windows converts to the device's own format
            Marshal.ThrowExceptionForHR(client.Initialize(0, AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY, 600_000, 0, fmtPtr, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.GetBufferSize(out uint bufFrames));
            client.GetStreamLatency(out long latency100ns);
            var riid = IID_IAudioRenderClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref riid, out var ro));
            render = (IAudioRenderClient)ro;
            var ciid = IID_IAudioClock;
            Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out var co));
            clock = (IAudioClock)co;
            clock.GetFrequency(out ulong clockFreq);

            var pcm = new short[bufFrames * 2];
            long written = 0;
            Marshal.ThrowExceptionForHR(client.Start());
            LastError = null;
            Log.Info($"Playback on {WindowsAudio.FriendlyName(dev)} (buffer {bufFrames * 1000 / 44100} ms, latency {latency100ns / 10000.0:0.0} ms)");

            while (_run)
            {
                Thread.Sleep(5);
                int hr = client.GetCurrentPadding(out uint padding);
                if (hr != 0) throw new COMException("Output device changed", hr);
                int free = (int)(bufFrames - padding);
                if (free < 441) continue; // top up in >= 10 ms chunks

                // When will the first sample we write now be heard?
                long heardAtNs;
                if (clock.GetPosition(out ulong pos, out ulong qpc) == 0 && clockFreq > 0)
                {
                    // pos (in clock units) was being played at qpc time; written frames follow on from it
                    double playedFrames = pos * 44100.0 / clockFreq;
                    long qpcNs = QpcToMonoNs((long)qpc);
                    heardAtNs = qpcNs + (long)((written - playedFrames) / 44100.0 * 1e9);
                }
                else heardAtNs = MonoClock.NowNs + (long)(padding / 44100.0 * 1e9);

                _player.Read(heardAtNs, pcm, free);
                Marshal.ThrowExceptionForHR(render.GetBuffer((uint)free, out IntPtr data));
                Marshal.Copy(pcm, 0, data, free * 2);
                Marshal.ThrowExceptionForHR(render.ReleaseBuffer((uint)free, 0));
                written += free;
            }
            client.Stop();
        }
        finally
        {
            if (fmtPtr != IntPtr.Zero) Marshal.FreeHGlobal(fmtPtr);
            if (clock != null) Marshal.ReleaseComObject(clock);
            if (render != null) Marshal.ReleaseComObject(render);
            if (client != null) Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(dev);
        }
    }

    /// <summary>Converts a QPC timestamp from IAudioClock (100 ns units) to our timeline.</summary>
    private static long QpcToMonoNs(long qpc100ns)
    {
        // MonoClock and QPC are both based on the performance counter; IAudioClock reports QPC in 100 ns units
        long nowQpc100 = (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e7 / System.Diagnostics.Stopwatch.Frequency));
        return MonoClock.NowNs - (nowQpc100 - qpc100ns) * 100;
    }

    public void Dispose() => Stop();
}

[ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
}

[ComImport, Guid("CD63314F-3FBA-4a1b-812C-EF96358728E7"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClock
{
    [PreserveSig] int GetFrequency(out ulong frequency);
    [PreserveSig] int GetPosition(out ulong position, out ulong qpcPosition);
    [PreserveSig] int GetCharacteristics(out uint characteristics);
}
