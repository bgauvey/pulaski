using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Processes;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Processes;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Scheduling;

/// <summary>
/// Dispatches (publishes) a planned work order: each of its jobs gets its projected times as the
/// baseline and goes to the floor (D-018).
/// </summary>
public sealed record DispatchWorkOrderCommand(Guid Id, long ExpectedVersion, string? Reason = null) : ICommand<DispatchWorkOrderResponse>;

/// <summary>Takes a dispatched work order back to planned, with its jobs, while none has started (D-018).</summary>
public sealed record UndispatchWorkOrderCommand(Guid Id, long ExpectedVersion, string? Reason = null) : ICommand<VersionResponse>;

/// <summary>Firms a dispatched work order: its jobs are frozen at their baseline start (D-018, D-019).</summary>
public sealed record FirmWorkOrderCommand(Guid Id, long ExpectedVersion, string? Reason = null) : ICommand<VersionResponse>;

internal sealed class DispatchWorkOrderHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<DispatchWorkOrderCommand, DispatchWorkOrderResponse>
{
    public async Task<DispatchWorkOrderResponse> HandleAsync(DispatchWorkOrderCommand command, CancellationToken cancellationToken)
    {
        WorkOrderPlanning.ValidateReason(command.Reason);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (workOrder, lines, jobs) = await WorkOrderPlanning.LockAsync(
            scheduling, workOrders, command.Id, command.ExpectedVersion, cancellationToken);
        if (workOrder.QuantityHeld)
        {
            throw new DomainRuleException(
                $"Work order {workOrder.OrderNumber} is held: the ERP's quantity isn't whole batches. Set a whole-batch quantity on the order first.");
        }

        var dispatched = workOrder.WithStatus(WorkOrderStatus.Dispatched);
        if (jobs.Count == 0)
        {
            throw new DomainRuleException($"Work order {workOrder.OrderNumber} has no jobs; plan at least one before dispatching.");
        }

        var now = timeProvider.GetUtcNow();
        foreach (var job in jobs.Where(j => j.MaterialId is not null))
        {
            await SchedulingRules.EnsureCanRunAsync(
                scheduling, lines.Single(l => l.Id == job.EquipmentId), job.MaterialId!.Value, now, cancellationToken);
        }

        // Each line is projected once, inside the transaction, after its lock. A group isn't
        // projected: its jobs publish their window as the baseline (D-022).
        var segments = new Dictionary<Guid, TimelineSegment>();
        foreach (var line in lines.Where(l => !l.IsGroup))
        {
            var source = await scheduling.GetLinePlanAsync(
                line.Id, now, now.AddDays(GetLineTimelineHandler.DefaultHorizonDays), cancellationToken);
            var timeline = TimelineMapping.Project(source!, now);
            foreach (var segment in timeline.Segments.Where(s => s.Kind == SegmentKind.Job))
            {
                segments[segment.JobId] = segment;
            }
        }

        var published = jobs
            .Select(j => j.IsOnGroup
                ? j.Dispatch(now, j.EarliestStart ?? now, j.LatestEnd ?? now)
                : j.Dispatch(now, segments[j.Id].Start, segments[j.Id].End))
            .ToList();
        var definition = workOrder.ProcessVersionId is { } versionId
            ? (await processes.GetVersionAsync(versionId, cancellationToken))?.Definition
            : null;
        var warnings = QuantityWarnings(workOrder, definition, published);

        var envelope = events.Create(
            EventTypes.WorkOrderDispatched,
            "work_order",
            workOrder.Id,
            new
            {
                Before = new { workOrder.Status },
                After = new { dispatched.Status },
                Jobs = published.Select(j => new { j.Id, j.EquipmentId, j.PlannedStart, j.PlannedEnd }),
            },
            command.Reason);
        var version = await workOrders.UpdateStatusAsync(workOrder.Id, dispatched.Status, envelope.OccurredAt, cancellationToken);

        var baselines = new List<BaselineJobDto>(published.Count);
        foreach (var job in published)
        {
            var jobVersion = await scheduling.UpdateJobAsync(job, envelope.OccurredAt, cancellationToken);
            await scheduling.AppendStatusChangeAsync(job.Id, JobStatus.Planned, job.Status, envelope.OccurredAt, cancellationToken);
            baselines.Add(new BaselineJobDto(
                job.Id,
                job.PlannedStart!.Value,
                job.PlannedEnd,
                jobVersion,
                segments.TryGetValue(job.Id, out var segment)
                    ? [.. segment.Warnings.Select(w => new ScheduleWarningDto(w.Code, w.Message))]
                    : []));
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new DispatchWorkOrderResponse(version, baselines, warnings);
    }

    /// <summary>
    /// Warns, without refusing, where the jobs don't add up to the order (D-018). Each operation of
    /// the order's process makes the whole quantity, so each is checked on its own (D-024).
    /// </summary>
    private static IReadOnlyList<ScheduleWarningDto> QuantityWarnings(WorkOrder order, ProcessDefinition? definition, IReadOnlyList<Job> jobs)
    {
        if (definition is null || definition.Operations.Count == 0)
        {
            var total = jobs.Sum(j => j.PlannedQty ?? 0m);
            return total == order.PlannedQty
                ? []
                : [new ScheduleWarningDto("quantity_mismatch", $"The jobs plan {total:0.######} against an order quantity of {order.PlannedQty:0.######}.")];
        }

        return [.. OperationQuantities.ByOperation(definition, jobs.Select(j => (j.ProcessOperationId, j.PlannedQty ?? 0m)))
            .Where(o => o.Quantity != order.PlannedQty)
            .Select(o => new ScheduleWarningDto(
                "quantity_mismatch",
                $"Operation {o.Operation.Number} ({o.Operation.Name}) plans {o.Quantity:0.######} against an order quantity of {order.PlannedQty:0.######}."))];
    }
}

internal sealed class UndispatchWorkOrderHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UndispatchWorkOrderCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UndispatchWorkOrderCommand command, CancellationToken cancellationToken)
    {
        WorkOrderPlanning.ValidateReason(command.Reason);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (workOrder, _, jobs) = await WorkOrderPlanning.LockAsync(
            scheduling, workOrders, command.Id, command.ExpectedVersion, cancellationToken);
        if (workOrder.Status == WorkOrderStatus.Firmed)
        {
            throw new DomainRuleException($"Work order {workOrder.OrderNumber} is firmed; it can no longer be un-dispatched.");
        }

        var planned = workOrder.WithStatus(WorkOrderStatus.Planned);
        if (jobs.FirstOrDefault(j => JobStatus.Started.Contains(j.Status)) is { } started)
        {
            throw new DomainRuleException($"Job {started.JobNumber} has started; cancel it instead of un-dispatching its order.");
        }

        var result = await WorkOrderPlanning.SaveAsync(
            scheduling, workOrders, eventLog, events, EventTypes.WorkOrderUndispatched, command.Reason,
            workOrder, planned, jobs, jobs.Select(j => j.Undispatch()).ToList(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

internal sealed class FirmWorkOrderHandler(
    IUnitOfWork unitOfWork,
    ISchedulingRepository scheduling,
    IWorkOrderRepository workOrders,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider) : ICommandHandler<FirmWorkOrderCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(FirmWorkOrderCommand command, CancellationToken cancellationToken)
    {
        WorkOrderPlanning.ValidateReason(command.Reason);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var (workOrder, _, jobs) = await WorkOrderPlanning.LockAsync(
            scheduling, workOrders, command.Id, command.ExpectedVersion, cancellationToken);
        var firmed = workOrder.WithStatus(WorkOrderStatus.Firmed);
        var at = timeProvider.GetUtcNow();

        var result = await WorkOrderPlanning.SaveAsync(
            scheduling, workOrders, eventLog, events, EventTypes.WorkOrderFirmed, command.Reason,
            workOrder, firmed, jobs, jobs.Select(j => j.Firm(at)).ToList(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

internal static class WorkOrderPlanning
{
    public static void ValidateReason(string? reason) =>
        new Validator()
            .Require(reason is null || reason.Length <= 500, "Reason", "At most 500 characters.")
            .ThrowIfInvalid();

    /// <summary>
    /// Locks the lines of the work order's open jobs, then the work order, then the jobs (D-017),
    /// and checks the version. A job moved to another line meanwhile is a conflict.
    /// </summary>
    public static async Task<(WorkOrder WorkOrder, IReadOnlyList<SchedulingLine> Lines, IReadOnlyList<Job> Jobs)> LockAsync(
        ISchedulingRepository scheduling,
        IWorkOrderRepository workOrders,
        Guid workOrderId,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        var lineIds = await scheduling.GetWorkOrderLinesAsync(workOrderId, cancellationToken);
        var lines = lineIds.Count == 0 ? [] : await scheduling.LockLinesAsync(lineIds, cancellationToken);

        var workOrder = await workOrders.GetForUpdateAsync(workOrderId, cancellationToken)
            ?? throw new NotFoundException($"Work order {workOrderId} not found.");
        if (workOrder.Version != expectedVersion)
        {
            throw Changed(workOrder.Value);
        }

        var jobs = (await scheduling.GetWorkOrderJobsForUpdateAsync(workOrderId, cancellationToken)).Select(j => j.Value).ToList();
        if (jobs.Any(j => !lineIds.Contains(j.EquipmentId)))
        {
            throw Changed(workOrder.Value);
        }

        return (workOrder.Value, lines, jobs);
    }

    /// <summary>Writes the order's new status and its jobs, with one event on the order and a history row per job.</summary>
    public static async Task<VersionResponse> SaveAsync(
        ISchedulingRepository scheduling,
        IWorkOrderRepository workOrders,
        IEventLog eventLog,
        IEventFactory events,
        string eventType,
        string? reason,
        WorkOrder before,
        WorkOrder after,
        IReadOnlyList<Job> jobsBefore,
        IReadOnlyList<Job> jobsAfter,
        CancellationToken cancellationToken)
    {
        var envelope = events.Create(
            eventType,
            "work_order",
            after.Id,
            new { Before = new { before.Status }, After = new { after.Status }, Jobs = jobsAfter.Select(j => j.Id) },
            reason);
        var version = await workOrders.UpdateStatusAsync(after.Id, after.Status, envelope.OccurredAt, cancellationToken);

        for (var i = 0; i < jobsAfter.Count; i++)
        {
            await scheduling.UpdateJobAsync(jobsAfter[i], envelope.OccurredAt, cancellationToken);
            await scheduling.AppendStatusChangeAsync(
                jobsAfter[i].Id, jobsBefore[i].Status, jobsAfter[i].Status, envelope.OccurredAt, cancellationToken);
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        return new VersionResponse(version);
    }

    private static ConflictException Changed(WorkOrder workOrder) =>
        new($"Work order {workOrder.OrderNumber} was changed by someone else. Reload and try again.");
}
