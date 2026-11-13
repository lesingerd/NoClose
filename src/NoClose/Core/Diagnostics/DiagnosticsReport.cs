namespace NoClose.Core.Diagnostics;

public static class DiagnosticsReport
{
    public static string Build(IMachineReader reader, bool elevated, EngineSnapshot? engine = null, DateTimeOffset? now = null, Guards.SilentMediaSnapshot? media = null)
    {
        var findings = DiagnosticsEngine.Run(reader, engine, media);
        var summary = MachineSummary.From(reader, elevated, now ?? DateTimeOffset.Now);
        return DiagnosticsTextFormatter.Format(findings, summary);
    }
}
