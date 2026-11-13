using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace NoClose.Platform.Interop;

// Core Audio (MMDevice + WASAPI) COM interfaces, source generated ([GeneratedComInterface]; no runtime reflection, trimming safe).
// Methods are declared in exact vtable order. Every method is [PreserveSig] and returns the raw HRESULT.

internal enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }

internal enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

internal enum AUDCLNT_SHAREMODE { AUDCLNT_SHAREMODE_SHARED = 0, AUDCLNT_SHAREMODE_EXCLUSIVE = 1 }

[StructLayout(LayoutKind.Sequential, Pack = 2)]
internal struct WAVEFORMATEX
{
    public ushort wFormatTag;
    public ushort nChannels;
    public uint nSamplesPerSec;
    public uint nAvgBytesPerSec;
    public ushort nBlockAlign;
    public ushort wBitsPerSample;
    public ushort cbSize;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PROPERTYKEY
{
    public Guid fmtid;
    public uint pid;
}

internal static class CoreAudioConstants
{
    internal static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    internal static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    internal static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2");
    internal static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    internal static readonly Guid IID_IAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");

    internal const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    internal const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    internal const uint DEVICE_STATE_ACTIVE = 0x1;
    internal const long ReferenceTimeMs = 10_000; // REFERENCE_TIME is in 100 ns units
    internal const long BufferDuration = 200 * ReferenceTimeMs;
}

[GeneratedComInterface]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
internal partial interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IntPtr endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[GeneratedComInterface]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
internal partial interface IMMDevice
{
    [PreserveSig] int Activate(in Guid iid, uint clsCtx, IntPtr activationParams, out IntPtr iface);
    [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
    [PreserveSig] int GetId(out IntPtr id); // CoTaskMemFree
    [PreserveSig] int GetState(out uint state);
}

[GeneratedComInterface]
[Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0")]
internal partial interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, uint newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(EDataFlow flow, ERole role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string deviceId, PROPERTYKEY key);
}

[GeneratedComInterface]
[Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
internal partial interface IAudioClient
{
    [PreserveSig] int Initialize(AUDCLNT_SHAREMODE shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
    [PreserveSig] int GetBufferSize(out uint numBufferFrames);
    [PreserveSig] int GetStreamLatency(out long latency);
    [PreserveSig] int GetCurrentPadding(out uint numPaddingFrames);
    [PreserveSig] int IsFormatSupported(AUDCLNT_SHAREMODE shareMode, IntPtr format, out IntPtr closestMatch);
    [PreserveSig] int GetMixFormat(out IntPtr deviceFormat); // CoTaskMemFree
    [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
    [PreserveSig] int Start();
    [PreserveSig] int Stop();
    [PreserveSig] int Reset();
    [PreserveSig] int SetEventHandle(IntPtr eventHandle);
    [PreserveSig] int GetService(in Guid riid, out IntPtr service);
}

[GeneratedComInterface]
[Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
internal partial interface IAudioRenderClient
{
    [PreserveSig] int GetBuffer(uint numFramesRequested, out IntPtr data);
    [PreserveSig] int ReleaseBuffer(uint numFramesWritten, uint flags);
}

[GeneratedComInterface]
[Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
internal partial interface IAudioSessionControl
{
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName(out IntPtr name);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetIconPath(out IntPtr path);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, IntPtr eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(in Guid groupingParam, IntPtr eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
    [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
}

/// <summary>Small helpers around the generated COM wrappers.</summary>
internal static unsafe class ComHelper
{
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>Wraps a raw interface pointer in a unique generated RCW (so FinalRelease really releases it) and drops the pointer's own reference.</summary>
    internal static T Wrap<T>(IntPtr ptr) where T : class
    {
        if (ptr == IntPtr.Zero) throw new COMException("Null interface pointer.");
        try
        {
            return Wrappers.GetOrCreateObjectForComInstance(ptr, CreateObjectFlags.UniqueInstance) as T
                ?? throw new COMException("Interface could not be wrapped.");
        }
        finally
        {
            Marshal.Release(ptr);
        }
    }

    /// <summary>Releases the native references held by a wrapper created with Wrap. The wrapper must not be used afterwards.</summary>
    internal static void Release(object? com)
    {
        if (com == null) return;
        try
        {
            if (com is ComObject c) c.FinalRelease();
        }
        catch (Exception) { /* releasing must never throw */ }
    }

    internal static string? TakeString(IntPtr p)
    {
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { Ole32.CoTaskMemFree(p); }
    }

    internal static int CreateEnumerator(out IMMDeviceEnumerator? enumerator)
    {
        enumerator = null;
        int hr = Ole32.CoCreateInstance(CoreAudioConstants.CLSID_MMDeviceEnumerator, IntPtr.Zero, Ole32.CLSCTX_ALL,
            CoreAudioConstants.IID_IMMDeviceEnumerator, out var p);
        if (hr < 0) return hr;
        enumerator = Wrap<IMMDeviceEnumerator>(p);
        return 0;
    }
}

/// <summary>COM apartment scope for a thread: joins the MTA and leaves it again if this scope initialized it.</summary>
internal readonly struct MtaScope : IDisposable
{
    private readonly bool _uninit;

    public MtaScope()
    {
        int hr = Ole32.CoInitializeEx(IntPtr.Zero, Ole32.COINIT_MULTITHREADED);
        _uninit = hr >= 0; // S_OK or S_FALSE both need a CoUninitialize; RPC_E_CHANGED_MODE does not
    }

    public void Dispose()
    {
        if (_uninit) Ole32.CoUninitialize();
    }
}
