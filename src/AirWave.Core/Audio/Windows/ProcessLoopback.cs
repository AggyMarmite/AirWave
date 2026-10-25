using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Audio.Windows;

/// <summary>
/// Windows 10 (2004+) "process loopback": captures what every app is playing, except AirWave itself, before the
/// output device's volume and mute are applied. So the PC's own speakers can be muted while the AirPlay speakers
/// still get the sound (plain device loopback goes silent on many PCs when the device is muted).
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ProcessLoopback
{
    private const string VirtualDevice = "VAD\\Process_Loopback";
    private const ushort VT_BLOB = 65;
    private const int ActivationTypeProcessLoopback = 1;
    private const int ExcludeTargetProcessTree = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public int ActivationType;
        public uint TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlobPropVariant
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public uint cbSize;
        public IntPtr pBlobData;
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceAsyncOperation
    {
        [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IActivateAudioInterfaceCompletionHandler
    {
        [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation);
    }

    // Marker: the callback may be called on any thread
    [ComImport, Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAgileObject { }

    [ClassInterface(ClassInterfaceType.None)]
    private sealed class Completion : IActivateAudioInterfaceCompletionHandler, IAgileObject
    {
        public readonly ManualResetEventSlim Done = new();
        public int Hr = -1;
        public object Client;
        public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation op)
        {
            try
            {
                int hr = op.GetActivateResult(out int result, out object iface);
                Hr = hr != 0 ? hr : result;
                if (Hr == 0) Client = iface;
            }
            catch (Exception ex) { Hr = ex.HResult; }
            finally { Done.Set(); }
            return 0;
        }
    }

    [DllImport("Mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    private static extern int ActivateAudioInterfaceAsync([MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid, IntPtr activationParams, IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation operation);

    /// <summary>True on Windows versions that have process loopback (Windows 10 2004, build 19041, and later).</summary>
    public static bool Supported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041);

    /// <summary>An audio client capturing every process except this one (and its children). Throws if unavailable.</summary>
    public static IAudioClient Activate()
    {
        var p = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)Environment.ProcessId,
            ProcessLoopbackMode = ExcludeTargetProcessTree,
        };
        IntPtr pParams = Marshal.AllocHGlobal(Marshal.SizeOf<ActivationParams>());
        IntPtr pVar = Marshal.AllocHGlobal(Marshal.SizeOf<BlobPropVariant>());
        try
        {
            Marshal.StructureToPtr(p, pParams, false);
            Marshal.StructureToPtr(new BlobPropVariant { vt = VT_BLOB, cbSize = (uint)Marshal.SizeOf<ActivationParams>(), pBlobData = pParams }, pVar, false);
            var done = new Completion();
            var iid = CoreAudioIds.IID_IAudioClient;
            Marshal.ThrowExceptionForHR(ActivateAudioInterfaceAsync(VirtualDevice, ref iid, pVar, done, out var op));
            if (!done.Done.Wait(5000)) throw new TimeoutException("Windows didn't start app capture");
            GC.KeepAlive(op);
            Marshal.ThrowExceptionForHR(done.Hr);
            return (IAudioClient)done.Client;
        }
        finally
        {
            Marshal.FreeHGlobal(pVar);
            Marshal.FreeHGlobal(pParams);
        }
    }
}
