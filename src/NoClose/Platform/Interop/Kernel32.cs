using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NoClose.Platform.Interop;

[Flags]
internal enum EXECUTION_STATE : uint
{
    ES_SYSTEM_REQUIRED = 0x00000001,
    ES_DISPLAY_REQUIRED = 0x00000002,
    ES_AWAYMODE_REQUIRED = 0x00000040,
    ES_CONTINUOUS = 0x80000000
}
// The "user present" flag is intentionally not defined: NoClose must not pretend that the user is active.

internal enum POWER_REQUEST_TYPE
{
    PowerRequestDisplayRequired = 0,
    PowerRequestSystemRequired = 1,
    PowerRequestAwayModeRequired = 2,
    PowerRequestExecutionRequired = 3
}

[StructLayout(LayoutKind.Sequential)]
internal struct REASON_DETAILED
{
    public IntPtr LocalizedReasonModule;
    public uint LocalizedReasonId;
    public uint ReasonStringCount;
    public IntPtr ReasonStrings;
}

[StructLayout(LayoutKind.Explicit)]
internal struct REASON_UNION
{
    [FieldOffset(0)] public REASON_DETAILED Detailed;
    [FieldOffset(0)] public IntPtr SimpleReasonString;
}

[StructLayout(LayoutKind.Sequential)]
internal struct REASON_CONTEXT
{
    public uint Version;   // POWER_REQUEST_CONTEXT_VERSION = 0
    public uint Flags;     // POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1
    public REASON_UNION Reason;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
    public uint BatteryLifeTime, BatteryFullLifeTime;
}

/// <summary>Owns a power request handle; closing it also removes the request.</summary>
internal sealed class SafePowerRequestHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePowerRequestHandle() : base(true) { }

    public SafePowerRequestHandle(IntPtr existing) : base(true) => SetHandle(existing);

    protected override bool ReleaseHandle() => Kernel32.CloseHandle(handle);
}

internal static partial class Kernel32
{
    // No SetLastError: SetThreadExecutionState is documented to return NULL on failure and does not document a last error.
    [LibraryImport("kernel32.dll")]
    internal static partial EXECUTION_STATE SetThreadExecutionState(EXECUTION_STATE esFlags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr PowerCreateRequest(in REASON_CONTEXT context); // INVALID_HANDLE_VALUE (-1) on failure

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerSetRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PowerClearRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegisterApplicationRestart(string? pwzCommandline, uint dwFlags); // HRESULT

    [LibraryImport("kernel32.dll")]
    internal static partial int UnregisterApplicationRestart();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachConsole(uint dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint pid, out uint sessionId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WTSGetActiveConsoleSessionId();

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetFileType(IntPtr hFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [LibraryImport("kernel32.dll")]
    internal static partial uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll")]
    internal static partial IntPtr LocalFree(IntPtr hMem);
}
