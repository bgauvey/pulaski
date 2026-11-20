namespace Pulaski.Contracts.Execution;

/// <summary>
/// What a line display board shows (#120): each line in the terminal's scope as it stands now, for a TV on the floor. Read-only; figures
/// cover each line's current shift so far (the site-local day without shifts), as on the Overview (D-070). A null figure is unavailable.
/// </summary>
public sealed record LineDisplayDto
{
    public required string Title { get; init; }
    public DateTimeOffset AsOf { get; init; }
    public IReadOnlyList<LineDisplayLineDto> Lines { get; init; } = [];
}

public sealed record LineDisplayLineDto
{
    public required Guid LineId { get; init; }
    public required string Code { get; init; }
    public string? Name { get; init; }

    /// <summary>
    /// What the board says of the line at a glance: <c>running</c>, <c>down</c>, <c>waiting</c>, <c>no_signal</c> or <c>idle</c> (no job running,
    /// or a line that doesn't report machine state and has no job).
    /// </summary>
    public required string Status { get; init; }

    /// <summary>The PackML state's name, when the line reports one.</summary>
    public string? StateName { get; init; }

    /// <summary>Since when it has been in that state.</summary>
    public DateTimeOffset? StateSince { get; init; }

    /// <summary>Why it is stopped: the stop's reason, else the machine's stop code text.</summary>
    public string? StopText { get; init; }

    /// <summary>The shift its figures are for; null without shifts (then the site-local day).</summary>
    public string? ShiftName { get; init; }
    public DateTimeOffset? ShiftEnd { get; init; }

    public LineDisplayJobDto? Running { get; init; }
    public LineDisplayJobDto? Next { get; init; }

    /// <summary>Good and scrap reported in its shift so far, by unit.</summary>
    public IReadOnlyList<LineDisplayOutputDto> Output { get; init; } = [];
    public decimal? DownMinutes { get; init; }
    public int? UncodedStops { get; init; }

    /// <summary>OEE so far this shift and its parts, in percent, with the target in force (D-073); null when not measured.</summary>
    public decimal? OeePct { get; init; }
    public decimal? AvailabilityPct { get; init; }
    public decimal? PerformancePct { get; init; }
    public decimal? QualityPct { get; init; }
    public decimal? OeeTargetPct { get; init; }
}

public sealed record LineDisplayJobDto
{
    public required string JobNumber { get; init; }
    public string? MaterialCode { get; init; }
    public decimal? PlannedQty { get; init; }
    public decimal GoodQty { get; init; }
    public string? UomCode { get; init; }
}

public sealed record LineDisplayOutputDto(string UomCode, decimal GoodQty, decimal ScrapQty);

/// <summary>
/// An operator station (#146): one line of an enrolled terminal, with who is signed on by badge. <see cref="Line"/> is null until the station
/// knows its line: the terminal's first line offered, the one asked for, or the only one; otherwise <see cref="Lines"/> offers them.
/// </summary>
public sealed record StationDto
{
    public required string TerminalName { get; init; }
    public required string TerminalCode { get; init; }

    /// <summary><c>badge</c> or <c>badge_plus_pin</c>.</summary>
    public string AuthMode { get; init; } = "badge";

    /// <summary>The lines the station can serve, to choose from.</summary>
    public IReadOnlyList<StationLineDto> Lines { get; init; } = [];
    public LineDisplayLineDto? Line { get; init; }

    /// <summary>The job running on the line, to act on (#147); null when none is.</summary>
    public RunningJobDto? RunningJob { get; init; }

    /// <summary>The line's open jobs after the running one, in sequence.</summary>
    public IReadOnlyList<BoardJobDto> Next { get; init; } = [];

    /// <summary>
    /// What reporting the running job at the station needs (#148), for a signed-on person whose role reports; null otherwise, or when no
    /// production job runs.
    /// </summary>
    public StationReportDto? Report { get; init; }

    /// <summary>
    /// The line's stops waiting for a reason (#149): on the line and the machines below it, over the last hours, the open one first. Shown
    /// even while the station waits, so it can ask for a badge.
    /// </summary>
    public IReadOnlyList<Pulaski.Contracts.MachineData.DowntimeDto> StopsToCode { get; init; } = [];

    /// <summary>The reasons each machine's stops may be coded with, by equipment, for a signed-on person whose role codes stops.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlyList<Pulaski.Contracts.Inventory.ReasonCodeDto>> CodingReasons { get; init; } =
        new Dictionary<Guid, IReadOnlyList<Pulaski.Contracts.Inventory.ReasonCodeDto>>();

