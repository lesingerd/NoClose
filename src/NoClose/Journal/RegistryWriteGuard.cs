using NoClose.Core.Diagnostics;

namespace NoClose.Journal;

/// <summary>
/// Enforces the scope limit: NoClose never writes policy, MDM or security enforced settings. Every registry write in the
/// application goes through <c>RegistryAccessor</c>, which calls <see cref="EnsureAllowed"/> first.
/// </summary>
public static class RegistryWriteGuard
{
    public const string ActiveHoursKey = @"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings";

    private static readonly string[] AllowedValues = { "ActiveHoursStart", "ActiveHoursEnd", "SmartActiveHoursState" };

    private static readonly string[] DeniedPrefixes =
    {
        @"SOFTWARE\Policies",
        @"Policies",
        @"SOFTWARE\Microsoft\PolicyManager",
        @"SOFTWARE\Microsoft\Enrollments",
        @"SYSTEM\CurrentControlSet\Control\Power\PowerRequestOverride",
        @"SYSTEM\CurrentControlSet\Control\Terminal Server",
    };

    public static string Normalize(string key) => key.Replace('/', '\\').Trim('\\');

    public static bool IsDenied(string key)
    {
        var k = Normalize(key);
        foreach (var p in DeniedPrefixes)
            if (k.Equals(p, StringComparison.OrdinalIgnoreCase) || k.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase))
                return true;
        // Any "...\Policies\..." (or a key whose last component is Policies) anywhere, including WOW6432Node paths.
        if (k.Contains(@"\Policies\", StringComparison.OrdinalIgnoreCase) || k.EndsWith(@"\Policies", StringComparison.OrdinalIgnoreCase))
            return true;
        return false;
    }

    public static bool IsAllowed(RegHive hive, string key, string valueName) =>
        hive == RegHive.HKLM
        && Normalize(key).Equals(ActiveHoursKey, StringComparison.OrdinalIgnoreCase)
        && AllowedValues.Contains(valueName, StringComparer.OrdinalIgnoreCase);

    /// <summary>Throws <see cref="InvalidOperationException"/> unless the write is on the allow-list and not denied.</summary>
    public static void EnsureAllowed(RegHive hive, string key, string valueName)
    {
        if (IsDenied(key))
            throw new InvalidOperationException($"NoClose never writes policy or management controlled settings: {hive}\\{key}");
        if (!IsAllowed(hive, key, valueName))
            throw new InvalidOperationException($"Registry write not on the NoClose allow-list: {hive}\\{key} [{valueName}]");
    }
}
