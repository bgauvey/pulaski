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

/// <summary>
/// What placing a new job would do to a line, worked out by the projector and saved nowhere.
/// <see cref="Timeline"/> is the whole line with the job in place; the job's own segments (and
/// any changeover placeholder Pulaski would put before it) carry <see cref="PreviewJobId"/>.
/// </summary>
public sealed record JobPlacementPreviewDto
{
    public required Guid PreviewJobId { get; init; }
    public required LineTimelineDto Timeline { get; init; }

    /// <summary>Jobs already on the line that would start later, and by how much.</summary>
    public IReadOnlyList<JobDelayDto> Delayed { get; init; } = [];
}

public sealed record JobDelayDto(Guid JobId, string JobNumber, TimeSpan Delay);

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

    /// <summary>For ERP orders that aren't whole batches (D-066): <c>reject</c> (the default), <c>round_up</c>, <c>round_down</c> or <c>hold</c>.</summary>
    public string ErpBatchRule { get; init; } = "reject";
    public bool IsDefault { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// A planning rate worked out from run history (D-045), for review before it is accepted: the window, every run looked at with
/// its rate or why it was left out, and the rate the runs give, next to the run standard's rates.
/// </summary>
public sealed record RunHistoryRateDto
{
    public required Guid RunStandardId { get; init; }
    public long Version { get; init; }
    public string LineCode { get; init; } = "";
    public string MaterialCode { get; init; } = "";
    public string UomCode { get; init; } = "";
    public int Days { get; init; }
    public DateTimeOffset From { get; init; }
    public DateTimeOffset To { get; init; }

    public decimal CalculatedRatePerHour { get; init; }
    public string CalculationBasis { get; init; } = "";
    public decimal? ModifiedRatePerHour { get; init; }
    public decimal EffectiveRatePerHour { get; init; }

    /// <summary>The capability's ideal rate in the run standard's unit; null when there is none in effect or it doesn't convert.</summary>
    public decimal? IdealRatePerHour { get; init; }

    /// <summary>The rate the usable runs give (good ÷ hours); null when there are too few, with <see cref="NotEnough"/> saying why.</summary>
    public decimal? SuggestedRatePerHour { get; init; }
    public string? NotEnough { get; init; }
    public int UsedRuns { get; init; }
    public decimal UsedHours { get; init; }
    public decimal UsedGoodQty { get; init; }
    public decimal? SlowFence { get; init; }
    public decimal? FastFence { get; init; }
    public int MinRuns { get; init; }
    public decimal MinHours { get; init; }
    public IReadOnlyList<RunHistorySampleDto> Runs { get; init; } = [];
}

/// <summary>One run looked at: its good output in the planning rate's unit, its rate, and why it was left out, if it was.</summary>
public sealed record RunHistorySampleDto
{
    public Guid RunId { get; init; }
    public string JobNumber { get; init; } = "";
    public int RunNo { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public decimal Hours { get; init; }
    public decimal? GoodQty { get; init; }
    public decimal? RatePerHour { get; init; }
    public string? Excluded { get; init; }
}
