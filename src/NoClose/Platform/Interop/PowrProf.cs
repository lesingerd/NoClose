using System.Runtime.InteropServices;

namespace NoClose.Platform.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct BATTERY_REPORTING_SCALE
{
    public uint Granularity;
    public uint Capacity;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SYSTEM_POWER_CAPABILITIES
{
    public byte PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5,
        HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent, ThermalControl, ProcessorThrottle;
    public byte ProcessorMinThrottle, ProcessorMaxThrottle;
    public byte FastSystemS4;
    public byte Hiberboot;
    public byte WakeAlarmPresent;
    public byte AoAc;
    public byte DiskSpinDown;
    public byte HiberFileType;
    public byte AoAcConnectivitySupported;
    public fixed byte spare3[6];
    public byte SystemBatteriesPresent, BatteriesAreShortTerm;
    public BATTERY_REPORTING_SCALE BatteryScale0, BatteryScale1, BatteryScale2;
    public int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
}

/// <summary>PowrProf functions return Win32 error codes directly (0 = ERROR_SUCCESS); no SetLastError.</summary>
internal static unsafe partial class PowrProf
{
    internal const int SystemExecutionState = 16;

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid); // LocalFree

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerSetActiveScheme(IntPtr userRootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerDuplicateScheme(IntPtr rootPowerKey, in Guid sourceSchemeGuid, ref IntPtr destinationSchemeGuid); // LocalFree out

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerDeleteScheme(IntPtr rootPowerKey, in Guid schemeGuid);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadACValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, out uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadDCValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, out uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteACValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, uint acValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteDCValueIndex(IntPtr root, in Guid scheme, in Guid sub, in Guid setting, uint dcValueIndex);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerReadFriendlyName(IntPtr root, in Guid scheme, IntPtr sub, IntPtr setting, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerWriteFriendlyName(IntPtr root, in Guid scheme, IntPtr sub, IntPtr setting, byte* buffer, uint bufferSize);

    internal const uint AccessScheme = 16;

    [LibraryImport("powrprof.dll")]
    internal static partial uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index, byte* buffer, ref uint bufferSize);

    [LibraryImport("powrprof.dll")]
    internal static partial int CallNtPowerInformation(int informationLevel, IntPtr inputBuffer, uint inputBufferLength, out uint outputBuffer, uint outputBufferLength);

    [LibraryImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    internal static partial bool GetPwrCapabilities(out SYSTEM_POWER_CAPABILITIES caps);
}
