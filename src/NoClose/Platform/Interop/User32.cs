using System.Runtime.InteropServices;

namespace NoClose.Platform.Interop;

[StructLayout(LayoutKind.Sequential)]
internal struct LASTINPUTINFO
{
    public uint cbSize;
    public uint dwTime;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POWERBROADCAST_SETTING
{
    public Guid PowerSetting;
    public uint DataLength;
    // byte Data[1] follows at offset 20
}

internal static partial class User32
{
    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfoBool(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShutdownBlockReasonDestroy(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr RegisterPowerSettingNotification(IntPtr hRecipient, in Guid powerSettingGuid, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterPowerSettingNotification(IntPtr handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr RegisterSuspendResumeNotification(IntPtr hRecipient, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterSuspendResumeNotification(IntPtr handle);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int nIndex);

    /// <summary>Diagnostics / logging only.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetLastInputInfo(ref LASTINPUTINFO plii);
}
