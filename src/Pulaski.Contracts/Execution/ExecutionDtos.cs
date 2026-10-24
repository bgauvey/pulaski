using Pulaski.Contracts.Inventory;

namespace Pulaski.Contracts.Execution;

/// <summary>A line on the floor board (D-035): what runs on it now and what comes next.</summary>
public sealed record LineBoardRowDto
{
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public required string LineName { get; init; }

    /// <summary>A department (D-022): its jobs wait here for the floor to choose a line below it when starting.</summary>
    public bool IsGroup { get; init; }
    public RunningJobDto? Running { get; init; }

    /// <summary>The line's other open jobs in sequence, with whether each can start.</summary>
    public IReadOnlyList<BoardJobDto> Next { get; init; } = [];
}

/// <summary>A job on the board that isn't running.</summary>
public sealed record BoardJobDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public long Version { get; init; }
    public string? MaterialCode { get; init; }
    public string? WorkOrderNumber { get; init; }
    public decimal? PlannedQty { get; init; }
    public string? UomCode { get; init; }

    /// <summary>Good quantity across the job's runs so far, in its unit.</summary>
    public decimal GoodQty { get; init; }

    /// <summary>True when its work order is on the floor and its process links are met (D-035).</summary>
    public bool IsReady { get; init; }

    /// <summary>Why it can't start yet, when it can't.</summary>
    public string? Waiting { get; init; }

    /// <summary>When a lag runs out, if that is all it waits for.</summary>
    public DateTimeOffset? ReadyAt { get; init; }
}

/// <summary>The job running on a line, with its open run.</summary>
public sealed record RunningJobDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public long Version { get; init; }
    public string? MaterialCode { get; init; }
    public string? WorkOrderNumber { get; init; }
    public decimal? PlannedQty { get; init; }
    public string? UomCode { get; init; }
    public required Guid RunId { get; init; }
    public int RunNo { get; init; }
    public DateTimeOffset StartedAt { get; init; }

    /// <summary>Good and scrap across all the job's runs, in its unit.</summary>
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }

    /// <summary>Good and scrap in this run.</summary>
    public decimal RunGoodQty { get; init; }
    public decimal RunScrapQty { get; init; }

    /// <summary>The most this run can have made by now (elapsed time x ideal rate x tolerance), in the job's unit; null when it can't be worked out.</summary>
    public decimal? CapQty { get; init; }

    /// <summary>For a material made in batches (D-036): the batch size in its base unit, and the rule.</summary>
    public decimal? BatchSize { get; init; }
    public string? BatchRule { get; init; }
    public string? BaseUomCode { get; init; }
}

/// <summary>A job with its execution history (D-035).</summary>
public sealed record JobExecutionDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public long Version { get; init; }
    public required Guid SiteId { get; init; }
    public string? SiteTimeZone { get; init; }
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public bool IsOnGroup { get; init; }
    public Guid? WorkOrderId { get; init; }
    public string? WorkOrderNumber { get; init; }
    public string? WorkOrderStatus { get; init; }
    public Guid? MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public string? MaterialName { get; init; }
    public decimal? PlannedQty { get; init; }
    public Guid? UomId { get; init; }
    public string? UomCode { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>The capability's ideal rate in the job's unit, for showing caps; null when there is none or it doesn't convert.</summary>
    public decimal? IdealRatePerHour { get; init; }
    public decimal RateTolerancePct { get; init; }
    public bool IsReady { get; init; }
    public string? Waiting { get; init; }
    public DateTimeOffset? ReadyAt { get; init; }

    /// <summary>For a material made in batches (D-036): batches are reported in its base unit.</summary>
    public decimal? BatchSize { get; init; }
    public decimal BatchTolerancePct { get; init; }
    public string? BatchRule { get; init; }
    public Guid? BaseUomId { get; init; }
    public string? BaseUomCode { get; init; }
    public IReadOnlyList<JobRunDto> Runs { get; init; } = [];
    public IReadOnlyList<ProductionReportDto> Reports { get; init; } = [];
    public IReadOnlyList<PartialBatchDto> PartialBatches { get; init; } = [];
}

/// <summary>A batch cut short when a line stopped (D-036), and whether it is approved.</summary>
public sealed record PartialBatchDto
{
    public required Guid Id { get; init; }
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public string? LineCode { get; init; }
    public string? MaterialCode { get; init; }
    public required Guid RunId { get; init; }
    public int RunNo { get; init; }
    public required Guid LotId { get; init; }
    public string? LotNumber { get; init; }
    public decimal Quantity { get; init; }
    public string? UomCode { get; init; }
    public decimal BatchSize { get; init; }
    public required string BatchRule { get; init; }

