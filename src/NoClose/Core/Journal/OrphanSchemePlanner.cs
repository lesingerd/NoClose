namespace NoClose.Core.Journal;

public sealed record OrphanSchemePlan(IReadOnlyList<Guid> Delete, IReadOnlyList<Guid> ActiveOrphans);

/// <summary>Pure: finds leftover "NoClose temporary (...)" power plans that no journal entry refers to any more.</summary>
public static class OrphanSchemePlanner
{
    public const string NamePrefix = "NoClose temporary";

    public static bool IsNoCloseTemporaryName(string? name) =>
        name != null && name.StartsWith(NamePrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Inactive orphans are deleted; an active orphan is only reported (never deleted underneath the user).</summary>
    public static OrphanSchemePlan Plan(IEnumerable<(Guid Id, string? Name)> schemes, Guid? active, IReadOnlySet<Guid> referenced)
    {
        var delete = new List<Guid>();
        var activeOrphans = new List<Guid>();
        foreach (var (id, name) in schemes)
        {
            if (!IsNoCloseTemporaryName(name) || referenced.Contains(id)) continue;
            if (active == id) activeOrphans.Add(id);
            else delete.Add(id);
        }
        return new OrphanSchemePlan(delete, activeOrphans);
    }
}
