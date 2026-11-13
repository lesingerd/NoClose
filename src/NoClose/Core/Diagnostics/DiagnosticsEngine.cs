using NoClose.Core.Diagnostics.Rules;

namespace NoClose.Core.Diagnostics;

public static class DiagnosticsEngine
{
    public static IReadOnlyList<IDiagnosticRule> CreateRules(EngineSnapshot? engine = null, Guards.SilentMediaSnapshot? media = null)
    {
        var rules = new List<IDiagnosticRule>
        {
            new OverrideRule(), new AllowRequestsRule(), new SleepTimeoutRule(), new ModernStandbyRule(), new LidRule(),
            new SignInOnWakeRule(), new InactivityLimitRule(), new ScreenSaverPolicyRule(), new ScreenSaverUserRule(),
            new MdmDeviceLockRule(), new DynamicLockRule(), new SmartCardRemovalRule(), new RemoteSessionRule(),
            new RemoteLimitsRule(), new PendingRebootRule(), new ActiveHoursRule(), new UpdatePolicyRule(),
            new ManagedDeviceRule(), new AutoRestartSignOnRule(), new PowercfgRequestsRule(), new SilentMediaRule(media),
        };
        if (engine != null) rules.Add(new EngineRule(engine));
        return rules;
    }

    /// <summary>Runs every rule (a failing rule becomes an Info finding) and sorts by severity, highest first.</summary>
    public static IReadOnlyList<DiagnosticFinding> Run(IMachineReader reader, EngineSnapshot? engine = null, Guards.SilentMediaSnapshot? media = null)
    {
        var findings = new List<DiagnosticFinding>();
        foreach (var rule in CreateRules(engine, media))
        {
            try
            {
                findings.AddRange(rule.Evaluate(reader));
            }
            catch (Exception ex)
            {
                findings.Add(new DiagnosticFinding(rule.Id, Severity.Info, "Rule could not be evaluated",
                    "rule failed: " + ex.Message, null, "This check was skipped.", null, false));
            }
        }
        return findings
            .Select((f, i) => (f, i))
            .OrderByDescending(x => x.f.Severity)
            .ThenBy(x => x.f.RuleId, StringComparer.Ordinal)
            .ThenBy(x => x.i)
            .Select(x => x.f)
            .ToList();
    }
}
