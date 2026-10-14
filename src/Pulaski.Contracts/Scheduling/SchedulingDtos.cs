namespace Pulaski.Contracts.Scheduling;

/// <summary>
/// The dispatched work order's new version, the baseline published for each of its jobs, and
/// warnings about the order as a whole (e.g. job quantities that don't add up to the order's).
/// </summary>
public sealed record DispatchWorkOrderResponse(long Version, IReadOnlyList<BaselineJobDto> Jobs, IReadOnlyList<ScheduleWarningDto> Warnings);

/// <summary><see cref="PlannedEnd"/> is null when the projection had no duration to give.</summary>
public sealed record BaselineJobDto(
    Guid JobId,
    DateTimeOffset PlannedStart,
    DateTimeOffset? PlannedEnd,
    long Version,
    IReadOnlyList<ScheduleWarningDto> Warnings);

/// <summary>The cancelled job's new version, and its replacement if one was asked for.</summary>
public sealed record CancelJobResponse(long Version, Guid? ReplacementJobId);

/// <summary>A line/material run standard (D-016).</summary>
public sealed record RunStandardDto
{
    public required Guid Id { get; init; }
    public required Guid EquipmentId { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required Guid UomId { get; init; }
    public required string UomCode { get; init; }
    public decimal CalculatedRatePerHour { get; init; }
    public required string CalculationBasis { get; init; }
    public DateTimeOffset CalculatedAt { get; init; }
    public decimal? ModifiedRatePerHour { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public string? ModifiedBy { get; init; }
    public string? ModifiedNote { get; init; }

    /// <summary><c>modified</c> or <c>calculated</c>: which rate is in force.</summary>
    public required string RateSource { get; init; }

    public decimal EffectiveRatePerHour { get; init; }
    public long Version { get; init; }
}

/// <summary>A site's scheduling policy (D-015). <see cref="IsDefault"/> sites have no row yet and version 0.</summary>
public sealed record SchedulingPolicyDto
{
    public required Guid SiteId { get; init; }
    public required string ChangeoverMode { get; init; }
    public bool IsDefault { get; init; }
    public long Version { get; init; }
}
