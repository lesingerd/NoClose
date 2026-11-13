using System.Globalization;
using System.Text;

namespace NoClose.Core.Diagnostics;

public static class DiagnosticsTextFormatter
{
    public static string Format(IEnumerable<DiagnosticFinding> findings, MachineSummary m)
    {
        var sb = new StringBuilder();
        sb.Append("NoClose diagnostics — ")
          .Append(m.Time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append(" (").Append(m.Time.ToString("zzz", CultureInfo.InvariantCulture)).AppendLine(")");
        sb.Append("Machine: ").Append(m.Computer).Append("  User: ").Append(m.User)
          .Append("  Elevated: ").Append(m.Elevated ? "yes" : "no")
          .Append("  Session: ").AppendLine(m.Session);
        sb.Append("Power: ").Append(m.Power).Append("  Modern Standby: ").Append(m.ModernStandby ? "yes" : "no")
          .Append("  Active scheme: ").AppendLine(m.ActiveScheme);

        int count = 0;
        foreach (var f in findings)
        {
            count++;
            var tag = "[" + f.Severity.ToString().ToUpperInvariant() + "]";
            sb.Append(tag.PadRight(9)).Append(f.RuleId).Append("  ").AppendLine(f.Title);
            foreach (var line in SplitLines(f.Detail))
                sb.Append("         ").AppendLine(line);
            sb.Append("         Policy: ").Append(f.PolicyName ?? "—")
              .Append("   Addressed by: ").AppendLine(f.AddressedBy is { } g ? GuardName(g) : "—");
            sb.Append("         Advice: ").AppendLine(f.Advice);
        }
        if (count == 0) sb.AppendLine("No findings.");
        return sb.ToString();
    }

    public static string GuardName(Guards.GuardId g) => g switch
    {
        Guards.GuardId.ExecutionState => "Keep awake (execution state)",
        Guards.GuardId.ScreenSaver => "Screen saver",
        Guards.GuardId.PowerPlan => "Power plan",
        Guards.GuardId.ActiveHours => "Active hours",
        Guards.GuardId.EndSession => "Ask before logoff/shutdown",
        Guards.GuardId.SilentMedia => "Silent media session",
        _ => g.ToString()
    };

    private static IEnumerable<string> SplitLines(string s) =>
        s.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
}
