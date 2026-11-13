using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using NoClose.Core.Diagnostics;
using NoClose.Platform.Interop;

namespace NoClose.Platform;

/// <summary>The only <see cref="IMachineReader"/> implementation for the real machine. Strictly read-only and thread-safe.</summary>
internal sealed class WindowsMachineReader : IMachineReader
{
    private readonly PowerSchemeAccessor _power;
    private readonly bool _isElevated;

    public WindowsMachineReader(PowerSchemeAccessor power, bool isElevated)
    {
        _power = power;
        _isElevated = isElevated;
    }

    // ---- registry -----------------------------------------------------------------------------------------------

    private static RegistryKey? Open(RegHive hive, string subKey, RegView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(
            hive == RegHive.HKLM ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            view == RegView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64);
        return baseKey.OpenSubKey(subKey, writable: false);
    }

    public object? ReadRegistry(RegHive hive, string subKey, string valueName, RegView view = RegView.Default)
    {
        try
        {
            using var k = Open(hive, subKey, view);
            return k?.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public bool KeyExists(RegHive hive, string subKey)
    {
        try { using var k = Open(hive, subKey, RegView.Default); return k != null; }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    public IReadOnlyList<string> SubKeyNames(RegHive hive, string subKey)
    {
        try { using var k = Open(hive, subKey, RegView.Default); return k?.GetSubKeyNames() ?? Array.Empty<string>(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }

    public IReadOnlyList<string> ValueNames(RegHive hive, string subKey)
    {
        try { using var k = Open(hive, subKey, RegView.Default); return k?.GetValueNames() ?? Array.Empty<string>(); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return Array.Empty<string>(); }
    }

    // ---- power ------------------------------------------------------------------------------------------------

    public uint? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac) => _power.TryReadValue(scheme, sub, setting, ac);

    public Guid? ActiveScheme => _power.TryGetActiveScheme();

    private PowerCaps? _caps;
    public PowerCaps Caps
    {
        get
        {
            if (_caps != null) return _caps;
            try
            {
                if (PowrProf.GetPwrCapabilities(out var c))
                    return _caps = new PowerCaps(c.SystemS3 != 0, c.SystemS4 != 0, c.AoAc != 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
            return _caps = new PowerCaps(false, false, false);
        }
    }

    public bool OnAcPower => !Kernel32.GetSystemPowerStatus(out var s) || s.ACLineStatus != 0; // 0 = offline, 1 = online, 255 = unknown

    // ---- session ----------------------------------------------------------------------------------------------

    public SessionInfo Session
    {
        get
        {
            bool remote = User32.GetSystemMetrics(NativeConstants.SM_REMOTESESSION) != 0;
            int protocol = 0, state = 0;
            try
            {
                if (Wtsapi32.WTSQuerySessionInformation(IntPtr.Zero, Wtsapi32.WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSClientProtocolType, out var p, out var n) && p != IntPtr.Zero)
                {
                    if (n >= 2) protocol = (ushort)Marshal.ReadInt16(p);
                    Wtsapi32.WTSFreeMemory(p);
                }
                if (Wtsapi32.WTSQuerySessionInformation(IntPtr.Zero, Wtsapi32.WTS_CURRENT_SESSION, WTS_INFO_CLASS.WTSConnectState, out p, out n) && p != IntPtr.Zero)
                {
                    if (n >= 4) state = Marshal.ReadInt32(p);
                    Wtsapi32.WTSFreeMemory(p);
                }
            }
            catch (DllNotFoundException) { }
            int sid = Process.GetCurrentProcess().SessionId;
            int console = unchecked((int)Kernel32.WTSGetActiveConsoleSessionId());
            return new SessionInfo(remote, protocol, state, sid, console);
        }
    }

    // ---- Windows Update / powercfg ---------------------------------------------------------------------------

    public bool? WuaRebootRequired
    {
        get
        {
            try
            {
                var task = Task.Run<bool?>(() =>
                {
                    var t = Type.GetTypeFromProgID("Microsoft.Update.SystemInfo");
                    if (t == null) return null;
                    object? si = null;
                    try
                    {
                        si = Activator.CreateInstance(t);
                        if (si == null) return null;
                        dynamic d = si;
                        return (bool)d.RebootRequired;
                    }
                    finally
                    {
                        if (si != null && Marshal.IsComObject(si)) Marshal.FinalReleaseComObject(si);
                    }
                });
                return task.Wait(TimeSpan.FromSeconds(10)) ? task.Result : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public string? PowercfgRequests
    {
        get
        {
            if (!_isElevated) return null;
            try
            {
                var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), "/requests")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p == null) return null;
                var outTask = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(5000))
                {
                    try { p.Kill(); } catch { }
                    return null;
                }
                return outTask.Wait(1000) ? outTask.Result : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    public bool? HasAudioRenderEndpoint => AudioEndpointProbe.HasDefaultRenderEndpoint();

    public DateTime NowLocal => DateTime.Now;
    public string MachineName => Environment.MachineName;
    public string UserName => Environment.UserName;
    public string UserDomainName => Environment.UserDomainName;
}
