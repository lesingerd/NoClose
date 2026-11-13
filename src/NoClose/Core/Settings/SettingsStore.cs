using NoClose.Core.IO;

namespace NoClose.Core.Settings;

/// <summary>Loads and saves %AppData%\NoClose\settings.json.</summary>
public sealed class SettingsStore
{
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NoClose", "settings.json");
    }

    public string Path { get; }

    /// <summary>Load settings; an invalid file is renamed to settings.json.bad-&lt;timestamp&gt; and defaults are used.</summary>
    public AppSettings Load(out string? warning)
    {
        warning = null;
        lock (_gate)
        {
            if (!File.Exists(Path)) return AppSettings.CreateDefault();
            try
            {
                var text = File.ReadAllText(Path);
                var s = SettingsSerializer.TryDeserialize(text, out var err);
                if (s != null) return s;
                warning = $"Settings file invalid ({err}); using defaults.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warning = $"Settings file unreadable ({ex.Message}); using defaults.";
                return AppSettings.CreateDefault();
            }
            try
            {
                File.Move(Path, Path + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
            catch { /* best effort */ }
            return AppSettings.CreateDefault();
        }
    }

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            try { AtomicFile.WriteAllText(Path, SettingsSerializer.Serialize(settings), writeThrough: false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* settings are a convenience */ }
        }
    }
}
