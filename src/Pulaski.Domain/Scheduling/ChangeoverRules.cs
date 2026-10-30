namespace Pulaski.Domain.Scheduling;

/// <summary>
/// What a changeover rule matches (D-015): a line (null for every line of the site) and, on each side, a material, a material class, or
/// anything when both are null. Two rules with the same pattern would match exactly the same changes, so only one may be on at a time.
/// </summary>
public sealed record ChangeoverPattern(Guid? EquipmentId, Guid? FromMaterialId, Guid? FromMaterialClassId, Guid? ToMaterialId, Guid? ToMaterialClassId);

/// <summary>A changeover rule as kept (<c>core.changeover_rule</c>), with its pattern, time and whether it is on.</summary>
public sealed record ChangeoverRuleRecord(Guid Id, Guid SiteId, ChangeoverPattern Pattern, TimeSpan Duration, string? Note, bool IsActive);

/// <summary>Checks on changeover rules made on the screen or imported (D-074).</summary>
public static class ChangeoverRules
{
    /// <summary>The longest changeover a rule may set: a week.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(7);

    public const int MaxNote = 500;

    /// <summary>Why the rule can't be kept, or null.</summary>
    public static string? Problem(ChangeoverPattern pattern, TimeSpan duration, string? note)
    {
        if (pattern.FromMaterialId is not null && pattern.FromMaterialClassId is not null)
        {
            return "The 'from' side is a material or a class, not both.";
        }

        if (pattern.ToMaterialId is not null && pattern.ToMaterialClassId is not null)
        {
            return "The 'to' side is a material or a class, not both.";
        }

        if (pattern.FromMaterialId is { } from && from == pattern.ToMaterialId)
        {
            return "From a material to itself is no changeover: the line just keeps running.";
        }

        if (duration < TimeSpan.Zero || duration > MaxDuration)
        {
            return "A changeover takes from 0 minutes to 7 days.";
        }

        if (duration.Ticks % TimeSpan.TicksPerMinute != 0)
        {
            return "Give the time in whole minutes.";
        }

        return note is { Length: > MaxNote } ? $"Keep the note to {MaxNote} characters." : null;
    }

    /// <summary>The rule switched on that already has this pattern, other than <paramref name="exceptId"/>; null when there is none.</summary>
    public static ChangeoverRuleRecord? Duplicate(IEnumerable<ChangeoverRuleRecord> siteRules, ChangeoverPattern pattern, Guid? exceptId = null) =>
        siteRules.FirstOrDefault(r => r.IsActive && r.Id != exceptId && r.Pattern == pattern);
}