    /// <summary><c>accepted</c> (near rule), <c>pending</c>, <c>approved</c> or <c>rejected</c>.</summary>
    public required string Status { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string? ReportedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public string? DecidedBy { get; init; }
    public string? DecisionComment { get; init; }
}

public sealed record JobRunDto
{
    public required Guid Id { get; init; }
    public int RunNo { get; init; }
    public required string LineCode { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? EndReason { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }

    /// <summary>The most it can hold: its length (so far) x ideal rate x tolerance, in the job's unit.</summary>
    public decimal? CapQty { get; init; }

    /// <summary>True when it ended with a reason: a partial batch may then be added to it later (D-036).</summary>
    public bool EndedWithReason { get; init; }
    public bool HasPartialBatch { get; init; }
}

/// <summary>One production report: good and scrap entered together, live or after the run.</summary>
public sealed record ProductionReportDto
{
    public required Guid RunId { get; init; }
    public int RunNo { get; init; }

    /// <summary>When it counts as made: the report time, or the run's end for one entered afterwards.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>When it was entered.</summary>
    public DateTimeOffset RecordedAt { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public string? ScrapReason { get; init; }
    public string? ActorId { get; init; }
    public string? Comment { get; init; }

    /// <summary>True when entered after its run ended.</summary>
    public bool IsLate { get; init; }

    /// <summary>The report's id (D-063); null for one recorded before reports had ids, which can't be reversed whole.</summary>
    public Guid? ReportId { get; init; }

    /// <summary>True once it has been reversed.</summary>
    public bool IsReversed { get; init; }
}

public sealed record StartJobRequest
{
    public long Version { get; init; }

    /// <summary>For a job waiting on a department: the line below it that runs it (D-022).</summary>
    public Guid? LineId { get; init; }

    /// <summary>Start the job itself even though the material changes on the line: no changeover is needed (D-015).</summary>
    public bool SkipChangeover { get; init; }
}

public sealed record PauseJobRequest
{
    public long Version { get; init; }

    /// <summary>Why the run ended, a reason of kind <c>run_end</c>; optional, but required with a partial batch.</summary>
    public Guid? ReasonCodeId { get; init; }
    public string? Comment { get; init; }

    /// <summary>The batch in progress when the line stopped, if any (D-036).</summary>
    public PartialBatchRequest? PartialBatch { get; init; }
}

/// <summary>A partial batch reported as the line stops (D-036): its quantity in the material's base unit, and where it goes.</summary>
public sealed record PartialBatchRequest
{
    public decimal Quantity { get; init; }
    public Guid LocationId { get; init; }
    public string? LotNumber { get; init; }
}

/// <summary>Completing a job, with a reason and a partial batch when it stopped short (D-036).</summary>
public sealed record CompleteJobRequest
{
    public long Version { get; init; }
    public Guid? ReasonCodeId { get; init; }
    public string? Comment { get; init; }
    public PartialBatchRequest? PartialBatch { get; init; }
}

/// <summary>Approves or rejects a partial batch waiting for approval; rejecting needs a scrap reason.</summary>
public sealed record DecidePartialBatchRequest
{
    public Guid? ScrapReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

public sealed record JobVersionRequest
{
    public long Version { get; init; }
}

/// <summary>
/// Good and scrap made by a production job (D-035). Without a run it goes into the open run; with
/// one it goes into that run, also after it ended, until the job is closed. Good output is put into
/// a lot at <see cref="LocationId"/>.
/// </summary>
public sealed record ReportProductionRequest
{
    public Guid? RunId { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }

    /// <summary>The unit of both quantities; the job's unit when left out.</summary>
    public Guid? UomId { get; init; }

    /// <summary>A reason of kind <c>scrap</c>; required when there is scrap.</summary>
    public Guid? ScrapReasonCodeId { get; init; }

