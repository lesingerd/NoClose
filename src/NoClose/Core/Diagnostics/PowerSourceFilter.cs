namespace NoClose.Core.Diagnostics;

/// <summary>Keeps only the findings that matter for the current power source (AC or battery).</summary>
public static class PowerSourceFilter
{
    private const string Ac = "(plugged in)";
    private const string Dc = "(on battery)";

    /// <summary>Findings that name neither power source apply to both and are always kept.</summary>
    public static bool AppliesTo(DiagnosticFinding f, bool onAc)
    {
        bool ac = f.Title.Contains(Ac, StringComparison.OrdinalIgnoreCase);
        bool dc = f.Title.Contains(Dc, StringComparison.OrdinalIgnoreCase);
        if (!ac && !dc) return true;
        return onAc ? ac : dc;
    }

    public static IEnumerable<DiagnosticFinding> For(IEnumerable<DiagnosticFinding> findings, bool onAc) =>
        findings.Where(f => AppliesTo(f, onAc));
}