    /// <summary>Why a run ends (reasons of kind <c>run_end</c> at the site), offered when pausing or completing at the station (#147).</summary>
    public IReadOnlyList<Pulaski.Contracts.Inventory.ReasonCodeDto> RunEndReasons { get; init; } = [];

    /// <summary>Who is signed on; null while it waits for a badge.</summary>
    public Guid? PersonId { get; init; }
    public string? PersonName { get; init; }

    /// <summary>
    /// Whether the signed-on person has a Pulaski user (#150), and what that user may do at the terminal's site: run jobs (start, pause,
    /// complete), report production, code stops. Without a user, only labor.
    /// </summary>
    public bool PersonIsUser { get; init; }
    public bool CanRunJobs { get; init; }
    public bool CanReport { get; init; }
    public bool CanCodeStops { get; init; }

    /// <summary>Minutes without anything done before the station signs its person off.</summary>
    public int IdleMinutes { get; init; }
}

public sealed record StationLineDto(Guid Id, string Code, string? Name);

/// <summary>A badge at the station, with its PIN on a terminal that asks for one; <see cref="Line"/> the line's code when chosen.</summary>
public sealed record StationSignOnRequest
{
    public string BadgeValue { get; init; } = "";
    public string? Pin { get; init; }
    public string? Line { get; init; }
}

/// <summary>The station as it stands, for the ticket of whoever is signed on (none: waiting).</summary>
public sealed record StationRequest
{
    public string? Ticket { get; init; }
    public string? Line { get; init; }
}

/// <summary>
/// The station, and the ticket that keeps its person signed on (#146): issued at sign-on for <c>StationIdle</c>; null when no one is signed on,
/// or their ticket ran out.
/// </summary>
public sealed record StationResponse
{
    public bool NeedsPin { get; init; }
    public string? Ticket { get; init; }
    public DateTimeOffset? TicketExpiresAt { get; init; }
    public StationDto? Station { get; init; }
}

/// <summary>
/// Start, pause or complete a job at an operator station (#147), as the person the ticket signs on: the job must be the one running on the
/// station's line or one of its next jobs.
/// </summary>
public sealed record StationJobRequest
{
    public string? Ticket { get; init; }
    public string? Line { get; init; }
    public Guid JobId { get; init; }
    public long Version { get; init; }
    public Guid? ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>What a station action did: its notes (a downstream job now ready, a changeover started), the renewed ticket and the station now.</summary>
public sealed record StationActionResponse
{
    public IReadOnlyList<string> Notes { get; init; } = [];
    public required string Ticket { get; init; }
    public DateTimeOffset TicketExpiresAt { get; init; }
    public required StationDto Station { get; init; }
}

/// <summary>
/// Reporting the running job at a station (#148): the job as the Floor sees it (its unit, batches and the lots being filled), what its
/// machine counted since the last report, and where output can go and why it was scrapped.
/// </summary>
public sealed record StationReportDto
{
    public required JobExecutionDto Job { get; init; }
    public MachineCountsDto? Machine { get; init; }
    public IReadOnlyList<Pulaski.Contracts.Inventory.InventoryLocationDto> Locations { get; init; } = [];
    public IReadOnlyList<Pulaski.Contracts.Inventory.ReasonCodeDto> ScrapReasons { get; init; } = [];

    /// <summary>Where good output goes unless chosen: where the job's lot is being filled, if one is.</summary>
    public Guid? DefaultLocationId { get; init; }
}

/// <summary>
/// A production report at a station (#148), as the signed-on person: <see cref="GoodQty"/> and <see cref="ScrapQty"/> in the job's unit, or
/// with <see cref="FromMachine"/> the machine's figures as the station showed them, in <see cref="UomId"/> and counted to
/// <see cref="MachineThrough"/> (D-097), so what is reported is what the operator saw.
/// </summary>
public sealed record StationReportRequest
{
    public string? Ticket { get; init; }
    public string? Line { get; init; }
    public Guid JobId { get; init; }
    public bool FromMachine { get; init; }
    public Guid? UomId { get; init; }
    public DateTimeOffset? MachineThrough { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public Guid? ScrapReasonCodeId { get; init; }
    public Guid? LocationId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>Code a stop at a station (#149), as the signed-on person: one of the station's stops to code, with its reason.</summary>
public sealed record StationStopRequest
{
    public string? Ticket { get; init; }
    public string? Line { get; init; }
    public Guid StopId { get; init; }
    public Guid ReasonCodeId { get; init; }
    public string? Comment { get; init; }
}
