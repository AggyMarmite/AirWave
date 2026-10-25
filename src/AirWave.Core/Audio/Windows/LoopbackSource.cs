using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Audio.Windows;

/// <summary>
/// Captures everything playing on a Windows output device (WASAPI loopback) and feeds it to the
/// streamer as 44.1 kHz stereo. Follows the default device if no specific device is chosen,
/// and quietly reconnects if the device changes or disappears.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class LoopbackSource : CapturedSource
{
    private const int AUDCLNT_SHAREMODE_SHARED = 0;
    private const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    private const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    private const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    private const int CLSCTX_ALL = 23;
    private static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00aa00389b71");
    private static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new("00000001-0000-0010-8000-00aa00389b71");

    private Thread _thread;
    private volatile bool _run;
    public string DeviceId { get; set; }  // null = follow the default output
    public string CurrentDeviceName { get; private set; }
    public string LastError { get; private set; }

    /// <summary>
    /// Capture apps' sound before the device's volume and mute (so muting the PC doesn't silence the speakers), when
    /// following the default output on Windows 10 2004 or later. Falls back to capturing the device.
    /// </summary>
    public bool PreferAppCapture { get; set; } = true;
    private bool _appCaptureFailed, _appStarted;

    /// <summary>True while capturing apps directly (unaffected by the PC's mute).</summary>
    public bool CapturingApps { get; private set; }

    public override void Start()
    {
        if (_run) return;
        _run = true;
        Ring.Clear();
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "AirWave capture", Priority = ThreadPriority.AboveNormal };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public override void Stop()
    {
        _run = false;
        _thread?.Join(1000);
        _thread = null;
    }

    /// <summary>Restart capture (e.g. after the user picks a different device).</summary>
    public void Restart()
    {
        Stop();
        Start();
    }

    private void CaptureLoop()
    {
        while (_run)
        {
            try
            {
                CaptureSession();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Log.Warn($"Capture: {ex.Message} – retrying");
            }
            for (int i = 0; i < 10 && _run; i++) Thread.Sleep(100);
        }
    }

    private void CaptureSession()
    {
        if (PreferAppCapture && string.IsNullOrEmpty(DeviceId) && ProcessLoopback.Supported && !_appCaptureFailed)
        {
            _appStarted = false;
            try { AppCaptureSession(); return; }
            catch (Exception) when (_appStarted)
            {
                CapturingApps = false;
                throw; // was working: retried after a moment
            }
            catch (Exception ex)
            {
                if (!_run) return;
                _appCaptureFailed = true; // couldn't start at all: device capture from now on
                CapturingApps = false;
                Log.Warn($"Capturing apps directly isn't available ({ex.Message}); capturing the output device instead");
            }
        }
        DeviceCaptureSession();
    }

    /// <summary>Process loopback: every app but AirWave, 44.1 kHz 16-bit stereo, unaffected by the device's mute.</summary>
    private void AppCaptureSession()
    {
        IAudioClient client = null;
        IAudioCaptureClient capture = null;
        IntPtr fmtPtr = IntPtr.Zero;
        using var ready = new AutoResetEvent(false);
        bool started = false;
        try
        {
            client = ProcessLoopback.Activate();
            const int rate = 44100, channels = 2, bits = 16;
            var fmt = new WaveFormatEx
            {
                wFormatTag = 1, nChannels = channels, nSamplesPerSec = rate, wBitsPerSample = bits,
                nBlockAlign = channels * bits / 8, nAvgBytesPerSec = rate * channels * bits / 8, cbSize = 0,
            };
            fmtPtr = Marshal.AllocCoTaskMem(Marshal.SizeOf<WaveFormatEx>());
            Marshal.StructureToPtr(fmt, fmtPtr, false);
            Marshal.ThrowExceptionForHR(client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                1_000_000, 0, fmtPtr, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.SetEventHandle(ready.SafeWaitHandle.DangerousGetHandle()));
            var ciid = CoreAudioIds.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out var co));
            capture = (IAudioCaptureClient)co;
            Resampler.Configure(rate);
            Marshal.ThrowExceptionForHR(client.Start());
            started = _appStarted = true;
            LastError = null;
            CurrentDeviceName = "All apps";
            CapturingApps = true;
            Log.Info("Capturing all apps' sound (unaffected by muting this PC)");

            var floats = new float[48000 * 8];
            long lastData = Environment.TickCount64;
            while (_run)
            {
                ready.WaitOne(10);
                int hr;
                while ((hr = capture.GetNextPacketSize(out uint packet)) == 0 && packet > 0)
                {
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int n = (int)frames * channels;
                    if (n > floats.Length) floats = new float[n];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(floats, 0, n);
                    else Convert(data, n, false, bits, floats);
                    capture.ReleaseBuffer(frames);
                    Resampler.Process(floats.AsSpan(0, n), channels, Ring);
                    lastData = Environment.TickCount64;
                }
                if (hr != 0) Marshal.ThrowExceptionForHR(hr);
                long now = Environment.TickCount64;
                if (now - lastData > 80)
                {
                    int sn = Math.Min((int)((now - lastData) * rate / 1000) * channels, floats.Length);
                    if (sn > 0)
                    {
                        Array.Clear(floats, 0, sn);
                        Resampler.Process(floats.AsSpan(0, sn), channels, Ring);
                        lastData = now;
                    }
                }
            }
        }
        finally
        {
            if (started) try { client.Stop(); } catch { }
            if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
        }
    }

    private void DeviceCaptureSession()
    {
        var dev = WindowsAudio.GetDevice(DeviceId);
        IAudioClient client = null;
        IAudioCaptureClient capture = null;
        IntPtr fmtPtr = IntPtr.Zero;
        string followingId = null;
        try
        {
            dev.GetId(out followingId);
            CurrentDeviceName = WindowsAudio.FriendlyName(dev);
            var iid = CoreAudioIds.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o));
            client = (IAudioClient)o;
            Marshal.ThrowExceptionForHR(client.GetMixFormat(out fmtPtr));
            var fmt = Marshal.PtrToStructure<WaveFormatEx>(fmtPtr);
            bool isFloat = fmt.wFormatTag == 3;
            int bits = fmt.wBitsPerSample;
            if (fmt.wFormatTag == 0xFFFE)
            {
                var ext = Marshal.PtrToStructure<WaveFormatExtensible>(fmtPtr);
                isFloat = ext.SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
                if (!isFloat && ext.SubFormat != KSDATAFORMAT_SUBTYPE_PCM) throw new NotSupportedException("Unsupported sound format");
            }
            int channels = fmt.nChannels;
            int rate = (int)fmt.nSamplesPerSec;

            // 100 ms shared-mode buffer, polled every 5 ms
            Marshal.ThrowExceptionForHR(client.Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_LOOPBACK, 1_000_000, 0, fmtPtr, IntPtr.Zero));
            var ciid = CoreAudioIds.IID_IAudioCaptureClient;
            Marshal.ThrowExceptionForHR(client.GetService(ref ciid, out var co));
            capture = (IAudioCaptureClient)co;
            Resampler.Configure(rate);
            Marshal.ThrowExceptionForHR(client.Start());
            LastError = null;
            Log.Info($"Capturing \"{CurrentDeviceName}\" ({rate} Hz, {channels} ch, {(isFloat ? "float" : bits + "-bit")})");

            var floats = new float[48000 * 8];
            long lastData = Environment.TickCount64;
            long lastCheck = Environment.TickCount64;
            while (_run)
            {
                Thread.Sleep(5);
                int hr;
                while ((hr = capture.GetNextPacketSize(out uint packet)) == 0 && packet > 0)
                {
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out IntPtr data, out uint frames, out uint flags, out _, out _));
                    int n = (int)frames * channels;
                    if (n > floats.Length) floats = new float[n];
                    if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0) Array.Clear(floats, 0, n);
                    else Convert(data, n, isFloat, bits, floats);
                    capture.ReleaseBuffer(frames);
                    Resampler.Process(floats.AsSpan(0, n), channels, Ring);
                    lastData = Environment.TickCount64;
                }
                if (hr != 0) Marshal.ThrowExceptionForHR(hr); // device invalidated etc.

                long now = Environment.TickCount64;
                // Loopback delivers nothing while Windows plays nothing: feed silence so the stream keeps its timing
                if (now - lastData > 80)
                {
                    int silentFrames = (int)((now - lastData) * rate / 1000);
                    if (silentFrames > 0)
                    {
                        int sn = Math.Min(silentFrames * channels, floats.Length);
                        Array.Clear(floats, 0, sn);
                        Resampler.Process(floats.AsSpan(0, sn), channels, Ring);
                        lastData = now;
                    }
                }
                // Following the default device? Switch when the user changes it in Windows.
                if (string.IsNullOrEmpty(DeviceId) && now - lastCheck > 2000)
                {
                    lastCheck = now;
                    var cur = WindowsAudio.GetDevice(null);
                    cur.GetId(out var curId);
                    Marshal.ReleaseComObject(cur);
                    if (curId != followingId) { Log.Info("Default output changed, switching capture"); break; }
                }
            }
            client.Stop();
        }
        finally
        {
            if (fmtPtr != IntPtr.Zero) Marshal.FreeCoTaskMem(fmtPtr);
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            Marshal.ReleaseComObject(dev);
        }
    }

    private static unsafe void Convert(IntPtr data, int n, bool isFloat, int bits, float[] dst)
    {
        if (isFloat && bits == 32)
        {
            Marshal.Copy(data, dst, 0, n);
        }
        else if (bits == 16)
        {
            short* p = (short*)data;
            for (int i = 0; i < n; i++) dst[i] = p[i] / 32768f;
        }
        else if (bits == 24)
        {
            byte* p = (byte*)data;
            for (int i = 0; i < n; i++) dst[i] = ((p[3 * i] << 8 | p[3 * i + 1] << 16 | p[3 * i + 2] << 24) >> 8) / 8388608f;
        }
        else if (bits == 32)
        {
            int* p = (int*)data;
            for (int i = 0; i < n; i++) dst[i] = p[i] / 2147483648f;
        }
        else Array.Clear(dst, 0, n);
    }
}
