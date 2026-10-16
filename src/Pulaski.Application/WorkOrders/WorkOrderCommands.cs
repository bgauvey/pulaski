using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Application.Processes;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.WorkOrders;

/// <summary>
/// Makes a work order from a process (D-024): the process's released version decides the
/// operations and the lines or departments that run them, and each becomes a planned job at the
/// end of its line for its share of the quantity, held until <see cref="PlannedStart"/>. The
/// material's released BOM (D-025) plans each job's components.
/// </summary>
public sealed record CreateWorkOrderCommand(
    Guid ProcessId,
    string OrderNumber,
    Guid MaterialId,
    decimal PlannedQty,
    Guid UomId,
    DateTimeOffset PlannedStart,
    DateTimeOffset? DueAt,
    int Priority,
    string? Notes) : ICommand<CreatedResponse>;

public sealed record ChangeWorkOrderStatusCommand(
    Guid Id,
    long ExpectedVersion,
    string Status,
    string? Reason) : ICommand<VersionResponse>;

internal sealed class CreateWorkOrderHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IMaterialRepository materials,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<CreateWorkOrderCommand, CreatedResponse>
{
    public const int NotesMaxLength = 4000;

    public async Task<CreatedResponse> HandleAsync(CreateWorkOrderCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(command.ProcessId != Guid.Empty, nameof(command.ProcessId), "Required.")
            .RequireText(command.OrderNumber, nameof(command.OrderNumber), 50)
            .Require(command.MaterialId != Guid.Empty, nameof(command.MaterialId), "Required.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .Require(command.PlannedQty > 0, nameof(command.PlannedQty), "Must be greater than zero.")
            .Require(command.PlannedStart != default, nameof(command.PlannedStart), "Required.")
            .Require(command.DueAt is null || command.DueAt > command.PlannedStart, nameof(command.DueAt), "Must be after the planned start.")
            .Require(command.Notes is null || command.Notes.Length <= NotesMaxLength, nameof(command.Notes), $"At most {NotesMaxLength} characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // Read the route without a lock to learn which lines to lock first (D-017).
        var route = await processes.GetReleasedAsync(command.ProcessId, lockVersion: false, cancellationToken)
            ?? throw new DomainRuleException($"Process {command.ProcessId} not found, or it has no released version.");
        var process = route.Process;
        if (!process.IsActive)
        {
            throw new DomainRuleException($"Process {process.Code} is inactive.");
        }

        if (!await materials.IsActiveAsync(command.MaterialId, cancellationToken))
        {
            throw new DomainRuleException($"Material {command.MaterialId} not found or inactive.");
        }

        if (!await processes.CoversAsync(process.Id, command.MaterialId, cancellationToken))
        {
            throw new DomainRuleException($"Process {process.Code} doesn't make this material; pick a process whose scope covers it.");
        }

        var orderNumber = command.OrderNumber.Trim();
        var plan = ProcessJobPlan.For(route.Version.Definition, orderNumber, command.PlannedQty, command.PlannedStart);
        var lines = (await scheduling.LockLinesAsync([.. plan.Select(j => j.EquipmentId).Distinct()], cancellationToken))
            .ToDictionary(l => l.Id);

        // Under the lock, the route must still be the released one.
        var locked = await processes.GetReleasedAsync(process.Id, lockVersion: true, cancellationToken);
        if (locked?.Version.Id != route.Version.Id)
        {
            throw new ConflictException($"Process {process.Code} was released again while the order was being made. Try again.");
        }

        // The same rules the order form checks before submitting (ProcessOrderRules).
        var at = timeProvider.GetUtcNow();
        var equipmentRows = await processes.GetOrderEquipmentAsync(route.Version.Id, command.MaterialId, at, cancellationToken);
        var problems = ProcessOrderRules.Problems(equipmentRows, process.SiteId).Select(p => p.Message).ToList();
        problems.AddRange(DepartmentProblems(plan, lines, command.DueAt));
        if (problems.Count > 0)
        {
            throw new DomainRuleException(string.Join(" ", problems));
        }

        // Read FOR SHARE after the lines and the route, so the BOM can't be replaced while it is used.
        var bom = await boms.GetReleasedForOrderAsync(command.MaterialId, process.Id, cancellationToken);
        var components = bom is null ? [] : BomExplosion.For(bom.Definition, command.PlannedQty, command.UomId, plan);

        var workOrder = WorkOrder.CreateLocal(
            ids.NewId(),
            process.SiteId,
            orderNumber,
            command.MaterialId,
            command.PlannedQty,
            command.UomId,
            null,
            command.PlannedStart,
            null,
            command.DueAt,
            command.Priority,
            route.Version.Id,
            command.Notes,
            bom?.Id);

        var envelope = events.Create(EventTypes.WorkOrderCreated, "work_order", workOrder.Id, new { After = workOrder });
        var version = await workOrders.InsertAsync(workOrder, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        var jobIds = new List<Guid>(plan.Count);
        foreach (var planned in plan)
        {
            var line = lines[planned.EquipmentId];
            var rank = await SchedulingRules.RankAsync(scheduling, line.Id, null, null, at, cancellationToken);
            var job = line.IsGroup
                ? Job.CreateForGroup(
                    ids.NewId(), workOrder.SiteId, workOrder.Id, planned.JobNumber, line.Id, workOrder.MaterialId,
                    planned.PlannedQty, workOrder.UomId, rank, planned.EarliestStart, workOrder.DueAt)
                : Job.CreateProduction(
                    ids.NewId(), workOrder.SiteId, workOrder.Id, planned.JobNumber, line.Id, workOrder.MaterialId,
                    planned.PlannedQty, workOrder.UomId, rank, planned.EarliestStart);
            job = job with { ProcessOperationId = planned.Operation.Id };

            var jobEnvelope = events.Create(EventTypes.JobCreated, "job", job.Id, new { After = job });
            await scheduling.InsertJobAsync(job, jobEnvelope.OccurredAt, cancellationToken);
            await scheduling.AppendStatusChangeAsync(job.Id, null, job.Status, jobEnvelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(jobEnvelope, cancellationToken);
            jobIds.Add(job.Id);
        }

        await boms.InsertJobMaterialsAsync(
            [.. components.Select(c => new JobMaterial(
                ids.NewId(), jobIds[c.JobIndex], c.Item.ComponentMaterialId, JobMaterial.Consume, c.PlannedQty, c.Item.UomId,
                c.Item.ConsumptionMethod, c.Item.Id))],
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(workOrder.Id, version);
    }

    /// <summary>A job on a department waits in a window that ends at the order's due date (D-022).</summary>
    private static IEnumerable<string> DepartmentProblems(
        IReadOnlyList<OperationJob> plan, Dictionary<Guid, SchedulingLine> lines, DateTimeOffset? dueAt)
    {
        foreach (var job in plan.Where(j => lines.GetValueOrDefault(j.EquipmentId)?.IsGroup == true))
        {
            var op = $"Operation {job.Operation.Number} ({job.Operation.Name}):";
            if (dueAt is not { } due)
            {
                yield return $"{op} {lines[job.EquipmentId].Code} is a department, so its job needs a finish-by; give the order a due date.";
            }
            else if (due <= job.EarliestStart)
            {
                yield return $"{op} the due date must be after the operation's earliest start, {job.EarliestStart:u}.";
            }
        }
    }
}

internal sealed class ChangeWorkOrderStatusHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<ChangeWorkOrderStatusCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ChangeWorkOrderStatusCommand command, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(WorkOrderStatus.All.Contains(command.Status), nameof(command.Status), $"Unknown status '{command.Status}'.")
            .Require(command.Reason is null || command.Reason.Length <= 500, nameof(command.Reason), "At most 500 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        var current = await workOrders.GetForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Work order {command.Id} not found.");

        // Check the version before the rule, so a stale screen reports a conflict, not a bad transition.
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"Work order {current.Value.OrderNumber} was changed by someone else. Reload and try again.");
        }

        if (WorkOrderStatus.IsPlanningTransition(current.Value.Status, command.Status))
        {
            throw new DomainRuleException(
                $"Moving a work order from '{current.Value.Status}' to '{command.Status}' also changes its jobs; use the dispatch, un-dispatch or firm command.");
        }

        var changed = current.Value.WithStatus(command.Status);
        if (changed.Status == WorkOrderStatus.Cancelled && await workOrders.HasOpenJobsAsync(changed.Id, cancellationToken))
        {
            throw new DomainRuleException($"Work order {changed.OrderNumber} still has open jobs; cancel them first.");
        }

        var envelope = events.Create(
            EventTypes.WorkOrderStatusChanged,
            "work_order",
            changed.Id,
            new { Before = new { current.Value.Status }, After = new { changed.Status } },
            command.Reason);
        var version = await workOrders.UpdateStatusAsync(changed.Id, changed.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}
