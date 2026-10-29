namespace Pulaski.Domain.MachineData;

/// <summary>Target percentages; a null one has no target. At least one is set.</summary>
public sealed record OeeTargetValues(decimal? AvailabilityPct, decimal? PerformancePct, decimal? QualityPct, decimal? OeePct)
{
    public bool IsEmpty => AvailabilityPct is null && PerformancePct is null && QualityPct is null && OeePct is null;

    /// <summary>Why the values can't be a target, or null: each one above 0 and at most 100, with one decimal at most, and one at least.</summary>
    public string? Problem()
    {
        if (IsEmpty)
        {
            return "Give at least one target.";
        }

        foreach (var value in new[] { AvailabilityPct, PerformancePct, QualityPct, OeePct })
        {
            if (value is { } v && (v <= 0 || v > 100 || decimal.Round(v, 1) != v))
            {
                return "Each target is a percentage above 0 and at most 100, with one decimal at most.";
            }
        }

        return null;
    }
}

/// <summary>
/// A period of OEE targets (D-073): for a site (no equipment, the default for its equipment) or one piece of equipment, in force from
/// <see cref="EffectiveFrom"/> up to, not including, <see cref="EffectiveTo"/> (open when null). Periods of one scope never overlap,
/// and are ended, never deleted.
/// </summary>
public sealed record OeeTarget(Guid Id, Guid SiteId, Guid? EquipmentId, DateOnly EffectiveFrom, DateOnly? EffectiveTo, OeeTargetValues Values)
{
    public bool InForceOn(DateOnly day) => EffectiveFrom <= day && (EffectiveTo is null || day < EffectiveTo);
}

/// <summary>A change to one scope's periods: the period in force ended or changed in place, and a new one added.</summary>
public sealed record OeeTargetChange(OeeTarget? Changed, OeeTarget? Added);

public static class OeeTargets
{
    /// <summary>The target in force for the equipment on the day: its own, else the site's. A machine's own target replaces the site's whole.</summary>
    public static OeeTarget? InForce(IEnumerable<OeeTarget> targets, Guid equipmentId, DateOnly day)
    {
        OeeTarget? site = null;
        foreach (var t in targets.Where(t => t.InForceOn(day)))
        {
            if (t.EquipmentId == equipmentId)
            {
                return t;
            }

            if (t.EquipmentId is null)
            {
                site = t;
            }
        }

        return site;
    }

    /// <summary>
    /// Sets one scope's target from <paramref name="from"/> on, given its periods. On the latest period's first day it changes that period;
    /// later, the latest is ended there (if still in force then) and a new open period added. Earlier periods are history and never change.
    /// </summary>
    public static OeeTargetChange Set(IReadOnlyList<OeeTarget> scope, Guid newId, Guid siteId, Guid? equipmentId, DateOnly from, OeeTargetValues values)
    {
        if (values.Problem() is { } problem)
        {
            throw new DomainRuleException(problem);
        }

        var latest = scope.MaxBy(t => t.EffectiveFrom);
        if (latest is not null && from < latest.EffectiveFrom)
        {
            throw new DomainRuleException($"Targets from {latest.EffectiveFrom:yyyy-MM-dd} are set already; a new one starts on that day or later.");
        }

        if (latest is not null && from == latest.EffectiveFrom)
        {
            return new OeeTargetChange(latest with { Values = values, EffectiveTo = null }, null);
        }

        var ended = latest is not null && (latest.EffectiveTo is null || latest.EffectiveTo > from) ? latest with { EffectiveTo = from } : null;
        return new OeeTargetChange(ended, new OeeTarget(newId, siteId, equipmentId, from, null, values));
    }

    /// <summary>Ends the scope's latest period on <paramref name="endOn"/> (it isn't in force that day): equipment falls back to the site's target.</summary>
    public static OeeTarget End(IReadOnlyList<OeeTarget> scope, DateOnly endOn)
    {
        var latest = scope.MaxBy(t => t.EffectiveFrom) ?? throw new DomainRuleException("There is no target to end.");
        if (latest.EffectiveTo is { } to && to <= endOn)
        {
            throw new DomainRuleException($"That target already ended on {to:yyyy-MM-dd}.");
        }

        if (endOn <= latest.EffectiveFrom)
        {
            throw new DomainRuleException($"It starts on {latest.EffectiveFrom:yyyy-MM-dd}; end it on a later day, or set new targets from that day.");
        }

        return latest with { EffectiveTo = endOn };
    }
}
