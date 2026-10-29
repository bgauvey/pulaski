namespace Pulaski.Contracts.MachineData;

// ---- Utilization and OEE reports (D-052) ----

/// <summary>
/// Utilization and OEE between two production days (D-052): by equipment, by shift and by product, and where each piece of
/// equipment's time went. Worked out on read from the machine history; a period longer than 31 days from the daily rollups (#66).
/// </summary>
public sealed record OeeReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }

    /// <summary>Worked out from the daily rollups (#66), with the days not rolled yet from the machine history.</summary>
    public bool FromRollups { get; init; }

    /// <summary><see cref="Shifts"/> has a row per equipment and production day, not per shift.</summary>
    public bool ByDay { get; init; }

    /// <summary>Days of the period left out because their rollups are being worked out again; empty when every day is in.</summary>
    public IReadOnlyList<DateOnly> LeftOutDays { get; init; } = [];

    /// <summary>Equipment that tracks state, in tree order (the one asked for and everything below it, or all of the site).</summary>
    public IReadOnlyList<OeeEquipmentRowDto> Equipment { get; init; } = [];

    /// <summary>Each piece of equipment's shifts (whole days for equipment without shifts), in time order; by day when <see cref="ByDay"/>.</summary>
    public IReadOnlyList<OeeShiftRowDto> Shifts { get; init; } = [];

    /// <summary>Each material made in the period, over the runs of it.</summary>
    public IReadOnlyList<OeeProductRowDto> Products { get; init; } = [];

    /// <summary>Each piece of equipment's time by use and reason, longest first.</summary>
    public IReadOnlyList<UtilizationRowDto> Utilization { get; init; } = [];

    /// <summary>What the figures leave out, in words: runs without an ideal rate, for example.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>The time and OEE of some working time. Percentages are null when there is nothing to measure them against.</summary>
public sealed record OeeFiguresDto
{
    /// <summary>Time the calendar says it works: its shifts, or the whole day without shifts.</summary>
    public decimal ScheduledHours { get; init; }

    /// <summary>In a unit mode that isn't production time (Maintenance, Manual).</summary>
    public decimal NonProductionHours { get; init; }

    /// <summary>Stops with a planned reason: setup, changeover, cleaning, planned maintenance.</summary>
    public decimal PlannedStopHours { get; init; }

    /// <summary>Scheduled less non-production and planned stops: what availability is measured against.</summary>
    public decimal PlannedHours { get; init; }

    /// <summary>Executing, in micro-stops, or in stops with a runtime reason.</summary>
    public decimal RunHours { get; init; }
    public decimal DownHours { get; init; }

    /// <summary>Down time without a reason yet, of which no signal is the part with nothing from the machine.</summary>
    public decimal UncodedHours { get; init; }
    public decimal NoSignalHours { get; init; }

    /// <summary>Run time with no job running: its output isn't reported, so it isn't in performance.</summary>
    public decimal RunWithoutJobHours { get; init; }
    public int Stops { get; init; }
    public int MicroStops { get; init; }
    public decimal? AvailabilityPct { get; init; }
    public decimal? PerformancePct { get; init; }
    public decimal? QualityPct { get; init; }
    public decimal? OeePct { get; init; }

    /// <summary>Unrounded components used to aggregate OEE across equipment without averaging percentages.</summary>
    public decimal PlannedHoursForOee { get; init; }
    public decimal RunHoursForAvailability { get; init; }
    public decimal RatedRunHoursForPerformance { get; init; }
    public decimal IdealHours { get; init; }
    public decimal GoodIdealHours { get; init; }
}

public sealed record OeeEquipmentRowDto
{
    public required Guid EquipmentId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary>The nearest equipment above it that tracks state, as on the Machines board.</summary>
    public Guid? ParentId { get; init; }

    /// <summary>0 for the top of its group, 1 below it and so on.</summary>
    public int Depth { get; init; }

    /// <summary><c>own_tags</c>, <c>series</c>, <c>parallel</c> or <c>constraint_machine</c>.</summary>
    public string StateDerivation { get; init; } = "own_tags";

    /// <summary>Jobs run on it, so it has performance and quality; a machine below a line has availability only.</summary>
    public bool CanRunJob { get; init; }
    public required OeeFiguresDto Figures { get; init; }
}

public sealed record OeeShiftRowDto
{
    public DateOnly ProductionDate { get; init; }

    /// <summary>The shift; null for a whole day of equipment without shifts.</summary>
    public string? ShiftCode { get; init; }
    public string? ShiftName { get; init; }
    public string? CrewCode { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public bool CanRunJob { get; init; }
    public required OeeFiguresDto Figures { get; init; }
}

public sealed record OeeProductRowDto
{
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }

    /// <summary>The unit of the quantities: the jobs' unit.</summary>
    public required string UomCode { get; init; }
    public decimal GoodQty { get; init; }
    public decimal ScrapQty { get; init; }
    public int Runs { get; init; }

    /// <summary>The equipment it ran on, by code.</summary>
    public IReadOnlyList<string> EquipmentCodes { get; init; } = [];

    /// <summary>Over the working time inside its runs.</summary>
    public required OeeFiguresDto Figures { get; init; }
}

/// <summary>Part of a piece of equipment's scheduled time: one use, and one reason within it.</summary>
public sealed record UtilizationRowDto
{
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }

    /// <summary><c>run</c>, <c>micro_stop</c>, <c>down</c>, <c>planned</c> or <c>non_production</c>.</summary>
    public required string Use { get; init; }

    /// <summary>The reason; null for plain run time, micro-stops, non-production modes and down time not coded yet.</summary>
    public Guid? ReasonCodeId { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonName { get; init; }

    /// <summary>The groups the reason is filed under (D-062), top first, for rolling the rows up by group (#86); empty without a reason or groups.</summary>
    public IReadOnlyList<ReasonGroupDto> ReasonGroups { get; init; } = [];

    /// <summary>For down time without a reason: true when the machine sent nothing.</summary>
    public bool NoSignal { get; init; }
    public decimal Hours { get; init; }

    /// <summary>Of the equipment's scheduled time.</summary>
    public decimal SharePct { get; init; }
    public int Stops { get; init; }
}

/// <summary>A reason group (D-062), as a report rolls reasons up to it (#86).</summary>
public sealed record ReasonGroupDto(Guid Id, string Code, string Name);
