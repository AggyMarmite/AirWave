using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Audio.Windows;

/// <summary>
/// Test helper: records what the default output device plays (WASAPI loopback) with the exact time each
/// buffer was played, as Stopwatch seconds (the same clock in every process). Used to check playback timing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LoopbackRecorder : IDisposable
{
    private Thread _t;
    private volatile bool _run;
    /// <summary>(time of first frame in Stopwatch seconds, left-channel samples -1..1, sample rate)</summary>
    public event Action<double, float[], int> Audio;
    public string DeviceName { get; private set; }

    public void Start()
    {
        _run = true;
        _t = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.Highest };
        _t.SetApartmentState(ApartmentState.MTA);
        _t.Start();
    }

    private void Loop()
    {
        var dev = WindowsAudio.GetDevice(null);
        DeviceName = WindowsAudio.FriendlyName(dev);
        var iid = CoreAudioIds.IID_IAudioClient;
        Marshal.ThrowExceptionForHR(dev.Activate(ref iid, 23, IntPtr.Zero, out var o));
        var client = (IAudioClient)o;
        Marshal.ThrowExceptionForHR(client.GetMixFormat(out IntPtr fmtPtr));
        var fmt = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
        bool isFloat = fmt.wFormatTag == 3 || (fmt.wFormatTag == 0xFFFE && Marshal.PtrToStructure<WaveFormatExtensible>(fmtPtr).SubFormat == new Guid("00000003-0000-0010-8000-00aa00389b71"));
        int ch = fmt.nChannels, bits = fmt.wBitsPerSample, rate = (int)fmt.nSamplesPerSec;
        Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000 /* LOOPBACK */, 1_000_000, 0, fmtPtr, IntPtr.Zero));
        var ciid = CoreAudioIds.IID_IAudioCaptureClient;
        Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out var co));
        var cap = (IAudioCaptureClient)co;
        Marshal.ThrowExceptionForHR(client.Start());
        double qpcFreq = 1e7; // IAudioCaptureClient reports QPC positions in 100 ns units
        while (_run)
        {
            Thread.Sleep(3);
            while (cap.GetNextPacketSize(out uint pkt) == 0 && pkt > 0)
            {
                Marshal.ThrowExceptionForHR(cap.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out ulong qpc));
                var left = new float[frames];
                if ((flags & 2) == 0)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        int off = i * ch * bits / 8;
                        left[i] = isFloat ? BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, off))
                                : bits == 16 ? Marshal.ReadInt16(data, off) / 32768f
                                : Marshal.ReadInt32(data, off) / 2147483648f;
                    }
                }
                cap.ReleaseBuffer(frames);
                // Convert the 100 ns QPC position to Stopwatch seconds
                double t = qpc / qpcFreq;
                Audio?.Invoke(t, left, rate);
            }
        }
        client.Stop();
    }

    public void Dispose() { _run = false; _t?.Join(500); }
}
