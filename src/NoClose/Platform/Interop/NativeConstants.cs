namespace NoClose.Platform.Interop;

internal static class NativeConstants
{
    public const int WM_QUERYENDSESSION = 0x0011;
    public const int WM_ENDSESSION = 0x0016;
    public const int WM_SETTINGCHANGE = 0x001A;
    public const int WM_POWERBROADCAST = 0x0218;
    public const int WM_WTSSESSION_CHANGE = 0x02B1;

    public const long ENDSESSION_CLOSEAPP = 0x00000001;
    public const long ENDSESSION_CRITICAL = 0x40000000;
    public const long ENDSESSION_LOGOFF = 0x80000000;

    public const int PBT_APMPOWERSTATUSCHANGE = 0x000A;
    public const int PBT_APMRESUMEAUTOMATIC = 0x0012;
    public const int PBT_APMRESUMESUSPEND = 0x0007;
    public const int PBT_APMSUSPEND = 0x0004;
    public const int PBT_POWERSETTINGCHANGE = 0x8013;

    public const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;

    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_FILE_NOT_FOUND = 2;
    public const uint ERROR_OPERATION_IN_PROGRESS = 329;
    public const int ERROR_CANCELLED = 1223;

    public const uint SPI_GETSCREENSAVETIMEOUT = 0x000E;
    public const uint SPI_GETSCREENSAVEACTIVE = 0x0010;
    public const uint SPI_SETSCREENSAVEACTIVE = 0x0011;
    public const uint SPI_GETSCREENSAVERRUNNING = 0x0072;
    public const uint SPI_GETSCREENSAVESECURE = 0x0076;

    public const int SM_REMOTESESSION = 0x1000;

    public const uint RESTART_NO_CRASH = 1;
    public const uint RESTART_NO_HANG = 2;

    public const uint ATTACH_PARENT_PROCESS = unchecked((uint)-1);

    public const int STD_OUTPUT_HANDLE = -11;
    public const uint FILE_TYPE_UNKNOWN = 0, FILE_TYPE_DISK = 1, FILE_TYPE_CHAR = 2, FILE_TYPE_PIPE = 3;

    public const uint PROCESS_SYNCHRONIZE = 0x00100000;
    public const uint WAIT_OBJECT_0 = 0, WAIT_TIMEOUT = 0x102;
    public const uint ERROR_INVALID_BINDING_RPC = 1702; // RPC_S_INVALID_BINDING: Remote Desktop Services not ready yet

    public static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    public static readonly Guid GUID_CONSOLE_DISPLAY_STATE = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    public static readonly Guid GUID_SESSION_DISPLAY_STATUS = new("2B84C20E-AD23-4DDF-93DB-05FFBD7EFCA5");
    public static readonly Guid GUID_SESSION_USER_PRESENCE = new("3C0F4548-C03F-4C4D-B9F2-237EDE686376");
    public static readonly Guid GUID_ACDC_POWER_SOURCE = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    public static readonly Guid GUID_SYSTEM_AWAYMODE = new("98A7F580-01F7-48AA-9C0F-44352C29E5C0");
    public static readonly Guid GUID_LIDSWITCH_STATE_CHANGE = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");

    public static string WtsEventName(int code) => code switch
    {
        1 => "WTS_CONSOLE_CONNECT",
        2 => "WTS_CONSOLE_DISCONNECT",
        3 => "WTS_REMOTE_CONNECT",
        4 => "WTS_REMOTE_DISCONNECT",
        5 => "WTS_SESSION_LOGON",
        6 => "WTS_SESSION_LOGOFF",
        7 => "WTS_SESSION_LOCK",
        8 => "WTS_SESSION_UNLOCK",
        9 => "WTS_SESSION_REMOTE_CONTROL",
        0xA => "WTS_SESSION_CREATE",
        0xB => "WTS_SESSION_TERMINATE",
        _ => "WTS_" + code
    };
}
