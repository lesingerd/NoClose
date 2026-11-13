using System.Reflection;

namespace NoClose.Properties;

/// <summary>Loads the embedded tray icons.</summary>
internal static class Icons
{
    private static readonly Dictionary<string, Icon> Cache = new();

    public static Icon Load(string name)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached)) return cached;
            var asm = typeof(Icons).Assembly;
            var resource = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(name + ".ico", StringComparison.OrdinalIgnoreCase));
            Icon icon;
            if (resource != null)
            {
                using var s = asm.GetManifestResourceStream(resource)!;
                icon = new Icon(s);
            }
            else icon = SystemIcons.Application;
            return Cache[name] = icon;
        }
    }
}
