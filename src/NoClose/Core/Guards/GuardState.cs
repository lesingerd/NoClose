namespace NoClose.Core.Guards;

/// <summary>Snapshot of one guard for display.</summary>
public sealed record GuardState(GuardId Id, GuardStatus Status, string Message)
{
    /// <summary>Short chip text shown in the status window.</summary>
    public string ChipText => Status switch
    {
        GuardStatus.Applied => "Applied",
        GuardStatus.Off => "Off",
        GuardStatus.NeedsElevation => "Needs elevation",
        GuardStatus.BlockedByPolicy => "Set by your organisation",
        GuardStatus.Reverted => "Changed externally",
        GuardStatus.Unavailable => "Unavailable",
        GuardStatus.Error => "Error",
        GuardStatus.Degraded => "Degraded",
        _ => Status.ToString()
    };
}
