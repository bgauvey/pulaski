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
/// <param name="Version">The cancelled job's new version.</param>
/// <param name="ReplacementJobId">The replacement, when one was made.</param>
/// <param name="Note">For a replacement with a partial last batch (D-067): its batches, and what happens when the partial is reported.</param>
public sealed record CancelJobResponse(long Version, Guid? ReplacementJobId, string? Note = null);

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

    /// <summary>Stops inside the runs were taken out of their hours (#70): the rate is good ÷ running time.</summary>
    public bool StopsTakenOut { get; init; }

    /// <summary>Why stops couldn't be taken out though asked (the line records no machine state); null otherwise.</summary>
    public string? StopsNote { get; init; }

    /// <summary>The site's default for taking stops out, which the review opens on.</summary>
    public bool SiteTakesOutStops { get; init; }

    /// <summary>The used runs' stopped hours: taken out of <see cref="UsedHours"/> when <see cref="StopsTakenOut"/>.</summary>
    public decimal UsedStoppedHours { get; init; }

    /// <summary>The basis accepting it records: <c>run_history</c> or <c>run_history_net</c>.</summary>
    public string SuggestedBasis { get; init; } = "run_history";

    /// <summary>The scheduled recalculation's proposal waiting for review, if any (#70).</summary>
    public RateProposalDto? Proposal { get; init; }
}

/// <summary>
/// A planning rate the scheduled recalculation proposed (#70, D-098), with what it was worked out from and the run standard as it is now. Pending
/// until a planner accepts or rejects it, the site accepts it without review, or a newer one replaces it.
/// </summary>
public sealed record RateProposalDto
{
    public Guid Id { get; init; }
    public Guid RunStandardId { get; init; }

    /// <summary>The run standard's version, for accepting it.</summary>
    public long RunStandardVersion { get; init; }
    public Guid LineId { get; init; }
    public string LineCode { get; init; } = "";
    public Guid MaterialId { get; init; }
    public string MaterialCode { get; init; } = "";
    public string MaterialName { get; init; } = "";
    public string UomCode { get; init; } = "";
    public decimal ProposedRatePerHour { get; init; }

    /// <summary><c>run_history</c> or <c>run_history_net</c> (stops taken out).</summary>
    public string CalculationBasis { get; init; } = "";
    public int WindowDays { get; init; }
    public DateTimeOffset WindowFrom { get; init; }
    public DateTimeOffset WindowTo { get; init; }
    public int UsedRuns { get; init; }
    public decimal UsedHours { get; init; }
    public decimal UsedGoodQty { get; init; }
    public decimal StoppedHours { get; init; }

    /// <summary>The calculated rate when it was proposed.</summary>
    public decimal CalculatedRatePerHour { get; init; }

    /// <summary>The run standard now: its calculated rate and basis, and a modified rate, which stays in force.</summary>
    public decimal CurrentCalculatedRatePerHour { get; init; }
    public string CurrentBasis { get; init; } = "";
    public decimal? ModifiedRatePerHour { get; init; }

    /// <summary>Why it waits for review rather than being accepted by the site's opt-in.</summary>
    public string? ReviewReason { get; init; }

    /// <summary><c>pending</c>, <c>accepted</c>, <c>auto_accepted</c>, <c>rejected</c> or <c>superseded</c>.</summary>
    public string Status { get; init; } = "pending";
    public DateTimeOffset ProposedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecisionNote { get; init; }
}

/// <summary>How a site keeps its planning rates from run history current (#70, D-098).</summary>
public sealed record RateRefreshSettingsDto
{
    public Guid SiteId { get; init; }

    /// <summary>Reviews open with stops taken out, and the scheduled recalculation takes them out.</summary>
    public bool TakeOutStops { get; init; }

    /// <summary>A recalculation every <see cref="EveryDays"/> proposes rates for review.</summary>
    public bool IsScheduled { get; init; }
    public int EveryDays { get; init; } = 7;

    /// <summary>Runs that ended in this many days.</summary>
    public int WindowDays { get; init; } = 90;

    /// <summary>Only changes of more than this, in percent of the calculated rate, are proposed.</summary>
    public decimal ThresholdPct { get; init; } = 5m;

    /// <summary>The site opts in to accepting, without review, changes within <see cref="AutoAcceptLimitPct"/> to rates already from run history.</summary>
    public bool AutoAccept { get; init; }
    public decimal AutoAcceptLimitPct { get; init; } = 10m;

    public DateTimeOffset? RefreshedAt { get; init; }
    public long Version { get; init; }
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

    /// <summary>Hours it was stopped (#70); null when its stops aren't known.</summary>
    public decimal? StoppedHours { get; init; }
}
