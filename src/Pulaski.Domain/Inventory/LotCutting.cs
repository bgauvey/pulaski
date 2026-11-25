using System.Globalization;

namespace Pulaski.Domain.Inventory;

/// <summary>
/// When a line's production lot is full (D-104): at <see cref="Size"/> (the material's base unit), after <see cref="Hours"/> since its first
/// production, or at a change of shift (<see cref="ByShift"/>). Kept per line and material on the capability; none of them means one lot per
/// report, as before.
/// </summary>
public sealed record LotCutRule(decimal? Size, decimal? Hours, bool ByShift)
{
    /// <summary>The longest a lot can stay open by age: 30 days.</summary>
    public const decimal MaxHours = 720m;

    /// <summary>The most lots one report may start, so a mistyped quantity can't make thousands.</summary>
    public const int MaxLotsPerReport = 100;

    public static LotCutRule Off { get; } = new(null, null, false);

    /// <summary>True when reports fill a lot instead of each making its own.</summary>
    public bool IsOn => Size is not null || Hours is not null || ByShift;

    /// <returns>Why the values can't be kept, or null.</returns>
    public UserMessage? Problem() =>
        Size is <= 0 ? UserMessage.Of("A lot's size must be greater than zero, or blank.")
        : Hours is { } hours && (hours <= 0 || hours > MaxHours) ? UserMessage.From($"A lot's age must be more than 0 and at most {MaxHours} hours, or blank.")
        : null;
}

/// <summary>The lot a job is filling on a line (D-104): its quantity now, in the base unit, and when its first production counted.</summary>
public sealed record OpenLot(Guid LotId, string LotNumber, decimal Quantity, DateTimeOffset StartedAt);

/// <summary>Part of a report's good output: into the open lot (<see cref="LotNumber"/>), or into a new one (null).</summary>
public sealed record LotPart(string? LotNumber, decimal Quantity);

/// <summary>Splits a report's good output between the lot being filled and new lots (D-104). Pure.</summary>
public static class LotCutting
{
    /// <summary>
    /// Why the open lot takes nothing more: it is full, too old, or from another shift; null when it carries on. A shift is named by a key
    /// (its production day and code, or the site-local day without a schedule); the lot's is that of its first production.
    /// </summary>
    public static string? WhyCut(LotCutRule rule, OpenLot open, DateTimeOffset at, string? lotShift, string? shiftNow)
    {
        if (rule.Size is { } size && open.Quantity >= size)
        {
            return $"lot {open.LotNumber} is full ({Qty(size)})";
        }

        if (rule.Hours is { } hours && at - open.StartedAt >= TimeSpan.FromHours((double)hours))
        {
            return $"lot {open.LotNumber} was started {Qty(hours)} h or more ago";
        }

        return rule.ByShift && !string.Equals(lotShift, shiftNow, StringComparison.Ordinal) ? $"lot {open.LotNumber} is from an earlier shift" : null;
    }

    /// <summary>
    /// The good quantity's parts, in order: what fits in the open lot when it carries on, then new lots of at most the rule's size each.
    /// </summary>
    /// <exception cref="DomainRuleException">It would start more than <see cref="LotCutRule.MaxLotsPerReport"/> lots.</exception>
    public static IReadOnlyList<LotPart> Parts(LotCutRule rule, OpenLot? open, decimal quantity, DateTimeOffset at, string? lotShift, string? shiftNow)
    {
        var parts = new List<LotPart>();
        var left = quantity;
        if (open is not null && WhyCut(rule, open, at, lotShift, shiftNow) is null)
        {
            var into = rule.Size is { } size ? Math.Min(size - open.Quantity, left) : left;
            if (into > 0)
            {
                parts.Add(new LotPart(open.LotNumber, into));
                left -= into;
            }
        }

        if (rule.Size is { } lotSize && Math.Ceiling(left / lotSize) > LotCutRule.MaxLotsPerReport)
        {
            throw new DomainRuleException(
                $"That would start more than {LotCutRule.MaxLotsPerReport} lots of {Qty(lotSize)}; check the quantity, or report it in parts.");
        }

        while (left > 0)
        {
            var next = rule.Size is { } s ? Math.Min(s, left) : left;
            parts.Add(new LotPart(null, next));
            left -= next;
        }

        return parts;
    }

    private static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
