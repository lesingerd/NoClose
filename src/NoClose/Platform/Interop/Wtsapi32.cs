using System.Runtime.InteropServices;

namespace NoClose.Platform.Interop;

internal enum WTS_INFO_CLASS
{
    WTSConnectState = 8,
    WTSClientProtocolType = 16,
    WTSSessionInfo = 24,
    WTSIsRemoteSession = 29
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WTSINFOW
{
    public int State;
    public uint SessionId;
    public uint IncomingBytes, OutgoingBytes, IncomingFrames, OutgoingFrames, IncomingCompressedBytes, OutgoingCompressedBytes;
    public fixed char WinStationName[32];
    public fixed char Domain[17];
    public fixed char UserName[21];
    public long ConnectTime, DisconnectTime, LastInputTime, LogonTime, CurrentTime;
}

internal static partial class Wtsapi32
{
    internal const uint NOTIFY_FOR_THIS_SESSION = 0;
    internal const uint WTS_CURRENT_SESSION = 0xFFFFFFFF;

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSRegisterSessionNotification(IntPtr hWnd, uint dwFlags);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSUnRegisterSessionNotification(IntPtr hWnd);

    [LibraryImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, WTS_INFO_CLASS infoClass, out IntPtr ppBuffer, out uint pBytesReturned);

    [LibraryImport("wtsapi32.dll")]
    internal static partial void WTSFreeMemory(IntPtr pMemory);
}
