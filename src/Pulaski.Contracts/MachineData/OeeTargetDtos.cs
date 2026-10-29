namespace Pulaski.Contracts.MachineData;

// ---- OEE targets (D-073) ----

/// <summary>Target percentages; null where there is no target.</summary>
public sealed record OeeTargetFiguresDto(decimal? AvailabilityPct, decimal? PerformancePct, decimal? QualityPct, decimal? OeePct);

/// <summary>A period of OEE targets: the site's default (no equipment) or one piece of equipment's, from a day until it is ended.</summary>
public sealed record OeeTargetDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }

    /// <summary>Null for the site's default, which equipment without targets of its own follows.</summary>
    public Guid? EquipmentId { get; init; }
    public string? EquipmentCode { get; init; }
    public string? EquipmentName { get; init; }

    /// <summary>The first production day it is in force.</summary>
    public DateOnly EffectiveFrom { get; init; }

    /// <summary>The first day it is no longer in force; null while open.</summary>
    public DateOnly? EffectiveTo { get; init; }
    public required OeeTargetFiguresDto Targets { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Sets the site's or one piece of equipment's targets from a production day on (D-073). <see cref="Version"/> is the version of that
/// scope's latest period, 0 when it has none: someone else's change in between is refused.
/// </summary>
public sealed record SetOeeTargetRequest
{
    public Guid? EquipmentId { get; init; }
    public DateOnly EffectiveFrom { get; init; }
    public decimal? AvailabilityPct { get; init; }
    public decimal? PerformancePct { get; init; }
    public decimal? QualityPct { get; init; }
    public decimal? OeePct { get; init; }
    public long Version { get; init; }
}

/// <summary>Ends the scope's latest targets on a day (not in force that day); equipment then follows the site's.</summary>
public sealed record EndOeeTargetRequest
{
    public Guid? EquipmentId { get; init; }
    public DateOnly EndOn { get; init; }
    public long Version { get; init; }
}