    /// <summary>Where the good output goes; required when there is good output.</summary>
    public Guid? LocationId { get; init; }
    public string? LotNumber { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public string? Comment { get; init; }

    /// <summary>
    /// For a material made in batches (D-036), instead of the quantities: the weight of each good batch
    /// and of each rejected one, in its base unit. Each good batch is its own lot.
    /// </summary>
    public IReadOnlyList<decimal> GoodBatches { get; init; } = [];
    public IReadOnlyList<decimal> ScrapBatches { get; init; } = [];

    /// <summary>A partial batch added to a run that ended with a reason, in the base unit.</summary>
    public decimal? PartialQty { get; init; }
}

/// <summary>What a start, pause, completion or reopening did, with notes such as downstream jobs now ready.</summary>
/// <param name="JobId">The job that started: the changeover put in front of it when one was needed.</param>
/// <param name="Version">That job's new version.</param>
/// <param name="RunId">The run opened, for a start.</param>
/// <param name="Notes">Plain-language notes for the operator.</param>
public sealed record ExecutionResponse(Guid JobId, long Version, Guid? RunId, IReadOnlyList<string> Notes);

/// <summary>A report's result: the run's totals and the lot the good output went into.</summary>
/// <summary>Reverses a production report whole (D-063), for an adjustment reason and a comment.</summary>
public sealed record ReverseProductionRequest
{
    public Guid ReasonCodeId { get; init; }
    public string Comment { get; init; } = "";
}

/// <summary>A reversed report's run as it is now, and every lot it took back.</summary>
public sealed record ReverseProductionResponse
{
    public required Guid RunId { get; init; }
    public decimal RunGoodQty { get; init; }
    public decimal RunScrapQty { get; init; }
    public IReadOnlyList<LotTransactionResponse> Lots { get; init; } = [];
}

public sealed record ReportProductionResponse
{
    /// <summary>The report (D-063), to reverse it by.</summary>
    public Guid ReportId { get; init; }
    public required Guid RunId { get; init; }
    public decimal RunGoodQty { get; init; }
    public decimal RunScrapQty { get; init; }

    /// <summary>The run's cap now, in the job's unit; null when it couldn't be checked.</summary>
    public decimal? CapQty { get; init; }
    public LotTransactionResponse? Lot { get; init; }

    /// <summary>Every lot the report made: one per good batch for a batch material.</summary>
    public IReadOnlyList<LotTransactionResponse> Lots { get; init; } = [];
    public IReadOnlyList<InventoryWarningDto> Warnings { get; init; } = [];
}

/// <summary>A site's tolerances for production reporting (D-035).</summary>
public sealed record ExecutionPolicyDto
{
    public required Guid SiteId { get; init; }
    public decimal RateTolerancePct { get; init; }
    public decimal ConsumptionTolerancePct { get; init; }

    /// <summary>True when the site has no policy of its own and uses the defaults.</summary>
    public bool IsDefault { get; init; }

    /// <summary>0 while the site uses the defaults.</summary>
    public long Version { get; init; }
}

/// <summary>Version 0 when the site still uses the default tolerances.</summary>
public sealed record SetExecutionPolicyRequest
{
    public long Version { get; init; }
    public decimal RateTolerancePct { get; init; } = 110m;
    public decimal ConsumptionTolerancePct { get; init; } = 110m;
}

/// <summary>A partial batch's decision (D-036) and what it did.</summary>
public sealed record PartialBatchDecisionResponse(Guid Id, string Status, IReadOnlyList<string> Notes);

/// <summary>
/// Good and scrap by shift (D-044) for a site's production days <see cref="From"/> to <see cref="To"/>: one row per production
/// day, shift, line, job and unit. <see cref="Jobs"/> checks each job's rows against its runs' totals.
/// </summary>
public sealed record ProductionByShiftDto
{
    public required Guid SiteId { get; init; }
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public string TimeZone { get; init; } = "";
    public IReadOnlyList<ShiftProductionRowDto> Rows { get; init; } = [];
    public IReadOnlyList<ShiftProductionJobDto> Jobs { get; init; } = [];
}

/// <summary>
/// What one job made on one line in one shift. With no <see cref="ShiftCode"/>, what it made on that production day outside any
/// shift (or on a line without shifts). Times are in the site's time zone.
/// </summary>
public sealed record ShiftProductionRowDto
{
    public DateOnly ProductionDate { get; init; }
    public string? ScheduleCode { get; init; }
    public string? ShiftCode { get; init; }
    public string? ShiftName { get; init; }
    public string? Crew { get; init; }
    public DateTimeOffset? ShiftStart { get; init; }
    public DateTimeOffset? ShiftEnd { get; init; }
    public Guid EquipmentId { get; init; }
    public string LineCode { get; init; } = "";
    public Guid JobId { get; init; }
    public string JobNumber { get; init; } = "";
    public string? WorkOrderNumber { get; init; }
    public string MaterialCode { get; init; } = "";
    public string MaterialName { get; init; } = "";
    public string UomCode { get; init; } = "";
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }

    /// <summary>How many production reports the row adds up.</summary>
    public int Reports { get; init; }
}

/// <summary>
/// A job's good and scrap in the report against what its runs hold (the job page's totals). <see cref="MatchesRuns"/> when they
/// are equal: every report of the job falls in the report's days. Otherwise the rest was reported on other days.
/// </summary>
public sealed record ShiftProductionJobDto
{
    public Guid JobId { get; init; }
    public string JobNumber { get; init; } = "";
    public string UomCode { get; init; } = "";
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public decimal RunGoodQty { get; init; }
    public decimal RunScrapQty { get; init; }
    public bool MatchesRuns { get; init; }
}
