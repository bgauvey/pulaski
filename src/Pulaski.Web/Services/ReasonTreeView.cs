using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services;

/// <summary>How the Reasons screen offers a reason's place in its tree (D-062); the API checks the same rules.</summary>
public static class ReasonTreeView
{
    /// <summary>
    /// The groups a reason may go under: groups of its kind that are not it or inside it, and whose scope covers it (a group for every
    /// site, or for the reason's own site). <paramref name="reason"/> is null for a new reason at <paramref name="siteId"/>.
    /// </summary>
    public static IReadOnlyList<ReasonCodeAdminDto> Groups(IReadOnlyList<ReasonCodeAdminDto> all, string kind, ReasonCodeAdminDto? reason, Guid? siteId)
    {
        var inside = reason is null ? [] : Inside(all, reason.Id);
        return [.. all.Where(g => g.Kind == kind && !g.IsSelectable && g.Id != reason?.Id && !inside.Contains(g.Id)
                                 && (g.SiteId is null || g.SiteId == siteId))];
    }

    /// <summary>A group as the "Under" list names it, with the groups above it.</summary>
    public static string GroupLabel(ReasonCodeAdminDto group) => group.GroupPath is { } path ? $"{path} › {group.Name}" : group.Name;

    // Everything filed below a reason, at any depth.
    private static HashSet<Guid> Inside(IReadOnlyList<ReasonCodeAdminDto> all, Guid id)
    {
        var found = new HashSet<Guid>();
        var next = new Queue<Guid>([id]);
        while (next.TryDequeue(out var at))
        {
            foreach (var child in all.Where(r => r.ParentId == at && found.Add(r.Id)))
            {
                next.Enqueue(child.Id);
            }
        }

        return found;
    }
}
