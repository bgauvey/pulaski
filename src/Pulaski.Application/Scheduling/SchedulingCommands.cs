using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.Units;
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
/// <see cref="LineId"/> may be a group such as a department (D-022): the job then waits there in
/// its window, <see cref="EarliestStart"/> to <see cref="LatestEnd"/>, for a line to take it.
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
    Guid? BeforeJobId,
    DateTimeOffset? LatestEnd = null) : ICommand<CreatedResponse>;

/// <summary>
/// Moves a job before <see cref="BeforeJobId"/> on <see cref="LineId"/>, or to the end of that line
/// (D-014). A firmed job may only change line; it keeps its time, which decides its place (D-019).
/// </summary>
public sealed record SequenceJobCommand(Guid Id, long ExpectedVersion, Guid LineId, Guid? BeforeJobId) : ICommand<VersionResponse>;

public sealed record PinJobCommand(
    Guid Id, long ExpectedVersion, bool IsLocked, DateTimeOffset? EarliestStart, DateTimeOffset? LatestEnd = null) : ICommand<VersionResponse>;

/// <summary>
/// The floor's move of a job planned to a group (D-022): onto one of the group's lines that can
/// run it (before <see cref="BeforeJobId"/>, or at the end), to another of them, or back to the
/// group when <see cref="TargetId"/> is the group.
/// </summary>
public sealed record AssignJobCommand(Guid Id, long ExpectedVersion, Guid TargetId, Guid? BeforeJobId) : ICommand<VersionResponse>;

/// <summary>Cancels a job; with <see cref="Replace"/>, a new job takes its slot for the quantity it did not make (D-019).</summary>
public sealed record CancelJobCommand(Guid Id, long ExpectedVersion, string? Reason, bool Replace) : ICommand<CancelJobResponse>;

internal sealed class CreateJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<CreateJobCommand, CreatedResponse>
{
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
            .Require(
                command.LatestEnd is null || command.EarliestStart is null || command.LatestEnd > command.EarliestStart,
                nameof(command.LatestEnd),
                "Must be after the earliest start.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var line = SchedulingRules.RequireLine(
            await scheduling.LockLinesAsync([command.LineId], cancellationToken), command.LineId);
        var at = timeProvider.GetUtcNow();

        WorkOrder? workOrder = null;
        if (production)
        {
            workOrder = (await workOrders.GetForShareAsync(command.WorkOrderId!.Value, cancellationToken))?.Value
                ?? throw new DomainRuleException($"Work order {command.WorkOrderId} not found.");
            if (workOrder.SiteId != line.SiteId)
            {
                throw new DomainRuleException($"Work order {workOrder.OrderNumber} belongs to another site than {line.Code}.");
            }

            // A dispatched order's plan is published; un-dispatch it to change its jobs (D-018).
            if (workOrder.Status != WorkOrderStatus.Planned)
            {
                throw new DomainRuleException($"Work order {workOrder.OrderNumber} is {workOrder.Status}; only planned orders get new jobs.");
            }

            await SchedulingRules.EnsureCanRunAsync(scheduling, line, workOrder.MaterialId, at, cancellationToken);
        }
        else if (line.IsGroup)
        {
            throw new DomainRuleException($"{line.Code} is a group; only production jobs are planned to it. Put {command.JobType} jobs on a line.");
        }

        var rank = await SchedulingRules.RankAsync(scheduling, line.Id, command.BeforeJobId, null, at, cancellationToken);
        Job job;
        if (workOrder is null)
        {
            job = Job.CreateActivity(
                ids.NewId(), line.SiteId!.Value, command.JobType, command.JobNumber.Trim(), line.Id,
                command.PlannedDuration, command.CountsAsPlannedDowntime, rank, command.EarliestStart);
            job = job with { LatestEnd = command.LatestEnd };
        }
        else if (line.IsGroup)
        {
            job = Job.CreateForGroup(
                ids.NewId(), workOrder.SiteId, workOrder.Id, command.JobNumber.Trim(), line.Id,
                workOrder.MaterialId, command.PlannedQty!.Value, workOrder.UomId, rank, command.EarliestStart, command.LatestEnd);
        }
        else
        {
            job = Job.CreateProduction(
                ids.NewId(), workOrder.SiteId, workOrder.Id, command.JobNumber.Trim(), line.Id,
                workOrder.MaterialId, command.PlannedQty!.Value, workOrder.UomId, rank, command.EarliestStart);
            job = job with { LatestEnd = command.LatestEnd };
        }

