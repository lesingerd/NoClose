using System.Text.Json;
using System.Text.Json.Serialization;
using NoClose.Core.Activation;

namespace NoClose.Core.Settings;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
}

public static class SettingsSerializer
{
    public static string Serialize(AppSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings);

    /// <summary>Returns normalised settings, or null if the text is not valid settings JSON.</summary>
    public static AppSettings? TryDeserialize(string json, out string? error)
    {
        error = null;
        try
        {
            var s = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.AppSettings);
            if (s == null) { error = "Settings file is empty."; return null; }
            s.Normalize();
            return s;
        }
        catch (JsonException ex)
        {
            error = ex.Message;
            return null;
        }
    }
}
