using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Scheduling;

/// <summary>
/// Puts a job on a line before <see cref="BeforeJobId"/>, or at the end when it is null. Production
/// jobs take the work order's material and unit; other types take an optional duration.
/// </summary>
public sealed record CreateJobCommand(
    Guid LineId,
    string JobNumber,
    string JobType,
    Guid? WorkOrderId,
    decimal? PlannedQty,
    TimeSpan? PlannedDuration,
    bool CountsAsPlannedDowntime,
    DateTimeOffset? EarliestStart,
    Guid? BeforeJobId) : ICommand<CreatedResponse>;

/// <summary>Moves a job before <see cref="BeforeJobId"/> on <see cref="LineId"/>, or to the end of that line (D-014).</summary>
public sealed record SequenceJobCommand(Guid Id, long ExpectedVersion, Guid LineId, Guid? BeforeJobId) : ICommand<VersionResponse>;

public sealed record PinJobCommand(Guid Id, long ExpectedVersion, bool IsLocked, DateTimeOffset? EarliestStart) : ICommand<VersionResponse>;

/// <summary>Releases a job to the floor; in auto mode its changeover placeholder becomes a job (D-015).</summary>
public sealed record DispatchJobCommand(Guid Id, long ExpectedVersion) : ICommand<DispatchJobResponse>;

/// <summary>Copies the line's projected times into the jobs' baseline (D-014).</summary>
public sealed record PublishScheduleCommand(Guid LineId, int? HorizonDays = null) : ICommand<PublishScheduleResponse>;

internal sealed class CreateJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<CreateJobCommand, CreatedResponse>
{
    /// <summary>Work order statuses that may still get jobs.</summary>
    private static readonly string[] PlannableWorkOrders = [WorkOrderStatus.Planned, WorkOrderStatus.Released, WorkOrderStatus.InProgress];

    public async Task<CreatedResponse> HandleAsync(CreateJobCommand command, CancellationToken cancellationToken)
    {
        var production = command.JobType == JobType.Production;
        new Validator()
            .Require(command.LineId != Guid.Empty, nameof(command.LineId), "Required.")
            .RequireText(command.JobNumber, nameof(command.JobNumber), 50)
            .Require(JobType.All.Contains(command.JobType), nameof(command.JobType), $"Unknown job type '{command.JobType}'.")
            .Require(!production || command.WorkOrderId is not null, nameof(command.WorkOrderId), "Required for a production job.")
            .Require(production || command.WorkOrderId is null, nameof(command.WorkOrderId), "Only production jobs belong to a work order.")
            .Require(!production || command.PlannedQty is not null, nameof(command.PlannedQty), "Required for a production job.")
            .Require(command.PlannedQty is null || command.PlannedQty > 0, nameof(command.PlannedQty), "Must be greater than zero.")
            .Require(production || command.PlannedQty is null, nameof(command.PlannedQty), "Only production jobs have a quantity.")
            .Require(!production || command.PlannedDuration is null, nameof(command.PlannedDuration), "Production jobs are sized by the run standard.")
            .Require(command.PlannedDuration is null || command.PlannedDuration > TimeSpan.Zero, nameof(command.PlannedDuration), "Must be greater than zero.")
            .Require(!production || !command.CountsAsPlannedDowntime, nameof(command.CountsAsPlannedDowntime), "Production is never planned downtime.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var line = SchedulingRules.RequireLine(
            await scheduling.LockLinesAsync([command.LineId], cancellationToken), command.LineId);
        var at = timeProvider.GetUtcNow();
        var rank = await SchedulingRules.RankAsync(scheduling, line.Id, command.BeforeJobId, null, at, cancellationToken);

        Job job;
        if (production)
        {
            var workOrder = (await workOrders.GetForShareAsync(command.WorkOrderId!.Value, cancellationToken))?.Value
                ?? throw new DomainRuleException($"Work order {command.WorkOrderId} not found.");
            if (workOrder.SiteId != line.SiteId)
            {
                throw new DomainRuleException($"Work order {workOrder.OrderNumber} belongs to another site than {line.Code}.");
            }

            if (!PlannableWorkOrders.Contains(workOrder.Status))
            {
                throw new DomainRuleException($"Work order {workOrder.OrderNumber} is {workOrder.Status}; it cannot get new jobs.");
            }

            job = Job.CreateProduction(
                ids.NewId(), workOrder.SiteId, workOrder.Id, command.JobNumber.Trim(), line.Id,
                workOrder.MaterialId, command.PlannedQty!.Value, workOrder.UomId, rank, command.EarliestStart);
        }
        else
        {
            job = Job.CreateActivity(
                ids.NewId(), line.SiteId!.Value, command.JobType, command.JobNumber.Trim(), line.Id,
                command.PlannedDuration, command.CountsAsPlannedDowntime, rank, command.EarliestStart);
        }

        var envelope = events.Create(EventTypes.JobCreated, "job", job.Id, new { After = job });
        var version = await scheduling.InsertJobAsync(job, envelope.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(job.Id, null, job.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(job.Id, version);
    }
}

internal sealed class SequenceJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<SequenceJobCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SequenceJobCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.LineId != Guid.Empty, nameof(command.LineId), "Required.")
            .Require(command.BeforeJobId != command.Id, nameof(command.BeforeJobId), "A job cannot be placed before itself.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (current, lines) = await SchedulingRules.LockJobAsync(
            scheduling, command.Id, command.ExpectedVersion, command.LineId, cancellationToken);
        var target = SchedulingRules.RequireLine(lines, command.LineId);
        if (target.SiteId != current.Value.SiteId)
        {
            throw new DomainRuleException($"{target.Code} is in another site; jobs cannot move between sites.");
        }

