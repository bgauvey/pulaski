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

    /// <summary>The first site-local day to show; today when left out.</summary>
    public DateOnly? From { get; init; }
    public int Days { get; init; } = 7;
}

/// <summary>A shift on the calendar, with its times in the site's time zone.</summary>
public sealed record ShiftOccurrenceDto(string Code, string Name, DateOnly ProductionDate, DateTimeOffset Start, DateTimeOffset End, string? Crew = null);

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
