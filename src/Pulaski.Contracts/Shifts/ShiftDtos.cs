namespace Pulaski.Contracts.Shifts;

/// <summary>One shift of a schedule (D-038). <see cref="Start"/> is "HH:mm" in the site's time zone; days count from 0, the cycle's first day.</summary>
public sealed record ShiftDto
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Start { get; init; } = "06:00";
    public int Minutes { get; init; } = 480;
    public IReadOnlyList<int> Days { get; init; } = [];

    /// <summary>The code of the crew that works it (D-040); null for none.</summary>
    public string? Crew { get; init; }
}

/// <summary>
/// One date's change to a schedule (D-042): on <see cref="Date"/> (the production day) shift <see cref="Code"/> runs at
/// <see cref="Start"/> ("HH:mm", site time) for <see cref="Minutes"/>, replacing the cycle's shift of that code or adding one,
/// or, when <see cref="IsWorking"/> is false, doesn't run.
/// </summary>
public sealed record ShiftOverrideDto
{
    public DateOnly Date { get; init; }
    public string Code { get; init; } = "";
    public bool IsWorking { get; init; } = true;
    public string Name { get; init; } = "";
    public string Start { get; init; } = "06:00";
    public int Minutes { get; init; } = 480;
    public string? Crew { get; init; }
    public string? Note { get; init; }
}

/// <summary>A site's shift schedule and the equipment that follows it (D-038).</summary>
public sealed record ShiftScheduleDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public string? SiteCode { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int CycleDays { get; init; }
    public DateOnly CycleStart { get; init; }
    public IReadOnlyList<ShiftDto> Shifts { get; init; } = [];

    /// <summary>Changes to single dates, by date then code (D-042).</summary>
    public IReadOnlyList<ShiftOverrideDto> Overrides { get; init; } = [];
    public bool IsActive { get; init; }

    /// <summary>Working hours in one cycle.</summary>
    public decimal HoursPerCycle { get; init; }

    /// <summary>The codes of the equipment that has this as its own schedule (not those inheriting it).</summary>
    public IReadOnlyList<string> UsedBy { get; init; } = [];
    public long Version { get; init; }
}

public sealed record CreateShiftScheduleRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int CycleDays { get; init; } = 7;
    public DateOnly CycleStart { get; init; }
    public IReadOnlyList<ShiftDto> Shifts { get; init; } = [];
}

public sealed record UpdateShiftScheduleRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int CycleDays { get; init; } = 7;
    public DateOnly CycleStart { get; init; }
    public IReadOnlyList<ShiftDto> Shifts { get; init; } = [];
    public bool IsActive { get; init; } = true;
}

/// <summary>A schedule, saved or not, to see what it gives over the next days.</summary>
public sealed record PreviewShiftScheduleRequest
{
    public Guid SiteId { get; init; }
    public int CycleDays { get; init; } = 7;
    public DateOnly CycleStart { get; init; }
    public IReadOnlyList<ShiftDto> Shifts { get; init; } = [];

    /// <summary>The schedule's date overrides, applied to the shifts shown (D-042).</summary>
    public IReadOnlyList<ShiftOverrideDto> Overrides { get; init; } = [];

    /// <summary>The first site-local day to show; today when left out.</summary>
    public DateOnly? From { get; init; }
    public int Days { get; init; } = 7;
}

/// <summary>A shift on the calendar, with its times in the site's time zone; <c>IsOverride</c> when a date override set it (D-042).</summary>
public sealed record ShiftOccurrenceDto(string Code, string Name, DateOnly ProductionDate, DateTimeOffset Start, DateTimeOffset End, string? Crew = null,
    bool IsOverride = false);

/// <summary>Sets one date's override for a shift (D-042), adding it or replacing the one there.</summary>
public sealed record SetShiftOverrideRequest
{
    public long Version { get; init; }
    public DateOnly Date { get; init; }
    public string Code { get; init; } = "";
    public bool IsWorking { get; init; } = true;
    public string? Name { get; init; }

    /// <summary>"HH:mm"; left out, the cycle's start for this shift.</summary>
    public string? Start { get; init; }

    /// <summary>Left out, the cycle's length for this shift.</summary>
    public int? Minutes { get; init; }
    public string? Crew { get; init; }
    public string? Note { get; init; }
}

public sealed record ShiftPreviewDto
{
    /// <summary>What is wrong with the schedule; when there is anything, no shifts are listed.</summary>
    public IReadOnlyList<string> Problems { get; init; } = [];
    public IReadOnlyList<ShiftOccurrenceDto> Shifts { get; init; } = [];
    public decimal HoursPerCycle { get; init; }
    public string TimeZone { get; init; } = "";
}

/// <summary>
/// Which working time equipment follows (D-038): <c>inherit</c> (its parent's), <c>schedule</c> (its own, naming
/// <see cref="ShiftScheduleId"/>) or <c>around_the_clock</c>.
/// </summary>
public sealed record SetEquipmentCalendarRequest
{
    public long Version { get; init; }
    public string Mode { get; init; } = "inherit";
    public Guid? ShiftScheduleId { get; init; }
}