        current.Value.EnsureMovable();
        var at = timeProvider.GetUtcNow();
        var rank = await SchedulingRules.RankAsync(scheduling, target.Id, command.BeforeJobId, command.Id, at, cancellationToken);
        var moved = current.Value.MoveTo(target.Id, rank);

        var envelope = events.Create(
            EventTypes.JobSequenced,
            "job",
            moved.Id,
            new
            {
                Before = new { current.Value.EquipmentId, current.Value.SequenceRank },
                After = new { moved.EquipmentId, moved.SequenceRank, command.BeforeJobId },
            });
        var version = await scheduling.UpdateJobAsync(moved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class PinJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<PinJobCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(PinJobCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // Pins don't change the sequence, so only the job is locked, not its line.
        var current = await scheduling.GetJobForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Job {command.Id} not found.");
        SchedulingRules.EnsureVersion(current, command.ExpectedVersion);

        var pinned = current.Value.Pin(command.IsLocked, command.EarliestStart);

        var envelope = events.Create(
            EventTypes.JobPinned,
            "job",
            pinned.Id,
            new
            {
                Before = new { current.Value.IsLocked, current.Value.EarliestStart },
                After = new { pinned.IsLocked, pinned.EarliestStart },
            });
        var version = await scheduling.UpdateJobAsync(pinned, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class DispatchJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<DispatchJobCommand, DispatchJobResponse>
{
    /// <summary>Work order statuses whose jobs may go to the floor.</summary>
    private static readonly string[] DispatchableWorkOrders = [WorkOrderStatus.Released, WorkOrderStatus.InProgress];

    public async Task<DispatchJobResponse> HandleAsync(DispatchJobCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (current, lines) = await SchedulingRules.LockJobAsync(
            scheduling, command.Id, command.ExpectedVersion, null, cancellationToken);
        var line = SchedulingRules.RequireLine(lines, current.Value.EquipmentId);
        var at = timeProvider.GetUtcNow();
        var dispatched = current.Value.Dispatch(at);

        if (current.Value.WorkOrderId is { } workOrderId)
        {
            var workOrder = (await workOrders.GetForShareAsync(workOrderId, cancellationToken))!.Value;
            if (!DispatchableWorkOrders.Contains(workOrder.Status))
            {
                throw new DomainRuleException($"Work order {workOrder.OrderNumber} is {workOrder.Status}; release it before dispatching its jobs.");
            }
        }

        Guid? changeoverJobId = null;
        if (line.ChangeoverMode == ChangeoverMode.Auto && current.Value.JobType == JobType.Production)
        {
            changeoverJobId = await MaterializeChangeoverAsync(line, current.Value, at, cancellationToken);
        }

        var envelope = events.Create(
            EventTypes.JobDispatched,
            "job",
            dispatched.Id,
            new
            {
                Before = new { current.Value.Status },
                After = new { dispatched.Status, dispatched.DispatchedAt },
                ChangeoverJobId = changeoverJobId,
            });
        var version = await scheduling.UpdateJobAsync(dispatched, envelope.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(dispatched.Id, current.Value.Status, dispatched.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DispatchJobResponse(version, changeoverJobId);
    }

    /// <summary>
    /// Turns the placeholder the projection puts in front of <paramref name="job"/> into a dispatched
    /// changeover job ranked just before it, so the changeover's runs and downtime attach somewhere
    /// (D-015). It has no fixed duration, so the projection keeps sizing it from the matrix.
    /// </summary>
    private async Task<Guid?> MaterializeChangeoverAsync(SchedulingLine line, Job job, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var source = await scheduling.GetLinePlanAsync(
            line.Id, at, at.AddDays(GetLineTimelineHandler.DefaultHorizonDays), cancellationToken);
        var timeline = TimelineProjector.Project(TimelineMapping.ToPlan(source!, at));
        var placeholder = timeline.Segments.FirstOrDefault(
            s => s.Kind == SegmentKind.ChangeoverPlaceholder && s.JobId == job.Id);
        if (placeholder is null)
        {
            return null;
        }

        var rank = await SchedulingRules.RankAsync(scheduling, line.Id, job.Id, null, at, cancellationToken);
        var changeover = Job.CreateActivity(
                ids.NewId(), job.SiteId, JobType.Changeover, job.JobNumber + "-CO", line.Id, null, false, rank, null)
            .Dispatch(at);

        var envelope = events.Create(
            EventTypes.JobCreated,
            "job",
            changeover.Id,
            new
            {
                After = changeover,
                ForJobId = job.Id,
                FromMaterialId = placeholder.FromMaterial?.Id,
                ToMaterialId = job.MaterialId,
                placeholder.Changeover,
            });
        await scheduling.InsertJobAsync(changeover, envelope.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(changeover.Id, null, changeover.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return changeover.Id;
    }
}

internal sealed class PublishScheduleHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<PublishScheduleCommand, PublishScheduleResponse>
{
    public async Task<PublishScheduleResponse> HandleAsync(PublishScheduleCommand command, CancellationToken cancellationToken)
    {
        var horizon = command.HorizonDays ?? GetLineTimelineHandler.DefaultHorizonDays;
        new Validator()
            .Require(
                horizon is > 0 and <= GetLineTimelineHandler.MaxHorizonDays,
                nameof(command.HorizonDays),
                $"Must be between 1 and {GetLineTimelineHandler.MaxHorizonDays}.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var lines = await scheduling.LockLinesAsync([command.LineId], cancellationToken);
        if (lines.Count == 0)
        {
            throw new NotFoundException($"Equipment {command.LineId} not found.");
        }

        var line = SchedulingRules.RequireLine(lines, command.LineId);
        var now = timeProvider.GetUtcNow();
        var source = await scheduling.GetLinePlanAsync(line.Id, now, now.AddDays(horizon), cancellationToken);
        var timeline = TimelineProjector.Project(TimelineMapping.ToPlan(source!, now));
        var jobs = (await scheduling.GetOpenJobsForUpdateAsync(line.Id, cancellationToken)).ToDictionary(j => j.Value.Id);

        // The envelope's time stamps every write, so the baseline and its event agree.
        var published = new List<(Job Before, Job After)>();
        foreach (var segment in timeline.Segments.Where(s => s.Kind == SegmentKind.Job))
        {
            if (jobs.TryGetValue(segment.JobId, out var job) && JobStatus.Publishable.Contains(job.Value.Status))
            {
                published.Add((job.Value, job.Value.Publish(segment.Start, segment.End)));
            }
        }

        var envelope = events.Create(
            EventTypes.SchedulePublished,
            "equipment",
            line.Id,
            new
            {
                ProjectedAt = timeline.ProjectedAt,
                Jobs = published.Select(p => new { p.After.Id, p.After.PlannedStart, p.After.PlannedEnd }),
            });

        var results = new List<PublishedJobDto>(published.Count);
        foreach (var (before, after) in published)
        {
            var version = await scheduling.UpdateJobAsync(after, envelope.OccurredAt, cancellationToken);
            if (after.Status != before.Status)
            {
                await scheduling.AppendStatusChangeAsync(after.Id, before.Status, after.Status, envelope.OccurredAt, cancellationToken);
            }

            results.Add(new PublishedJobDto(after.Id, after.PlannedStart!.Value, after.PlannedEnd, version));
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PublishScheduleResponse(line.Id, envelope.OccurredAt, results);
    }
}

/// <summary>Locking order, line checks and rank placement shared by the job commands.</summary>
internal static class SchedulingRules
{
    /// <summary>Locks the job's line (and <paramref name="otherLineId"/>, if any), then the job, and checks its version.</summary>
    public static async Task<(Versioned<Job> Job, IReadOnlyList<SchedulingLine> Lines)> LockJobAsync(
        ISchedulingRepository scheduling, Guid jobId, long expectedVersion, Guid? otherLineId, CancellationToken cancellationToken)
    {
        var lineId = await scheduling.GetJobLineAsync(jobId, cancellationToken)
            ?? throw new NotFoundException($"Job {jobId} not found.");
        Guid[] lineIds = otherLineId is { } other && other != lineId ? [lineId, other] : [lineId];
        var lines = await scheduling.LockLinesAsync(lineIds, cancellationToken);

        var job = await scheduling.GetJobForUpdateAsync(jobId, cancellationToken)
            ?? throw new NotFoundException($"Job {jobId} not found.");
        EnsureVersion(job, expectedVersion);
        if (job.Value.EquipmentId != lineId)
        {
            throw Changed(job.Value);
        }

        return (job, lines);
    }

    public static void EnsureVersion(Versioned<Job> job, long expectedVersion)
    {
        if (job.Version != expectedVersion)
        {
            throw Changed(job.Value);
        }
    }

    /// <exception cref="DomainRuleException">The line was not found or is not schedulable.</exception>
    public static SchedulingLine RequireLine(IReadOnlyList<SchedulingLine> lines, Guid lineId)
    {
        var line = lines.FirstOrDefault(l => l.Id == lineId)
            ?? throw new DomainRuleException($"Line {lineId} not found.");
        if (!line.IsSchedulable || line.SiteId is null)
        {
            throw new DomainRuleException($"{line.Code} is not schedulable; jobs cannot be placed on it.");
        }

        return line;
    }

    /// <summary>A rank for the slot before <paramref name="beforeJobId"/>; renumbers the line once if the ranks have run out of room.</summary>
    public static async Task<decimal> RankAsync(
        ISchedulingRepository scheduling,
        Guid lineId,
        Guid? beforeJobId,
        Guid? excludingJobId,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        var gap = await scheduling.FindGapAsync(lineId, beforeJobId, excludingJobId, cancellationToken)
            ?? throw new DomainRuleException($"Job {beforeJobId} is not an open job on this line.");
        if (SequenceRank.Between(gap.Before, gap.After) is { } rank)
        {
            return rank;
        }

        await scheduling.RenumberAsync(lineId, at, cancellationToken);
        gap = (await scheduling.FindGapAsync(lineId, beforeJobId, excludingJobId, cancellationToken))!;
        return SequenceRank.Between(gap.Before, gap.After)
            ?? throw new InvalidOperationException($"No room between ranks on line {lineId} after renumbering.");
    }

    private static ConflictException Changed(Job job) =>
        new($"Job {job.JobNumber} was changed by someone else. Reload and try again.");
}
