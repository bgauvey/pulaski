using Pulaski.Domain.WorkOrders;

namespace Pulaski.Domain.Scheduling;

/// <summary>
/// A job: the piece of a work order that runs on one line (D-003). The planner places and
/// sequences jobs; dispatch and firm happen on the work order and carry its jobs with them
/// (D-018). Operators execute jobs. Pulaski only calculates the times (D-014).
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
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? FirmedAt,
    Guid? ReplacesJobId)
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
        EnsurePositive(plannedQty);
        return new Job(
            id, siteId, workOrderId, jobNumber, Scheduling.JobType.Production, lineId, materialId, plannedQty, uomId,
            null, null, sequenceRank, false, earliestStart, null, false, JobStatus.Planned, null, null, null);
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
            null, null, sequenceRank, false, earliestStart, plannedDuration, countsAsPlannedDowntime, JobStatus.Planned,
            null, null, null);
    }

    public bool IsOpen => JobStatus.Open.Contains(Status);

    public bool IsFirmed => Status == JobStatus.Firmed;

    /// <summary>
    /// Checks the job may move. A planned job may move anywhere. A firmed job may only change
    /// line, keeping its time (D-019). Anything else stays put.
    /// </summary>
    /// <exception cref="DomainRuleException">The job is locked, dispatched, started or finished, or firmed and staying on its line.</exception>
    public void EnsureMovable(Guid toLineId)
    {
        if (IsLocked)
        {
            throw new DomainRuleException($"Job {JobNumber} is locked; unlock it before moving it.");
        }

        switch (Status)
        {
            case JobStatus.Planned:
                return;
            case JobStatus.Firmed when toLineId != EquipmentId:
                return;
            case JobStatus.Firmed:
                throw new DomainRuleException($"Job {JobNumber} is firmed; it can change machine but not move in time.");
            case JobStatus.Dispatched:
                throw new DomainRuleException($"Job {JobNumber} is dispatched; un-dispatch its work order before moving it.");
            default:
                throw new DomainRuleException($"Job {JobNumber} is {Status}; it can no longer be moved.");
        }
    }

    /// <summary>Puts the job at a new rank, on the same line or another one.</summary>
    /// <exception cref="DomainRuleException">See <see cref="EnsureMovable"/>.</exception>
    public Job MoveTo(Guid lineId, decimal sequenceRank)
    {
        EnsureMovable(lineId);
        return this with { EquipmentId = lineId, SequenceRank = sequenceRank };
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

    /// <summary>
    /// Publishes the job with its work order: records the projected times as the baseline. A
    /// zero-length projection (no run standard, no duration) has no end to record.
    /// </summary>
    /// <exception cref="DomainRuleException">The job is not planned.</exception>
    public Job Dispatch(DateTimeOffset at, DateTimeOffset start, DateTimeOffset end)
    {
        EnsureStatus(JobStatus.Planned, "dispatched");
        return this with
        {
            Status = JobStatus.Dispatched,
            DispatchedAt = at,
            PlannedStart = start,
            PlannedEnd = end > start ? end : null,
        };
    }

    /// <summary>Takes the job back to planned with its work order; the baseline is cleared.</summary>
    /// <exception cref="DomainRuleException">The job is not dispatched.</exception>
    public Job Undispatch()
    {
        EnsureStatus(JobStatus.Dispatched, "un-dispatched");
        return this with { Status = JobStatus.Planned, DispatchedAt = null, PlannedStart = null, PlannedEnd = null };
    }

    /// <summary>Freezes the job in time with its work order; its baseline start becomes its anchor (D-019).</summary>
    /// <exception cref="DomainRuleException">The job is not dispatched.</exception>
    public Job Firm(DateTimeOffset at)
    {
        EnsureStatus(JobStatus.Dispatched, "firmed");
        return this with { Status = JobStatus.Firmed, FirmedAt = at };
    }

    /// <exception cref="DomainRuleException">The job is already finished.</exception>
    public Job Cancel()
    {
        if (!IsOpen)
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; it can no longer be cancelled.");
        }

        return this with { Status = JobStatus.Cancelled };
    }

    /// <summary>
    /// A new job for the quantity this one did not make, in the same slot on the same line (D-019).
    /// It takes the plan state of its work order; a dispatched or firmed replacement starts now.
    /// </summary>
    /// <exception cref="DomainRuleException">Not a production job, nothing left to make, or the order is finished.</exception>
    public Job Replacement(Guid id, decimal remainingQty, string workOrderStatus, DateTimeOffset at)
    {
        if (JobType != Scheduling.JobType.Production)
        {
            throw new DomainRuleException($"Job {JobNumber} is a {JobType} job; only production jobs are replaced.");
        }

        if (remainingQty <= 0)
        {
            throw new DomainRuleException($"Job {JobNumber} has made its quantity; there is nothing left to replace.");
        }

        var status = workOrderStatus switch
        {
            WorkOrderStatus.Planned => JobStatus.Planned,
            WorkOrderStatus.Dispatched => JobStatus.Dispatched,
            WorkOrderStatus.Firmed or WorkOrderStatus.InProgress => JobStatus.Firmed,
            _ => throw new DomainRuleException($"The work order is {workOrderStatus}; it can no longer get jobs."),
        };
        var published = status != JobStatus.Planned;

        return this with
        {
            Id = id,
            JobNumber = JobNumber + "-R",
            PlannedQty = remainingQty,
            Status = status,
            IsLocked = false,
            PlannedStart = published ? at : null,
            PlannedEnd = null,
            DispatchedAt = published ? at : null,
            FirmedAt = status == JobStatus.Firmed ? at : null,
            ReplacesJobId = Id,
        };
    }

    private void EnsureStatus(string expected, string action)
    {
        if (Status != expected)
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; only {expected} jobs can be {action}.");
        }
    }

    private static void EnsurePositive(decimal plannedQty)
    {
        if (plannedQty <= 0)
        {
            throw new DomainRuleException("Planned quantity must be greater than zero.");
        }
    }
}
