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
