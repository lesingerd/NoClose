namespace NoClose.Core.Guards;

/// <summary>Pure decision logic for WM_QUERYENDSESSION.</summary>
public static class EndSessionDecision
{
    public const long CloseApp = 0x00000001;
    public const long Critical = 0x40000000;
    public const long Logoff = 0x80000000;

    /// <summary>
    /// True if the application should ask Windows to wait (return FALSE from WM_QUERYENDSESSION): the guard is applied
    /// and the request is neither critical nor a Restart Manager servicing request.
    /// </summary>
    public static bool ShouldBlock(bool guardApplied, long lParam)
    {
        if (!guardApplied) return false;
        if ((lParam & Critical) != 0) return false;
        if ((lParam & CloseApp) != 0) return false;
        return true;
    }

    public static string DescribeFlags(long lParam)
    {
        var parts = new List<string>();
        if ((lParam & CloseApp) != 0) parts.Add("ENDSESSION_CLOSEAPP");
        if ((lParam & Critical) != 0) parts.Add("ENDSESSION_CRITICAL");
        if ((lParam & Logoff) != 0) parts.Add("ENDSESSION_LOGOFF");
        return parts.Count == 0 ? "none (shutdown/restart)" : string.Join("|", parts);
    }
}
