using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using NoClose.Core.Cli;
using NoClose.Core.Guards;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

internal static class Elevation
{
    private static bool? _isElevated;

    public static bool IsElevated
    {
        get
        {
            if (_isElevated is { } v) return v;
            using var id = WindowsIdentity.GetCurrent();
            return (_isElevated = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator)).Value;
        }
    }

    public static string CurrentUserSid
    {
        get
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? "";
        }
    }

    /// <summary>TokenElevationType of this process (1 default, 2 full, 3 limited); 0 when it cannot be read.</summary>
    public static int TokenElevationType
    {
        get
        {
            try
            {
                using var p = Process.GetCurrentProcess();
                if (!Advapi32.OpenProcessToken(p.Handle, Advapi32.TOKEN_QUERY, out var token)) return 0;
                try
                {
                    return Advapi32.GetTokenInformation(token, Advapi32.TokenElevationType, out var type, sizeof(int), out _) ? type : 0;
                }
                finally { Kernel32.CloseHandle(token); }
            }
            catch (Exception) { return 0; }
        }
    }

    /// <summary>True for an administrator with a filtered token (the same user can elevate through a plain UAC consent prompt).</summary>
    public static bool CanRestartElevated => ElevationRules.CanRestartElevated(IsElevated, TokenElevationType);

    /// <summary>
    /// Starts a new elevated copy of this program (<paramref name="args"/> already carry --handoff &lt;sid&gt;). Returns false if the user
    /// cancelled the UAC prompt (error 1223). The caller is responsible for restoring guards and releasing the single-instance mutex BEFORE
    /// calling this and for re-acquiring them if this returns false.
    /// </summary>
    public static bool TryStartElevated(IEnumerable<string> args)
    {
        var path = Environment.ProcessPath ?? throw new InvalidOperationException("Process path unknown.");
        var psi = new ProcessStartInfo
        {
            FileName = path,
            Arguments = CliParser.Quote(args),
            UseShellExecute = true,
            Verb = "runas",
        };
        try
        {
            Process.Start(psi)?.Dispose();
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NativeConstants.ERROR_CANCELLED)
        {
            return false;
        }
    }
}
