namespace Pulaski.Contracts.Overview;

/// <summary>
/// The Overview's live figures for one site (D-070), worked out on read. Each line's figures cover its current shift so far, or
/// the site-local day so far on a line without shifts. A null figure is unavailable (not measured), never zero.
/// </summary>
public sealed record OverviewDto
{
    public required Guid SiteId { get; init; }
    public DateTimeOffset AsOf { get; init; }

    /// <summary>
    /// The span the line strips cover: from the earliest current shift's start to the latest one's end (<see cref="WindowIsShift"/>),
    /// else six hours back to eighteen ahead.
    /// </summary>
    public DateTimeOffset WindowStart { get; init; }
    public DateTimeOffset WindowEnd { get; init; }
    public bool WindowIsShift { get; init; }
    public IReadOnlyList<OverviewLineDto> Lines { get; init; } = [];

    /// <summary>Lots at the site with stock, on hold and in quarantine.</summary>
    public int LotsOnHold { get; init; }
    public int LotsInQuarantine { get; init; }
}

/// <summary>A line on the Overview: its live state and what it has done in its shift so far.</summary>
public sealed record OverviewLineDto
{
    public required Guid EquipmentId { get; init; }
    public required string Code { get; init; }

    /// <summary>False when the line reports no machine state: its state, downtime and stops are then null.</summary>
    public bool TracksState { get; init; }

    /// <summary>The shift it is on now; null for a line without shifts, or between shifts, whose figures cover the site-local day.</summary>
    public OverviewShiftDto? Shift { get; init; }

    /// <summary>When the time its figures cover began: the shift's start, else the site-local midnight.</summary>
    public DateTimeOffset From { get; init; }

    /// <summary>The OEE category of its state now (as on the Machines board): <c>producing</c>, <c>waiting_external</c>,
    /// <c>held_internal</c>, <c>stopped_faulted</c>, <c>ready_transitioning</c> or <c>no_signal</c>; null when it doesn't track state.</summary>
    public string? Category { get; init; }
    public string? PackMlName { get; init; }
    public DateTimeOffset? StateSince { get; init; }

    /// <summary>Why it is stopped: the open stop's reason, else the machine's stop code text.</summary>
    public string? StopText { get; init; }

    /// <summary>Its edge node has been quiet for over 2 minutes.</summary>
    public bool EdgeQuiet { get; init; }

    /// <summary>Good and scrap reported (D-035) in its shift, by unit; empty when nothing was reported.</summary>
    public IReadOnlyList<OverviewOutputDto> Output { get; init; } = [];

    /// <summary>Minutes down in its shift (availability loss: stops, no signal and not executing), and by reason, most first.</summary>
    public decimal? DownMinutes { get; init; }
    public IReadOnlyList<OverviewReasonDto> DownReasons { get; init; } = [];

    /// <summary>Stops in its shift with no reason yet (micro-stops left out).</summary>
    public int? UncodedStops { get; init; }

    /// <summary>What its time was used for from the window's start (or <see cref="From"/>, if earlier) to now; empty when it doesn't track state.</summary>
    public IReadOnlyList<OverviewStateSpanDto> States { get; init; } = [];
}

public sealed record OverviewShiftDto
{
    public DateOnly ProductionDate { get; init; }
    public required string ShiftCode { get; init; }
    public string? ShiftName { get; init; }
    public string? Crew { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
}

public sealed record OverviewOutputDto
{
    public required string UomCode { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
}

/// <summary>Down time with one reason; <see cref="ReasonCode"/> is null for stops not coded yet and for no signal (<see cref="NoSignal"/>).</summary>
public sealed record OverviewReasonDto
{
    public string? ReasonCode { get; init; }
    public required string Name { get; init; }
    public bool NoSignal { get; init; }
    public decimal Minutes { get; init; }
}

/// <summary>A stretch of a line's time: <c>run</c>, <c>down</c>, <c>no_signal</c>, <c>planned</c> or <c>non_production</c>.</summary>
public sealed record OverviewStateSpanDto
{
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public required string Use { get; init; }
}
