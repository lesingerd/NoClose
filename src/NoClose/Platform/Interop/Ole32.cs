using System.Runtime.InteropServices;

namespace NoClose.Platform.Interop;

internal static partial class Ole32
{
    internal const uint COINIT_MULTITHREADED = 0x0;
    internal const uint CLSCTX_ALL = 0x17; // INPROC_SERVER | INPROC_HANDLER | LOCAL_SERVER | REMOTE_SERVER
    internal const int RPC_E_CHANGED_MODE = unchecked((int)0x80010106);

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();

    [LibraryImport("ole32.dll")]
    internal static partial void CoTaskMemFree(IntPtr pv);

    [LibraryImport("ole32.dll")]
    internal static partial int CoCreateInstance(in Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, in Guid riid, out IntPtr ppv);
}