        // A job added to an order takes its operation and components as if the order had made it (D-025).
        var components = new JobComponents(processes, boms, units, ids);
        if (workOrder is not null)
        {
            job = await components.WithOperationAsync(job, workOrder, cancellationToken);
        }

        var envelope = events.Create(EventTypes.JobCreated, "job", job.Id, new { After = job });
        var version = await scheduling.InsertJobAsync(job, envelope.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(job.Id, null, job.Status, envelope.OccurredAt, cancellationToken);
        if (workOrder is not null)
        {
            await components.PlanAsync(job, workOrder, cancellationToken);
        }

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
        var job = current.Value;
        var target = SchedulingRules.RequireLine(lines, command.LineId);
        if (target.SiteId != job.SiteId)
        {
            throw new DomainRuleException($"{target.Code} is in another site; jobs cannot move between sites.");
        }

        job.EnsureMovable(target.Id);
        if (target.IsGroup)
        {
            throw new DomainRuleException($"{target.Code} is a group; plan a new job to it instead of moving one there.");
        }
        var at = timeProvider.GetUtcNow();
        if (job.MaterialId is { } material)
        {
            await SchedulingRules.EnsureEligibleAsync(scheduling, target, material, at, cancellationToken);
        }

        var before = job.IsFirmed
            ? await BeforeFirmedStartAsync(target.Id, job.PlannedStart!.Value, at, cancellationToken)
            : command.BeforeJobId;
        var rank = await SchedulingRules.RankAsync(scheduling, target.Id, before, job.Id, at, cancellationToken);
        var moved = job.MoveTo(target.Id, rank);

        var envelope = events.Create(
            EventTypes.JobSequenced,
            "job",
            moved.Id,
            new
            {
                Before = new { job.EquipmentId, job.SequenceRank },
                After = new { moved.EquipmentId, moved.SequenceRank, BeforeJobId = before },
            });
        var version = await scheduling.UpdateJobAsync(moved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }

    /// <summary>
    /// A firmed job keeps its time on the new line, so it goes before the first job projected to
    /// start after its firmed start (D-019); null puts it at the end.
    /// </summary>
    private async Task<Guid?> BeforeFirmedStartAsync(Guid lineId, DateTimeOffset firmedStart, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var source = await scheduling.GetLinePlanAsync(
            lineId, now, now.AddDays(GetLineTimelineHandler.DefaultHorizonDays), cancellationToken);
        var timeline = TimelineProjector.Project(TimelineMapping.ToPlan(source!, now));
        return timeline.Segments
            .FirstOrDefault(s => s.Kind == SegmentKind.Job && s.Start > firmedStart)?.JobId;
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

        var pinned = current.Value.Pin(command.IsLocked, command.EarliestStart, command.LatestEnd);

        var envelope = events.Create(
            EventTypes.JobPinned,
            "job",
            pinned.Id,
            new
            {
                Before = new { current.Value.IsLocked, current.Value.EarliestStart, current.Value.LatestEnd },
                After = new { pinned.IsLocked, pinned.EarliestStart, pinned.LatestEnd },
            });
        var version = await scheduling.UpdateJobAsync(pinned, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class AssignJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<AssignJobCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(AssignJobCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.TargetId != Guid.Empty, nameof(command.TargetId), "Required.")
            .Require(command.BeforeJobId != command.Id, nameof(command.BeforeJobId), "A job cannot be placed before itself.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (current, lines) = await SchedulingRules.LockJobAsync(
            scheduling, command.Id, command.ExpectedVersion, command.TargetId, cancellationToken);
        var job = current.Value;
        var target = lines.FirstOrDefault(l => l.Id == command.TargetId)
            ?? throw new DomainRuleException($"Equipment {command.TargetId} not found.");
        var targetIsGroup = target.Id == job.GroupEquipmentId;
        var at = timeProvider.GetUtcNow();

        if (!targetIsGroup && job.GroupEquipmentId is { } group)
        {
            if (!target.CanRunJob)
            {
                throw new DomainRuleException($"{target.Code} can't run jobs; pick a line.");
            }

            if (!await scheduling.IsUnderAsync(group, target.Id, cancellationToken))
            {
                throw new DomainRuleException($"{target.Code} is not in the group job {job.JobNumber} was planned to.");
            }

            if (job.MaterialId is { } material)
            {
                await SchedulingRules.EnsureEligibleAsync(scheduling, target, material, at, cancellationToken);
            }
        }

        var rank = await SchedulingRules.RankAsync(
            scheduling, target.Id, targetIsGroup ? null : command.BeforeJobId, job.Id, at, cancellationToken);
        var assigned = job.Assign(target.Id, targetIsGroup, rank);

        var envelope = events.Create(
            EventTypes.JobAssigned,
            "job",
            assigned.Id,
            new
            {
                Before = new { job.EquipmentId, job.SequenceRank },
                After = new { assigned.EquipmentId, assigned.SequenceRank, command.BeforeJobId },
                assigned.GroupEquipmentId,
            });
        var version = await scheduling.UpdateJobAsync(assigned, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}

internal sealed class CancelJobHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<CancelJobCommand, CancelJobResponse>
{
    public async Task<CancelJobResponse> HandleAsync(CancelJobCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.Reason is null || command.Reason.Length <= 500, nameof(command.Reason), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (current, _) = await SchedulingRules.LockJobAsync(
            scheduling, command.Id, command.ExpectedVersion, null, cancellationToken);
        var job = current.Value;
        var at = timeProvider.GetUtcNow();
        var cancelled = job.Cancel();

        Job? replacement = null;
        WorkOrder? workOrder = null;
        if (command.Replace)
        {
            workOrder = job.WorkOrderId is { } workOrderId
                ? (await workOrders.GetForShareAsync(workOrderId, cancellationToken))!.Value
                : null;
            var workOrderStatus = workOrder?.Status ?? WorkOrderStatus.Planned;
            var remaining = (job.PlannedQty ?? 0m) - await scheduling.GetGoodQtyAsync(job.Id, cancellationToken);
            replacement = job.Replacement(ids.NewId(), remaining, workOrderStatus, at);
        }

        var envelope = events.Create(
            EventTypes.JobCancelled,
            "job",
            cancelled.Id,
            new { Before = new { job.Status }, After = new { cancelled.Status }, ReplacementJobId = replacement?.Id },
            command.Reason);
        await scheduling.EndOpenRunsAsync(job.Id, envelope.OccurredAt, cancellationToken);
        var version = await scheduling.UpdateJobAsync(cancelled, envelope.OccurredAt, cancellationToken);
        await scheduling.AppendStatusChangeAsync(job.Id, job.Status, cancelled.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        if (replacement is not null)
        {
            var created = events.Create(EventTypes.JobCreated, "job", replacement.Id, new { After = replacement });
            await scheduling.InsertJobAsync(replacement, created.OccurredAt, cancellationToken);
            await scheduling.AppendStatusChangeAsync(replacement.Id, null, replacement.Status, created.OccurredAt, cancellationToken);

            // The replacement keeps the cancelled job's operation and plans components for the quantity it takes over (D-025).
            if (workOrder is not null)
            {
                await new JobComponents(processes, boms, units, ids).PlanAsync(replacement, workOrder, cancellationToken);
            }

            await eventLog.AppendAsync(created, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new CancelJobResponse(version, replacement?.Id);
    }
}

/// <summary>Locking order, line checks and rank placement shared by the scheduling commands.</summary>
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

    /// <exception cref="DomainRuleException">The line has no current capability for the material (D-020).</exception>
    public static async Task EnsureEligibleAsync(
        ISchedulingRepository scheduling, SchedulingLine line, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!await scheduling.IsEligibleAsync(line.Id, materialId, at, cancellationToken))
        {
            throw new DomainRuleException($"Material {materialId} is not set up to run on {line.Code}; add a capability for it first.");
        }
    }

    /// <summary>
    /// Checks the material can run where the job is: on a line, that line's capability; on a
    /// group, a capability on any line under it that can run jobs (D-020, D-022).
    /// </summary>
    /// <exception cref="DomainRuleException">Nothing there can run the material.</exception>
    public static async Task EnsureCanRunAsync(
        ISchedulingRepository scheduling, SchedulingLine line, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (!line.IsGroup)
        {
            await EnsureEligibleAsync(scheduling, line, materialId, at, cancellationToken);
        }
        else if (!await scheduling.IsEligibleInGroupAsync(line.Id, materialId, at, cancellationToken))
        {
            throw new DomainRuleException($"No line in {line.Code} is set up to run material {materialId}; add a capability for it first.");
        }
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
