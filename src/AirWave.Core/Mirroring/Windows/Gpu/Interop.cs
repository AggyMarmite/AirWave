using System.Runtime.InteropServices;
using AirWave.Core.Audio;

namespace AirWave.Core.Mirroring.Windows.Gpu;

// Minimal DXGI / Direct3D 11 / Media Foundation bindings for GPU screen capture and encoding.
// Unused vtable slots are placeholders so the used methods land on the right entries.

#region DXGI

[ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIFactory1
{
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    void EnumAdapters(); void MakeWindowAssociation(); void GetWindowAssociation(); void CreateSwapChain(); void CreateSoftwareAdapter();
    [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
    void IsCurrent();
}

[ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIAdapter1
{
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    [PreserveSig] int EnumOutputs(uint index, out IDXGIOutput output);
    void GetDesc(); void CheckInterfaceSupport();
    [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 desc);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_ADAPTER_DESC1
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    public uint VendorId, DeviceId, SubSysId, Revision;
    public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
    public uint LuidLow; public int LuidHigh;
    public uint Flags;
}

[ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput
{
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    [PreserveSig] int GetDesc(out DXGI_OUTPUT_DESC desc);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DXGI_OUTPUT_DESC
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
    public int Left, Top, Right, Bottom;
    public int AttachedToDesktop;
    public int Rotation;
    public IntPtr Monitor;
}

[ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutput1
{
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    void GetDesc(); void GetDisplayModeList(); void FindClosestMatchingMode(); void WaitForVBlank(); void TakeOwnership(); void ReleaseOwnership();
    void GetGammaControlCapabilities(); void SetGammaControl(); void GetGammaControl(); void SetDisplaySurface(); void GetDisplaySurfaceData(); void GetFrameStatistics();
    void GetDisplayModeList1(); void FindClosestMatchingMode1(); void GetDisplaySurfaceData1();
    [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
}

[ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGIOutputDuplication
{
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    void GetDesc();
    [PreserveSig] int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO info, [MarshalAs(UnmanagedType.IUnknown)] out object resource);
    void GetFrameDirtyRects(); void GetFrameMoveRects(); void GetFramePointerShape(); void MapDesktopSurface(); void UnMapDesktopSurface();
    [PreserveSig] int ReleaseFrame();
}

[StructLayout(LayoutKind.Sequential)]
internal struct DXGI_OUTDUPL_FRAME_INFO
{
    public long LastPresentTime;
    public long LastMouseUpdateTime;
    public uint AccumulatedFrames;
    public int RectsCoalesced;
    public int ProtectedContentMaskedOut;
    public int PointerX, PointerY;
    public int PointerVisible;
    public uint TotalMetadataBufferSize;
    public uint PointerShapeBufferSize;
}

[ComImport, Guid("4AE63092-6327-4c1b-80AE-BFE12EA32B86"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDXGISurface1
{
    void SetPrivateData(); void SetPrivateDataInterface(); void GetPrivateData(); void GetParent();
    void GetDevice();
    void GetDesc(); void Map(); void Unmap();
    [PreserveSig] int GetDC(int discard, out IntPtr hdc);
    [PreserveSig] int ReleaseDC(IntPtr dirtyRect);
}

#endregion

#region Direct3D 11

[ComImport, Guid("db6f6ddb-ac77-4e88-8253-819df9bbf140"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Device
{
    void CreateBuffer(); void CreateTexture1D();
    [PreserveSig] int CreateTexture2D(in D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out ID3D11Texture2D texture);
}

[ComImport, Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D11Texture2D
{
    void GetDevice(); void GetPrivateData(); void SetPrivateData(); void SetPrivateDataInterface();
    void GetResourceType(); void SetEvictionPriority(); void GetEvictionPriority();
    [PreserveSig] void GetDesc(out D3D11_TEXTURE2D_DESC desc);
}

[ComImport, Guid("9B7E4E00-342C-4106-A19F-4F2704F689F0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ID3D10Multithread
{
    void Enter(); void Leave();
    [PreserveSig] int SetMultithreadProtected(int protect);
    [PreserveSig] int GetMultithreadProtected();
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_TEXTURE2D_DESC
{
    public uint Width, Height, MipLevels, ArraySize;
    public int Format;
    public uint SampleCount, SampleQuality;
    public int Usage;
    public uint BindFlags, CPUAccessFlags, MiscFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct D3D11_MAPPED_SUBRESOURCE
{
    public IntPtr Data;
    public uint RowPitch, DepthPitch;
}

/// <summary>ID3D11DeviceContext, called through its vtable (it's large and only three methods are needed).</summary>
internal sealed unsafe class D3DContext
{
    public readonly IntPtr Ptr;
    public D3DContext(IntPtr ptr) { Ptr = ptr; }
    private IntPtr Fn(int i) => (*(IntPtr**)Ptr)[i];

    public int Map(IntPtr resource, uint sub, int mapType, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped)
    {
        fixed (D3D11_MAPPED_SUBRESOURCE* m = &mapped)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int, uint, D3D11_MAPPED_SUBRESOURCE*, int>)Fn(14))(Ptr, resource, sub, mapType, flags, m);
    }

    public void Unmap(IntPtr resource, uint sub) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Fn(15))(Ptr, resource, sub);

    public void CopyResource(IntPtr dst, IntPtr src) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Fn(47))(Ptr, dst, src);
}

internal static class D3D
{
    public const int DXGI_FORMAT_B8G8R8A8_UNORM = 87, DXGI_FORMAT_NV12 = 103;
    public const uint BIND_SHADER_RESOURCE = 0x8, BIND_RENDER_TARGET = 0x20;
    public const uint MISC_GDI_COMPATIBLE = 0x200;
    public const int USAGE_DEFAULT = 0, USAGE_STAGING = 3;
    public const uint CPU_ACCESS_READ = 0x20000;

    [DllImport("d3d11.dll")]
    public static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        [In] int[] featureLevels, uint numLevels, uint sdkVersion, out IntPtr device, out int level, out IntPtr context);

    [DllImport("dxgi.dll")]
    public static extern int CreateDXGIFactory1(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object factory);

    public static IntPtr Unknown(object o) => Marshal.GetIUnknownForObject(o);
    public static IntPtr As(object o, Type iface) => Marshal.GetComInterfaceForObject(o, iface);
}

#endregion

#region Media Foundation additions

[ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFAttributes
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare();
    [PreserveSig] int GetUINT32(in Guid key, out uint value);
    void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString();
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
    void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    [PreserveSig] int SetUINT32(in Guid key, uint value);
}

[ComImport, Guid("7FEE9E9A-4A89-47a6-899C-B6A53A70FB67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFActivate
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32();
    [PreserveSig] int GetUINT64(in Guid key, out ulong value);
    void GetDouble(); void GetGUID();
    void GetStringLength(); void GetString();
    [PreserveSig] int GetAllocatedString(in Guid key, out IntPtr value, out uint length);
    void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    [PreserveSig] int ActivateObject(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    [PreserveSig] int ShutdownObject();
    void DetachObject();
}

[ComImport, Guid("2CD0BD52-BCD5-4B89-B62C-EADC0C031E7D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEventGenerator
{
    [PreserveSig] int GetEvent(uint flags, out IMFMediaEvent ev);
    void BeginGetEvent(); void EndGetEvent(); void QueueEvent();
}

[ComImport, Guid("DF598932-F10C-4E39-BBA2-C308F101DAA3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaEvent
{
    void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
    void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
    void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    [PreserveSig] int GetType(out uint type);
    void GetExtendedType();
    [PreserveSig] int GetStatus(out int status);
}

[ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFDXGIDeviceManager
{
    void CloseDeviceHandle(); void GetVideoService(); void LockDevice(); void OpenDeviceHandle();
    [PreserveSig] int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object device, uint resetToken);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MFT_REGISTER_TYPE_INFO { public Guid MajorType; public Guid SubType; }

internal static class MfGpu
{
    public static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    public static readonly Guid CLSID_VideoProcessorMFT = new("88753B26-5B24-49BD-B2E7-0C445C78C982");
    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_NV12 = new("3231564E-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00AA00389B71");
    public static readonly Guid MFT_FRIENDLY_NAME = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    public static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    public static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MFT_ENUM_ADAPTER_LUID = new("1d39518c-e220-4da8-a07f-ba172552d6b1");
    public static readonly Guid MF_SA_D3D11_AWARE = new("206b4fc8-fcf9-4c51-afe3-9764369e33a0");

    public const uint MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    public const uint MFT_MESSAGE_SET_D3D_MANAGER = 2;
    public const uint METransformNeedInput = 601, METransformHaveOutput = 602;

    [DllImport("mfplat.dll")]
    public static extern int MFTEnumEx(Guid category, uint flags, in MFT_REGISTER_TYPE_INFO input, in MFT_REGISTER_TYPE_INFO output, out IntPtr activates, out uint count);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGIDeviceManager(out uint resetToken, out IMFDXGIDeviceManager manager);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateDXGISurfaceBuffer(in Guid riid, [MarshalAs(UnmanagedType.IUnknown)] object surface, uint subresource, int bottomUp, out AirWave.Core.Audio.IMFMediaBuffer buffer);

    public static string FriendlyName(IMFActivate a)
    {
        if (a.GetAllocatedString(MFT_FRIENDLY_NAME, out var p, out _) != 0 || p == IntPtr.Zero) return "Graphics card encoder";
        var s = Marshal.PtrToStringUni(p);
        Marshal.FreeCoTaskMem(p);
        return s;
    }
}

#endregion
