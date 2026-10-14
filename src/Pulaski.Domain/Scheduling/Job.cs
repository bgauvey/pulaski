namespace Pulaski.Domain.Scheduling;

/// <summary>
/// A job (ISA-95 job order): one dispatchable unit on one schedulable line (D-003, D-009). The
/// planner owns its line, its rank and its pins; Pulaski only calculates its times (D-014).
/// </summary>
public sealed record Job(
    Guid Id,
    Guid SiteId,
    Guid? WorkOrderId,
    string JobNumber,
    string JobType,
    Guid EquipmentId,
    Guid? MaterialId,
    decimal? PlannedQty,
    Guid? UomId,
    DateTimeOffset? PlannedStart,
    DateTimeOffset? PlannedEnd,
    decimal SequenceRank,
    bool IsLocked,
    DateTimeOffset? EarliestStart,
    TimeSpan? PlannedDuration,
    bool CountsAsPlannedDowntime,
    string Status,
    DateTimeOffset? DispatchedAt)
{
    /// <summary>A production job for part of a work order, sized by the run standard.</summary>
    /// <exception cref="DomainRuleException">The quantity is invalid.</exception>
    public static Job CreateProduction(
        Guid id,
        Guid siteId,
        Guid workOrderId,
        string jobNumber,
        Guid lineId,
        Guid materialId,
        decimal plannedQty,
        Guid uomId,
        decimal sequenceRank,
        DateTimeOffset? earliestStart)
    {
        if (plannedQty <= 0)
        {
            throw new DomainRuleException("Planned quantity must be greater than zero.");
        }

        return new Job(
            id, siteId, workOrderId, jobNumber, Scheduling.JobType.Production, lineId, materialId, plannedQty, uomId,
            null, null, sequenceRank, false, earliestStart, null, false, JobStatus.Planned, null);
    }

    /// <summary>
    /// A non-production job (changeover, cleaning, trial, maintenance) with a fixed duration. A
    /// changeover may leave the duration out; the projection sizes it from the changeover matrix.
    /// </summary>
    /// <exception cref="DomainRuleException">The type or duration is invalid.</exception>
    public static Job CreateActivity(
        Guid id,
        Guid siteId,
        string jobType,
        string jobNumber,
        Guid lineId,
        TimeSpan? plannedDuration,
        bool countsAsPlannedDowntime,
        decimal sequenceRank,
        DateTimeOffset? earliestStart)
    {
        if (jobType == Scheduling.JobType.Production || !Scheduling.JobType.All.Contains(jobType))
        {
            throw new DomainRuleException($"'{jobType}' is not a non-production job type.");
        }

        if (plannedDuration is { } duration && duration <= TimeSpan.Zero)
        {
            throw new DomainRuleException("Planned duration must be greater than zero.");
        }

        return new Job(
            id, siteId, null, jobNumber, jobType, lineId, null, null, null,
            null, null, sequenceRank, false, earliestStart, plannedDuration, countsAsPlannedDowntime, JobStatus.Planned, null);
    }

    public bool IsOpen => JobStatus.Open.Contains(Status);

    /// <summary>Puts the job at a new rank, on the same line or another one.</summary>
    /// <exception cref="DomainRuleException">The job is locked or no longer plannable.</exception>
    public Job MoveTo(Guid lineId, decimal sequenceRank)
    {
        EnsureMovable();
        return this with { EquipmentId = lineId, SequenceRank = sequenceRank };
    }

    /// <exception cref="DomainRuleException">The job is locked or no longer plannable.</exception>
    public void EnsureMovable()
    {
        if (IsLocked)
        {
            throw new DomainRuleException($"Job {JobNumber} is locked; unlock it before moving it.");
        }

        if (!JobStatus.Resequenceable.Contains(Status))
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; only planned or scheduled jobs can be moved.");
        }
    }

    /// <summary>Sets the pins: the lock and the earliest start (D-014).</summary>
    /// <exception cref="DomainRuleException">The job is finished.</exception>
    public Job Pin(bool isLocked, DateTimeOffset? earliestStart)
    {
        if (!IsOpen)
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; it can no longer be pinned.");
        }

        return this with { IsLocked = isLocked, EarliestStart = earliestStart };
    }

    /// <summary>Releases the job to the floor.</summary>
    /// <exception cref="DomainRuleException">The job is already dispatched or finished.</exception>
    public Job Dispatch(DateTimeOffset at)
    {
        if (!JobStatus.Resequenceable.Contains(Status))
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; only planned or scheduled jobs can be dispatched.");
        }

        return this with { Status = JobStatus.Dispatched, DispatchedAt = at };
    }

    /// <summary>
    /// Records the projected times as the baseline. A planned job becomes scheduled. A zero-length
    /// projection (no run standard, no duration) has no end to record.
    /// </summary>
    /// <exception cref="DomainRuleException">The job has started or finished.</exception>
    public Job Publish(DateTimeOffset start, DateTimeOffset end)
    {
        if (!JobStatus.Publishable.Contains(Status))
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; only jobs that have not started can be published.");
        }

        return this with
        {
            PlannedStart = start,
            PlannedEnd = end > start ? end : null,
            Status = Status == JobStatus.Planned ? JobStatus.Scheduled : Status,
        };
    }
}
