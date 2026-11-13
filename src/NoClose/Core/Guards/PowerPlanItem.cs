namespace NoClose.Core.Guards;

/// <summary>Well-known power setting GUIDs.</summary>
public static class PowerGuids
{
    public static readonly Guid SubNone = new("FEA3413E-7E05-4911-9A71-700331F1C294");
    public static readonly Guid SubSleep = new("238C9FA8-0AAD-41ED-83F4-97BE242C8F20");
    public static readonly Guid SubVideo = new("7516B95F-F776-4464-8C53-06167F40CC99");
    public static readonly Guid SubButtons = new("4F971E89-EEBD-4455-A8DE-9E59040E7347");

    public static readonly Guid StandbyIdle = new("29F6C1DB-86DA-48C5-9FDB-F2B67B1F44DA");
    public static readonly Guid HibernateIdle = new("9D7815A6-7EE4-497E-8888-515A05F02364");
    public static readonly Guid UnattendSleep = new("7BC4A2F9-D8FC-4469-B07B-33EB785AACA0");
    public static readonly Guid VideoIdle = new("3C0BC021-C8A8-4E07-A973-6B14CBCB2B7E");
    public static readonly Guid VideoConLock = new("8EC4B3A5-6868-48C2-BE75-4F3044BE88A7");
    public static readonly Guid AllowSystemRequired = new("A4B195F5-8225-47D8-8012-9D41369786E2");
    public static readonly Guid AllowDisplayRequired = new("A9CEB8DA-CD46-44FB-A98B-02AF69DE4623");
    public static readonly Guid AllowAwayMode = new("25DFA149-5DD1-4736-B5AB-E8A37B5B8187");
    public static readonly Guid ConsoleLock = new("0E796BDB-100D-47D6-A2D5-F7D2DAA51F51");
    public static readonly Guid LidAction = new("5CA83367-6E45-459F-A27B-476B1D01C936");
    public static readonly Guid PowerButtonAction = new("7648EFA3-DD9C-4E3E-B566-50F929386280");
    public static readonly Guid Balanced = new("381B4222-F694-41F0-9685-FF5BB260DF2E");
    public static readonly Guid HighPerformance = new("8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C");
    public static readonly Guid PowerSaver = new("A1841308-3541-4FAB-BC81-F71556F20B4A");

    /// <summary>Registry sub key (below HKLM) used by the "Power Management" policies for one setting. Read only.</summary>
    public static string PolicyKeyFor(Guid setting) =>
        @"SOFTWARE\Policies\Microsoft\Power\PowerSettings\" + setting.ToString("D").ToUpperInvariant();

    public const string PolicyPowerSettingsRoot = @"SOFTWARE\Policies\Microsoft\Power\PowerSettings";

    public static string FriendlySchemeName(Guid g)
    {
        if (g == Balanced) return "Balanced";
        if (g == HighPerformance) return "High performance";
        if (g == PowerSaver) return "Power saver";
        return "custom";
    }
}

/// <summary>One item the PowerPlan guard may change on the duplicated scheme.</summary>
public sealed record PowerPlanItem(string Key, string DisplayName, Guid Subgroup, Guid Setting, uint NewValue, bool DefaultOn);

public static class PowerPlanItems
{
    public static readonly IReadOnlyList<PowerPlanItem> All = new[]
    {
        new PowerPlanItem("sleep", "Sleep after: never", PowerGuids.SubSleep, PowerGuids.StandbyIdle, 0, true),
        new PowerPlanItem("hibernate", "Hibernate after: never", PowerGuids.SubSleep, PowerGuids.HibernateIdle, 0, true),
        new PowerPlanItem("unattendedSleep", "Unattended sleep timeout: never", PowerGuids.SubSleep, PowerGuids.UnattendSleep, 0, true),
        new PowerPlanItem("display", "Turn off display after: never", PowerGuids.SubVideo, PowerGuids.VideoIdle, 0, true),
        new PowerPlanItem("consoleLockDisplayOff", "Console lock display off timeout: never", PowerGuids.SubVideo, PowerGuids.VideoConLock, 0, false),
        new PowerPlanItem("allowSystemRequired", "Allow system required requests: on", PowerGuids.SubSleep, PowerGuids.AllowSystemRequired, 1, true),
        new PowerPlanItem("allowDisplayRequired", "Allow display required policy: on", PowerGuids.SubVideo, PowerGuids.AllowDisplayRequired, 1, true),
        new PowerPlanItem("allowAwayMode", "Allow away mode policy: on", PowerGuids.SubSleep, PowerGuids.AllowAwayMode, 1, false),
    };
}
