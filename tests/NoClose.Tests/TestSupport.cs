using NoClose.Core.Diagnostics;
using NoClose.Core.Time;

namespace NoClose.Tests;

internal sealed class FakeClock : IClock
{
    public FakeClock(DateTime utcNow, TimeZoneInfo? tz = null)
    {
        UtcNow = utcNow;
        TimeZone = tz ?? TimeZoneInfo.Utc;
    }

    public DateTime UtcNow { get; set; }
    public TimeZoneInfo TimeZone { get; }
    public void Advance(TimeSpan t) => UtcNow += t;
}

/// <summary>Dictionary backed machine reader; never touches the real machine.</summary>
internal sealed class FakeMachineReader : IMachineReader
{
    private readonly Dictionary<string, object> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Guid, Guid, Guid, bool), uint> _power = new();

    private static string K(RegHive h, string key) => h + "\\" + key.Trim('\\');
    private static string V(RegHive h, string key, string name) => K(h, key) + "|" + name;

    public FakeMachineReader Set(RegHive hive, string key, string name, object value)
    {
        _values[V(hive, key, name)] = value;
        _keys.Add(K(hive, key));
        return this;
    }

    public FakeMachineReader AddKey(RegHive hive, string key) { _keys.Add(K(hive, key)); return this; }

    public FakeMachineReader Power(Guid sub, Guid setting, uint ac, uint dc)
    {
        _power[(ActiveScheme!.Value, sub, setting, true)] = ac;
        _power[(ActiveScheme!.Value, sub, setting, false)] = dc;
        return this;
    }

    public object? ReadRegistry(RegHive hive, string subKey, string valueName, RegView view = RegView.Default) =>
        _values.TryGetValue(V(hive, subKey, valueName), out var v) ? v : null;

    public bool KeyExists(RegHive hive, string subKey)
    {
        var k = K(hive, subKey);
        return _keys.Contains(k) || _keys.Any(x => x.StartsWith(k + "\\", StringComparison.OrdinalIgnoreCase));
    }

    public IReadOnlyList<string> SubKeyNames(RegHive hive, string subKey)
    {
        var prefix = K(hive, subKey) + "\\";
        return _keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(k => k[prefix.Length..].Split('\\')[0]).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<string> ValueNames(RegHive hive, string subKey)
    {
        var prefix = K(hive, subKey) + "|";
        return _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(k => k[prefix.Length..]).ToList();
    }

    public uint? ReadPowerValue(Guid scheme, Guid sub, Guid setting, bool ac) =>
        _power.TryGetValue((scheme, sub, setting, ac), out var v) ? v : null;

    public Guid? ActiveScheme { get; set; } = NoClose.Core.Guards.PowerGuids.Balanced;
    public PowerCaps Caps { get; set; } = new(true, true, false);
    public bool OnAcPower { get; set; } = true;
    public SessionInfo Session { get; set; } = new(false, 0, 0, 1, 1);
    public bool? WuaRebootRequired { get; set; }
    public string? PowercfgRequests { get; set; }
    public bool? HasAudioRenderEndpoint { get; set; }
    public DateTime NowLocal { get; set; } = new(2026, 11, 12, 14, 0, 0);
    public string MachineName { get; set; } = "PC";
    public string UserName { get; set; } = "user";
    public string UserDomainName { get; set; } = "PC";
}
