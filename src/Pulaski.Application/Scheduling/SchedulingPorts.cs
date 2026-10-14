namespace Pulaski.Application.Scheduling;

/// <summary>Read side: everything the timeline projector needs for one line, straight from SQL.</summary>
public interface ISchedulingReadStore
{
    /// <summary>
    /// The line, its open jobs in planner sequence, its rates and changeover rules, and the shift
    /// windows and exceptions overlapping <paramref name="windowStart"/> to <paramref name="windowEnd"/>.
    /// Null if the equipment does not exist.
    /// </summary>
    Task<LinePlanSource?> GetLinePlanAsync(Guid lineId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);
}

public sealed record LinePlanSource
{
    public required SchedulingLineRow Line { get; init; }

    /// <summary>The material of the last production run that ended on the line.</summary>
    public PlanMaterialRow? PreviousMaterial { get; init; }

    public IReadOnlyList<PlanJobRow> Jobs { get; init; } = [];
    public IReadOnlyList<RunRateRow> Rates { get; init; } = [];
    public IReadOnlyList<ChangeoverRuleRow> ChangeoverRules { get; init; } = [];
    public IReadOnlyList<StandardChangeoverRow> StandardChangeovers { get; init; } = [];

    /// <summary>True when the line or an ancestor has shift instances at all.</summary>
    public bool HasShiftCalendar { get; init; }

    public IReadOnlyList<WindowRow> Shifts { get; init; } = [];
    public IReadOnlyList<WindowRow> Exceptions { get; init; } = [];
}

public sealed record SchedulingLineRow
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsSchedulable { get; init; }
    public required string ChangeoverMode { get; init; }
}

public sealed record PlanMaterialRow
{
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public Guid? MaterialClassId { get; init; }
}

public sealed record PlanJobRow
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public Guid? MaterialId { get; init; }
    public string? MaterialCode { get; init; }
    public Guid? MaterialClassId { get; init; }
    public decimal? PlannedQty { get; init; }
    public Guid? UomId { get; init; }
    public string? UomCode { get; init; }
    public decimal GoodQty { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EarliestStart { get; init; }
    public TimeSpan? PlannedDuration { get; init; }
    public DateTimeOffset? PlannedStart { get; init; }
    public DateTimeOffset? PlannedEnd { get; init; }
    public bool IsLocked { get; init; }
    public string? WorkOrderNumber { get; init; }
    public DateTimeOffset? DueAt { get; init; }
}

public sealed record RunRateRow
{
    public required Guid MaterialId { get; init; }
    public required Guid UomId { get; init; }
    public decimal RatePerHour { get; init; }
    public required string RateSource { get; init; }
}

public sealed record ChangeoverRuleRow
{
    public required Guid Id { get; init; }
    public Guid? EquipmentId { get; init; }
    public Guid? FromMaterialId { get; init; }
    public Guid? FromMaterialClassId { get; init; }
    public Guid? ToMaterialId { get; init; }
    public Guid? ToMaterialClassId { get; init; }
    public TimeSpan Duration { get; init; }
}

public sealed record StandardChangeoverRow
{
    public required Guid MaterialId { get; init; }
    public TimeSpan StandardChangeover { get; init; }
}

public sealed record WindowRow
{
    public DateTimeOffset StartsAt { get; init; }
    public DateTimeOffset EndsAt { get; init; }
}
