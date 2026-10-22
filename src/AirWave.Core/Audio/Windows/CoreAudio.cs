using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AirWave.Core.Audio.Windows;

// Minimal Windows Core Audio (WASAPI / MMDevice / endpoint volume) COM interop.
// Only what AirWave needs: enumerate output devices, loopback-capture one, and mute it.

[SupportedOSPlatform("windows")]
internal static class CoreAudioIds
{
    public static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly PropertyKey PKEY_Device_FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid fmtid;
    public int pid;
    public PropertyKey(Guid f, int p) { fmtid = f; pid = p; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort vt;
    public ushort r1, r2, r3;
    public IntPtr p;
    public IntPtr p2;
}

internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IMMDeviceCollection devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
}

[ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int Item(int index, out IMMDevice device);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
    [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore props);
    [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetState(out int state);
}

[ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig] int GetCount(out int count);
    [PreserveSig] int GetAt(int index, out PropertyKey key);
    [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
    [PreserveSig] int Commit();
}

[ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioClient
{
    [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
    [PreserveSig] int GetBufferSize(out uint frames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint padding);
    [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
    [PreserveSig] int GetMixFormat(out IntPtr format);
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr handle);
    [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
}

[ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioCaptureClient
{
    [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
    [PreserveSig] int ReleaseBuffer(uint frames);
    [PreserveSig] int GetNextPacketSize(out uint frames);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
    [PreserveSig] int GetChannelCount(out uint count);
    [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid ctx);
    [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid ctx);
    [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
    [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
    [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid ctx);
    [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid ctx);
    [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
    [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
    [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
    [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatEx
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WaveFormatExtensible
{
    public WaveFormatEx Format;
    public ushort wValidBitsPerSample;
    public uint dwChannelMask;
    public Guid SubFormat;
}

/// <summary>An audio output (speakers/headphones) on this PC.</summary>
public sealed record OutputDevice(string Id, string Name, bool IsDefault);

[SupportedOSPlatform("windows")]
public static class WindowsAudio
{
    private const int DEVICE_STATE_ACTIVE = 1;
    private const int CLSCTX_ALL = 23;

    internal static IMMDeviceEnumerator Enumerator()
    {
        var t = Type.GetTypeFromCLSID(CoreAudioIds.CLSID_MMDeviceEnumerator);
        return (IMMDeviceEnumerator)Activator.CreateInstance(t);
    }

    public static List<OutputDevice> ListOutputs()
    {
        var list = new List<OutputDevice>();
        var en = Enumerator();
        string defId = null;
        if (en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var def) == 0) { def.GetId(out defId); Marshal.ReleaseComObject(def); }
        if (en.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var col) != 0) return list;
        col.GetCount(out int n);
        for (int i = 0; i < n; i++)
        {
            if (col.Item(i, out var dev) != 0) continue;
            dev.GetId(out var id);
            list.Add(new OutputDevice(id, FriendlyName(dev) ?? "Speakers", id == defId));
            Marshal.ReleaseComObject(dev);
        }
        Marshal.ReleaseComObject(col);
        Marshal.ReleaseComObject(en);
        return list;
    }

    internal static string FriendlyName(IMMDevice dev)
    {
        if (dev.OpenPropertyStore(0, out var ps) != 0) return null;
        try
        {
            var key = CoreAudioIds.PKEY_Device_FriendlyName;
            if (ps.GetValue(ref key, out var pv) != 0) return null;
            string s = pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; // VT_LPWSTR
            PropVariantClear(ref pv);
            return s;
        }
        finally { Marshal.ReleaseComObject(ps); }
    }

    /// <summary>Gets an output device by id, or the default output when id is null/empty or gone.</summary>
    internal static IMMDevice GetDevice(string id)
    {
        var en = Enumerator();
        try
        {
            if (!string.IsNullOrEmpty(id) && en.GetDevice(id, out var d) == 0) return d;
            Marshal.ThrowExceptionForHR(en.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var def));
            return def;
        }
        finally { Marshal.ReleaseComObject(en); }
    }

    /// <summary>Mutes or unmutes an output. Loopback capture still receives the audio while it's muted.</summary>
    public static bool? SetMute(string deviceId, bool mute)
    {
        try
        {
            var dev = GetDevice(deviceId);
            var iid = CoreAudioIds.IID_IAudioEndpointVolume;
            Marshal.ThrowExceptionForHR(dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var o));
            var vol = (IAudioEndpointVolume)o;
            vol.GetMute(out bool was);
            var ctx = Guid.Empty;
            vol.SetMute(mute, ref ctx);
            Marshal.ReleaseComObject(vol);
            Marshal.ReleaseComObject(dev);
            return was;
        }
        catch (Exception ex)
        {
            Log.Warn($"Couldn't change mute: {ex.Message}");
            return null;
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pv);
}
