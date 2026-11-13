using System.ComponentModel;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

/// <summary>Reads/changes the session-only screen saver "active" flag with SystemParametersInfo (never persisted to the profile).</summary>
internal class ScreenSaverAccessor
{
    public virtual bool GetActive()
    {
        if (!User32.SystemParametersInfoBool(NativeConstants.SPI_GETSCREENSAVEACTIVE, 0, out int v, 0))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
        return v != 0;
    }

    /// <summary>fWinIni is always 0: the change is not written to the user profile.</summary>
    public virtual void SetActive(bool active)
    {
        if (!User32.SystemParametersInfoW(NativeConstants.SPI_SETSCREENSAVEACTIVE, active ? 1u : 0u, IntPtr.Zero, 0))
            throw new Win32Exception(System.Runtime.InteropServices.Marshal.GetLastPInvokeError());
    }

    public static bool IsOperationInProgress(Win32Exception ex) => ex.NativeErrorCode == (int)NativeConstants.ERROR_OPERATION_IN_PROGRESS;
}
