using System.Runtime.InteropServices;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

/// <summary>Logging/diagnostics only: how long ago the last user input in this session was.</summary>
internal static unsafe class SessionIdleProbe
{
    public static TimeSpan? LastInputAge()
    {
        // Preferred: WTSINFO.LastInputTime for this session (0 / unknown on many console sessions).
        try
        {
            if (Wtsapi32.WTSQuerySessionInformation(IntPtr.Zero, Wtsapi32.WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSSessionInfo, out var p, out var n)
                && p != IntPtr.Zero)
            {
                try
                {
                    if (n >= (uint)sizeof(WTSINFOW))
                    {
                        var info = *(WTSINFOW*)p;
                        if (info.LastInputTime > 0 && info.CurrentTime >= info.LastInputTime)
                            return TimeSpan.FromTicks(info.CurrentTime - info.LastInputTime);
                    }
                }
                finally { Wtsapi32.WTSFreeMemory(p); }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }

        var lii = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (User32.GetLastInputInfo(ref lii))
        {
            uint now = unchecked((uint)Environment.TickCount);
            return TimeSpan.FromMilliseconds(unchecked(now - lii.dwTime));
        }
        return null;
    }
}
