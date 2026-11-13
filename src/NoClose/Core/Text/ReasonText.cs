using System.Text;

namespace NoClose.Core.Text;

public static class ReasonText
{
    public const string Default = "Long-running task";
    public const int MaxLength = 120;

    /// <summary>Strips control characters, trims and truncates to 120 characters. Empty input yields the default.</summary>
    public static string Sanitize(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return Default;
        var sb = new StringBuilder(reason.Length);
        foreach (var ch in reason)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }
        var s = sb.ToString().Trim();
        if (s.Length == 0) return Default;
        if (s.Length > MaxLength) s = s[..MaxLength].TrimEnd();
        return s;
    }

    public static string PowerRequestReason(string reason) => "NoClose: " + Sanitize(reason);

    public static string ShutdownBlockReason(string reason) => "NoClose is keeping this session open: " + Sanitize(reason);
}
