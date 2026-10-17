namespace Pulaski.Domain.Materials;

/// <summary>Everything a person sets on a capability (D-020, D-034).</summary>
public sealed record CapabilityChange(
    decimal IdealRatePerHour,
    Guid RateUomId,
    TimeSpan? StandardChangeover,
    decimal? StandardCrewSize,
    bool IsPreferred,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo);

/// <summary>
/// A line's permission to run a material over a period, with its ideal rate (D-020). Capabilities
/// are never deleted: one that no longer applies is ended, so history still says what could run where.
/// </summary>
public sealed record Capability(
    Guid Id,
    Guid EquipmentId,
    Guid MaterialId,
    decimal IdealRatePerHour,
    Guid RateUomId,
    TimeSpan? StandardChangeover,
    decimal? StandardCrewSize,
    bool IsPreferred,
    DateTimeOffset EffectiveFrom,
    DateTimeOffset? EffectiveTo)
{
    /// <summary>Longest standard changeover accepted: a week.</summary>
    public static readonly TimeSpan MaxChangeover = TimeSpan.FromDays(7);

    /// <summary>Largest crew size the column holds.</summary>
    public const decimal MaxCrewSize = 9999.99m;

    /// <exception cref="DomainRuleException">A value is out of range, or the period ends before it starts.</exception>
    public static Capability Create(Guid id, Guid equipmentId, Guid materialId, CapabilityChange change)
    {
        Ensure(change);
        return new Capability(
            id, equipmentId, materialId, change.IdealRatePerHour, change.RateUomId, change.StandardChangeover, change.StandardCrewSize,
            change.IsPreferred, change.EffectiveFrom, change.EffectiveTo);
    }

    /// <exception cref="DomainRuleException">A value is out of range, or the period ends before it starts.</exception>
    public Capability Edit(CapabilityChange change)
    {
        Ensure(change);
        return this with
        {
            IdealRatePerHour = change.IdealRatePerHour,
            RateUomId = change.RateUomId,
            StandardChangeover = change.StandardChangeover,
            StandardCrewSize = change.StandardCrewSize,
            IsPreferred = change.IsPreferred,
            EffectiveFrom = change.EffectiveFrom,
            EffectiveTo = change.EffectiveTo,
        };
    }

    /// <summary>Stops the capability at <paramref name="at"/>; it stays in history.</summary>
    /// <exception cref="DomainRuleException">It hasn't started yet, or has already ended.</exception>
    public Capability End(DateTimeOffset at)
    {
        if (EffectiveTo is { } to && to <= at)
        {
            throw new DomainRuleException("This capability has already ended.");
        }

        if (EffectiveFrom >= at)
        {
            throw new DomainRuleException("This capability hasn't started yet; change its dates instead.");
        }

        return this with { EffectiveTo = at };
    }

    public bool IsInEffect(DateTimeOffset at) => EffectiveFrom <= at && (EffectiveTo is null || EffectiveTo > at);

    /// <summary>True when the two periods share any instant (periods are half-open: from inclusive, to exclusive).</summary>
    public bool Overlaps(DateTimeOffset from, DateTimeOffset? to) =>
        (to is null || EffectiveFrom < to) && (EffectiveTo is null || from < EffectiveTo);

    private static void Ensure(CapabilityChange change)
    {
        if (change.IdealRatePerHour <= 0)
        {
            throw new DomainRuleException("The ideal rate must be greater than zero.");
        }

        if (change.StandardChangeover is { } changeover && (changeover < TimeSpan.Zero || changeover > MaxChangeover))
        {
            throw new DomainRuleException("The standard changeover must be between 0 and 7 days.");
        }

        if (change.StandardCrewSize is { } crew && (crew <= 0 || crew > MaxCrewSize))
        {
            throw new DomainRuleException($"The crew size must be greater than 0 and at most {MaxCrewSize}.");
        }

        if (change.EffectiveTo is { } to && to <= change.EffectiveFrom)
        {
            throw new DomainRuleException("A capability must end after it starts.");
        }
    }
}
