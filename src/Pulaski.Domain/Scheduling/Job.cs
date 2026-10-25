using Pulaski.Domain.WorkOrders;

namespace Pulaski.Domain.Scheduling;

/// <summary>
/// A job: the piece of a work order that runs on one line (D-003). The planner places and
/// sequences jobs; dispatch and firm happen on the work order and carry its jobs with them
/// (D-018). Operators execute jobs. Pulaski only calculates the times (D-014).
/// A job may instead be planned to a group such as a department (D-022): it then holds a window
/// (<see cref="EarliestStart"/> to <see cref="LatestEnd"/>) until the floor assigns it to one of
/// the group's lines. <see cref="GroupEquipmentId"/> remembers the group after that.
/// A job made from a process route keeps its operation (<see cref="ProcessOperationId"/>, D-024).
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
    Guid? ReplacesJobId,
    Guid? GroupEquipmentId = null,
    DateTimeOffset? LatestEnd = null,
    Guid? ProcessOperationId = null,
    DateTimeOffset? CompletedAt = null)
{
    /// <summary>
    /// Planned with a partial last batch (D-067): the replacement for exactly what was left of a cancelled job. Its quantity needn't be
    /// whole batches on any line, and its last batch is reported as a partial batch without a stop reason.
    /// </summary>
    public bool PartialBatchPlanned { get; init; }

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
    /// A production job planned to a group (D-022): it waits there, in its window, for the floor to
    /// assign it to one of the group's lines. The window must have an end.
    /// </summary>
    /// <exception cref="DomainRuleException">The quantity or the window is invalid.</exception>
    public static Job CreateForGroup(
        Guid id,
        Guid siteId,
        Guid workOrderId,
        string jobNumber,
        Guid groupId,
        Guid materialId,
        decimal plannedQty,
        Guid uomId,
        decimal sequenceRank,
        DateTimeOffset? earliestStart,
        DateTimeOffset? latestEnd)
    {
        EnsurePositive(plannedQty);
        EnsureWindow(earliestStart, latestEnd, onGroup: true);
        var job = CreateProduction(id, siteId, workOrderId, jobNumber, groupId, materialId, plannedQty, uomId, sequenceRank, earliestStart);
        return job with { GroupEquipmentId = groupId, LatestEnd = latestEnd };
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

    /// <summary>Planned to a group and not yet taken by one of its lines (D-022).</summary>
    public bool IsOnGroup => GroupEquipmentId is { } group && group == EquipmentId;

    /// <summary>
    /// Checks the job may move. A planned job may move anywhere. A firmed job may only change
    /// line, keeping its time (D-019). Anything else stays put.
    /// </summary>
    /// <exception cref="DomainRuleException">The job is locked, dispatched, started or finished, or firmed and staying on its line.</exception>
    public void EnsureMovable(Guid toLineId)
    {
        if (GroupEquipmentId is not null)
        {
            throw new DomainRuleException($"Job {JobNumber} was planned to a group; assign it to one of the group's lines instead.");
        }

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

    /// <summary>
    /// Sets the pins: the lock, the earliest start and the finish-by (D-014). A job waiting on a
    /// group keeps a finish-by, since that is its window (D-022).
    /// </summary>
    /// <exception cref="DomainRuleException">The job is finished, or the window is invalid.</exception>
    public Job Pin(bool isLocked, DateTimeOffset? earliestStart, DateTimeOffset? latestEnd)
    {
        if (!IsOpen)
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; it can no longer be pinned.");
        }

        EnsureWindow(earliestStart, latestEnd, IsOnGroup);
        return this with { IsLocked = isLocked, EarliestStart = earliestStart, LatestEnd = latestEnd };
    }

    /// <summary>
    /// The floor's move of a job planned to a group (D-022): onto one of the group's lines, to
    /// another of them, or back to the group. Allowed whatever the plan status, because where the
    /// job runs is the floor's call; never for a locked or started job.
    /// </summary>
    /// <param name="targetId">A line of the group, or the group itself.</param>
    /// <param name="targetIsGroup">The target is the job's group itself, not a line.</param>
    /// <param name="sequenceRank">The job's rank on the target.</param>
    /// <exception cref="DomainRuleException">The job was not planned to a group, is locked or started, or the group is another one.</exception>
    public Job Assign(Guid targetId, bool targetIsGroup, decimal sequenceRank)
    {
        if (GroupEquipmentId is not { } group)
        {
            throw new DomainRuleException($"Job {JobNumber} was planned to a line, not a group; move it with the sequence instead.");
        }

        if (IsLocked)
        {
            throw new DomainRuleException($"Job {JobNumber} is locked; unlock it before moving it.");
        }

        if (Status is not (JobStatus.Planned or JobStatus.Dispatched or JobStatus.Firmed))
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; it can no longer be moved.");
        }

        if (targetIsGroup && targetId != group)
        {
            throw new DomainRuleException($"Job {JobNumber} can only go back to the group it was planned to.");
        }

        if (targetIsGroup && LatestEnd is null)
        {
            throw new DomainRuleException($"Job {JobNumber} needs a finish-by time before it goes back to its group.");
        }

        return this with { EquipmentId = targetId, SequenceRank = sequenceRank };
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

    /// <summary>
    /// Execution begins or resumes (D-035). A production job runs once its work order is on the floor
    /// (dispatched or firmed: starting firms the order first, in the handler); a paused job resumes; a
    /// non-production job, which belongs to no order, starts from planned too.
    /// </summary>
    /// <exception cref="DomainRuleException">The job can't start from its status, or is still waiting on a group.</exception>
    public Job Start()
    {
        if (IsOnGroup)
        {
            throw new DomainRuleException($"Job {JobNumber} is waiting on its group; choose the line it runs on.");
        }

        var startable = JobType == Scheduling.JobType.Production
            ? Status is JobStatus.Firmed or JobStatus.Paused
            : Status is JobStatus.Planned or JobStatus.Dispatched or JobStatus.Firmed or JobStatus.Paused;
        if (!startable)
        {
            throw new DomainRuleException(Status switch
            {
                JobStatus.Planned or JobStatus.Dispatched => $"Job {JobNumber} is {Status}; dispatch its work order before starting it.",
                JobStatus.Running => $"Job {JobNumber} is already running.",
                _ => $"Job {JobNumber} is {Status}; it can no longer be started.",
            });
        }

        return this with { Status = JobStatus.Running };
    }

    /// <summary>Stops the job for now; its run ends and a later start opens a new one (D-035).</summary>
    /// <exception cref="DomainRuleException">The job isn't running.</exception>
    public Job Pause()
    {
        EnsureStatus(JobStatus.Running, "paused");
        return this with { Status = JobStatus.Paused };
    }

    /// <summary>The job is done. Production can still be reported into its runs until it is closed (D-035).</summary>
    /// <exception cref="DomainRuleException">The job hasn't started, or is already finished.</exception>
    public Job Complete(DateTimeOffset at)
    {
        if (Status is not (JobStatus.Running or JobStatus.Paused))
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; only a running or paused job can be completed.");
        }

        return this with { Status = JobStatus.Completed, CompletedAt = at };
    }

    /// <summary>Takes a completed job back to paused, to run it again (D-035).</summary>
    /// <exception cref="DomainRuleException">The job isn't completed.</exception>
    public Job Reopen()
    {
        EnsureStatus(JobStatus.Completed, "reopened");
        return this with { Status = JobStatus.Paused, CompletedAt = null };
    }

    /// <summary>Closes a completed job with its work order; nothing more can be reported into it.</summary>
    /// <exception cref="DomainRuleException">The job isn't completed.</exception>
    public Job Close()
    {
        EnsureStatus(JobStatus.Completed, "closed");
        return this with { Status = JobStatus.Closed };
    }

    private void EnsureStatus(string expected, string action)
    {
        if (Status != expected)
        {
            throw new DomainRuleException($"Job {JobNumber} is {Status}; only {expected} jobs can be {action}.");
        }
    }

    private static void EnsureWindow(DateTimeOffset? earliestStart, DateTimeOffset? latestEnd, bool onGroup)
    {
        if (onGroup && latestEnd is null)
        {
            throw new DomainRuleException("A job planned to a group needs a finish-by time: its window must have an end.");
        }

        if (earliestStart is { } start && latestEnd is { } end && end <= start)
        {
            throw new DomainRuleException("The finish-by time must be after the earliest start.");
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
