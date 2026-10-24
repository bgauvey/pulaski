namespace Pulaski.Domain.Inventory;

/// <summary>
/// Where a reason may sit in its kind's tree (D-062). A reason is filed under a group of the same kind, never under itself or
/// anything below it, and never under a group with a narrower scope: a reason for every site only under a group for every site,
/// a site's reason under a group for every site or for that site. At most <see cref="MaxLevels"/> levels. A group is switched off
/// only once everything in it is, and nothing is switched on or filed under a group that is off. History points at a reason's id,
/// so none of this changes what it recorded. Pure; the caller passes every reason of the kind, locked.
/// </summary>
public static class ReasonTree
{
    /// <summary>Levels a tree may have, top groups included.</summary>
    public const int MaxLevels = 5;

    /// <summary>Why <paramref name="reason"/> can't be filed under <paramref name="parentId"/> (null: at the top); null when it can.</summary>
    /// <param name="kindTree">Every reason of the reason's kind, including it when it already exists.</param>
    /// <param name="reason">The reason as it is to be, before the move.</param>
    /// <param name="parentId">The group it is to go under, or null for the top.</param>
    public static string? WhyNotUnder(IReadOnlyCollection<ReasonCode> kindTree, ReasonCode reason, Guid? parentId)
    {
        if (parentId is not { } id)
        {
            return Height(kindTree, reason.Id) > MaxLevels ? $"A tree has at most {MaxLevels} levels." : null;
        }

        var parent = kindTree.FirstOrDefault(r => r.Id == id);
        if (parent is null || parent.Kind != reason.Kind)
        {
            return "That group isn't one of this kind's reasons.";
        }

        if (!parent.IsGroup)
        {
            return $"{parent.Code} isn't a group: only a group holds other reasons.";
        }

        if (parent.Id == reason.Id || Ancestors(kindTree, parent).Any(a => a.Id == reason.Id))
        {
            return $"{reason.Code} can't go under {parent.Code}: {parent.Code} is inside it.";
        }

        if (parent.SiteId is { } parentSite && parentSite != reason.SiteId)
        {
            return reason.SiteId is null
                ? $"A reason for every site can only go under a group for every site; {parent.Code} is for one site."
                : $"{parent.Code} is for another site.";
        }

        if (!parent.IsActive && reason.IsActive)
        {
            return $"{parent.Code} is switched off.";
        }

        var levels = Ancestors(kindTree, parent).Count + 1 + Height(kindTree, reason.Id);
        return levels > MaxLevels ? $"That would make {levels} levels; a tree has at most {MaxLevels}." : null;
    }

    /// <summary>Why <paramref name="reason"/> can't be switched on or off as asked; null when it can.</summary>
    /// <param name="kindTree">Every reason of the reason's kind.</param>
    /// <param name="reason">The reason as it is now.</param>
    /// <param name="isActive">Whether it is to be on.</param>
    public static string? WhyNotActive(IReadOnlyCollection<ReasonCode> kindTree, ReasonCode reason, bool isActive)
    {
        if (isActive == reason.IsActive)
        {
            return null;
        }

        if (!isActive && kindTree.Where(r => r.ParentId == reason.Id && r.IsActive).Select(r => r.Code).Order(StringComparer.Ordinal).ToList() is { Count: > 0 } on)
        {
            return $"Switch off what is in {reason.Code} first: {string.Join(", ", on)}.";
        }

        return isActive && kindTree.FirstOrDefault(r => r.Id == reason.ParentId) is { IsActive: false } parent
            ? $"Its group {parent.Code} is switched off; switch it on first."
            : null;
    }

    /// <summary>The groups above a reason, nearest first.</summary>
    public static IReadOnlyList<ReasonCode> Ancestors(IReadOnlyCollection<ReasonCode> kindTree, ReasonCode reason)
    {
        var byId = kindTree.ToDictionary(r => r.Id);
        var result = new List<ReasonCode>();
        var seen = new HashSet<Guid> { reason.Id };
        for (var at = reason.ParentId; at is { } id && byId.TryGetValue(id, out var parent) && seen.Add(id); at = parent.ParentId)
        {
            result.Add(parent);
        }

        return result;
    }

    // Levels from a reason down to its deepest descendant, itself included.
    private static int Height(IReadOnlyCollection<ReasonCode> kindTree, Guid id, int guard = 0) =>
        guard > MaxLevels * 4 ? guard : 1 + kindTree.Where(r => r.ParentId == id).Select(r => Height(kindTree, r.Id, guard + 1)).DefaultIfEmpty(0).Max();
}
