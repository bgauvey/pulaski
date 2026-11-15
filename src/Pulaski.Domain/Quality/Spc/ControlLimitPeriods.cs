namespace Pulaski.Domain.Quality.Spc;

/// <summary>How a set of control limits was set (D-099). Values match <c>core.control_limit.method</c>.</summary>
public static class LimitMethods
{
    /// <summary>Worked out from a reviewed baseline of results.</summary>
    public const string Computed = "computed";

    /// <summary>Entered by a person, with a reason.</summary>
    public const string Manual = "manual";
}

/// <summary>The results a computed set of limits came from: when, how many subgroups, and which results the reviewer left out.</summary>
public sealed record LimitBaseline(DateTimeOffset From, DateTimeOffset To, int Subgroups, IReadOnlyList<Guid> LeftOut);

/// <summary>
/// Control limits in force over a period (D-099) for a characteristic and material, and optionally one line, whose own limits come first. Fixed
/// once approved: new limits start a new period, and a period is ended, never changed or deleted.
/// </summary>
public sealed record ControlLimit(
    Guid Id,
    Guid CharacteristicId,
    Guid MaterialId,
    Guid? EquipmentId,
    string ChartType,
    int SubgroupSize,
    LimitSet Limits,
    string Method,
    LimitBaseline? Baseline,
    string? Note,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo,
    string ApprovedBy,
    DateTimeOffset ApprovedAt)
{
    public const int MaxNote = 1000;

    public bool IsInEffect(DateTimeOffset at) => EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);

    /// <summary>The same characteristic, material and line (or both for every line).</summary>
    public bool SameScope(ControlLimit other) =>
        CharacteristicId == other.CharacteristicId && MaterialId == other.MaterialId && EquipmentId == other.EquipmentId;

    public bool Overlaps(DateTimeOffset from, DateTimeOffset? to) => (to is null || EffectiveFrom < to) && (EffectiveTo is null || from < EffectiveTo);

    /// <exception cref="DomainRuleException">It has ended already, or hasn't started.</exception>
    public ControlLimit End(DateTimeOffset at)
    {
        if (EffectiveTo is { } to && to <= at)
        {
            throw new DomainRuleException("These limits have ended already.");
        }

        return EffectiveFrom >= at ? throw new DomainRuleException("These limits haven't started yet.") : this with { EffectiveTo = at };
    }
}

/// <summary>The rules for placing a new set of limits among a scope's periods (D-099). Pure.</summary>
public static class ControlLimitPeriods
{
    /// <summary>
    /// Checks <paramref name="added"/> against the scope's <paramref name="existing"/> periods: an open period that started earlier is ended where
    /// the new one starts (returned to save); any other overlap is refused, so a time has at most one set of limits for a scope.
    /// </summary>
    /// <exception cref="DomainRuleException">The limits are out of order, or the new period overlaps one it can't end.</exception>
    public static IReadOnlyList<ControlLimit> Place(ControlLimit added, IReadOnlyList<ControlLimit> existing)
    {
        Ensure(added);
        var ended = new List<ControlLimit>();
        foreach (var other in existing.Where(e => e.SameScope(added) && e.Id != added.Id && e.Overlaps(added.EffectiveFrom, added.EffectiveTo)))
        {
            if (other.EffectiveTo is null && other.EffectiveFrom < added.EffectiveFrom && added.EffectiveTo is null)
            {
                ended.Add(other with { EffectiveTo = added.EffectiveFrom });
                continue;
            }

            throw new DomainRuleException(
                "Those dates overlap other limits for the same characteristic, material and line; new limits can only follow on from the latest.");
        }

        return ended;
    }

    /// <summary>The limits used at <paramref name="at"/>: the line's own, else those for every line; null when neither is in force.</summary>
    public static ControlLimit? InForce(IEnumerable<ControlLimit> limits, Guid? equipmentId, DateTimeOffset at)
    {
        var inForce = limits.Where(l => l.IsInEffect(at)).ToList();
        return (equipmentId is null ? null : inForce.FirstOrDefault(l => l.EquipmentId == equipmentId)) ?? inForce.FirstOrDefault(l => l.EquipmentId is null);
    }

    /// <summary>The limits a person enters must be in order, and a chart with a dispersion chart needs its centre and upper limit.</summary>
    /// <exception cref="DomainRuleException">They aren't.</exception>
    public static void Ensure(ControlLimit limit)
    {
        var l = limit.Limits;
        if (!(double.IsFinite(l.Center) && double.IsFinite(l.Ucl) && double.IsFinite(l.Lcl)) || l.Lcl > l.Center || l.Center > l.Ucl)
        {
            throw new DomainRuleException("The limits must be numbers with the lower limit at most the centre line, and the centre line at most the upper limit.");
        }

        if (ChartTypes.HasDispersion(limit.ChartType)
            && (l.DispersionCenter is not { } dc || l.DispersionUcl is not { } du || dc < 0 || du < dc || (l.DispersionLcl ?? 0) > dc || (l.DispersionLcl ?? 0) < 0))
        {
            throw new DomainRuleException("This chart has a dispersion chart: give its centre line and upper limit (and a lower limit from 0 to the centre).");
        }

        if (ChartTypes.IsAttribute(limit.ChartType) && l.Lcl < 0)
        {
            throw new DomainRuleException("A count can't go below zero, so neither can its lower limit.");
        }

        if (limit.Method == LimitMethods.Manual && string.IsNullOrWhiteSpace(limit.Note))
        {
            throw new DomainRuleException("Say why the limits are entered by hand.");
        }

        if (limit.Note is { Length: > ControlLimit.MaxNote })
        {
            throw new DomainRuleException($"The note is at most {ControlLimit.MaxNote} characters.");
        }

        if (limit.EffectiveTo is { } to && to <= limit.EffectiveFrom)
        {
            throw new DomainRuleException("Limits must end after they start.");
        }
    }

    /// <summary>Whether limits worked out again match those reviewed, to the stored precision.</summary>
    public static bool Same(LimitSet a, LimitSet b) =>
        Close(a.Center, b.Center) && Close(a.Ucl, b.Ucl) && Close(a.Lcl, b.Lcl);

    private static bool Close(double a, double b) => Math.Abs(a - b) <= 1e-9 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)));
}
