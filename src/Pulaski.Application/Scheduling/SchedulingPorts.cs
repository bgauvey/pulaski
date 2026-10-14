using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Scheduling;

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

    /// <summary>The line's run standards by material code.</summary>
    Task<IReadOnlyList<RunStandardDto>> ListRunStandardsAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>
    /// The site's policy; a site with no policy row gets the defaults with <c>IsDefault</c> set.
    /// Null when <paramref name="siteId"/> is not a site.
    /// </summary>
    Task<SchedulingPolicyDto?> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>
/// Write side: runs inside the current <see cref="IUnitOfWork"/>. Commands lock the line (or
/// lines) first and the job second, so every scheduling change to a line serializes on the line
/// row and two commands never wait on each other in opposite order.
/// </summary>
public interface ISchedulingRepository
{
    /// <summary>Locks the equipment rows <c>FOR UPDATE</c> in a fixed order and returns those found.</summary>
    Task<IReadOnlyList<SchedulingLine>> LockLinesAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken);

    /// <summary>The line a job is on, read without a lock, so the caller can lock that line first.</summary>
    Task<Guid?> GetJobLineAsync(Guid jobId, CancellationToken cancellationToken);

    Task<Versioned<Job>?> GetJobForUpdateAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Open jobs on the line, locked <c>FOR UPDATE</c>.</summary>
    Task<IReadOnlyList<Versioned<Job>>> GetOpenJobsForUpdateAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>
    /// The ranks either side of the slot before <paramref name="beforeJobId"/> (or at the end of
    /// the line when null) among the line's open jobs, ignoring <paramref name="excludingJobId"/>.
    /// Null when <paramref name="beforeJobId"/> is not an open job on the line.
    /// </summary>
    Task<RankGap?> FindGapAsync(Guid lineId, Guid? beforeJobId, Guid? excludingJobId, CancellationToken cancellationToken);

    /// <summary>Renumbers the line's open jobs 1..n in their current order, to make room between ranks.</summary>
    Task RenumberAsync(Guid lineId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertJobAsync(Job job, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the planner-owned and lifecycle columns: line, rank, pins, status, dispatch time and baseline.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateJobAsync(Job job, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Appends to <c>ts.job_status_change</c> (D-010). <paramref name="fromStatus"/> is null for a new job.</summary>
    Task AppendStatusChangeAsync(Guid jobId, string? fromStatus, string toStatus, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The same plan inputs as <see cref="ISchedulingReadStore.GetLinePlanAsync"/>, read inside the transaction.</summary>
    Task<LinePlanSource?> GetLinePlanAsync(Guid lineId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    Task<Versioned<RunStandard>?> GetRunStandardForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes the modified-rate columns only; the calculated rate belongs to Pulaski (D-016).</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateRunStandardModificationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The site's changeover mode, locked <c>FOR UPDATE</c>; null when the site has no policy row.</summary>
    Task<Versioned<string>?> GetPolicyForUpdateAsync(Guid siteId, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertPolicyAsync(Guid siteId, string changeoverMode, DateTimeOffset at, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> UpdatePolicyAsync(Guid siteId, string changeoverMode, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>A line as scheduling commands see it, with its site's changeover mode.</summary>
public sealed record SchedulingLine(Guid Id, Guid? SiteId, string Code, bool IsSchedulable, string ChangeoverMode);

/// <summary>The ranks either side of an insertion point; null means the start or end of the line.</summary>
public sealed record RankGap(decimal? Before, decimal? After);

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
    public long Version { get; init; }
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
