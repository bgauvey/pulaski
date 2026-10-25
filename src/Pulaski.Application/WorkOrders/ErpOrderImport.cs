using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Application.Processes;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.Scheduling;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.WorkOrders;

/// <summary>
/// Work orders from the ERP (D-066), each in a transaction of its own, matched on external system and key so sending one again never
/// duplicates it. A new one is made like a local order, from its process, with jobs and components; a quantity that isn't whole batches
/// follows the material's (else the site's) ERP batch rule. A change applies while the order is planned and waits for a planner after.
/// </summary>
/// <param name="Orders">The orders, as sent.</param>
/// <param name="Sites">Sites where the caller may manage work orders; null for every site.</param>
public sealed record ImportWorkOrdersCommand(IReadOnlyList<ErpWorkOrderRequest> Orders, IReadOnlySet<Guid>? Sites) : ICommand<ImportWorkOrdersResponse>;

/// <summary>Applies the ERP's pending change to a dispatched order (D-066).</summary>
public sealed record AcceptErpChangeCommand(Guid WorkOrderId, long ExpectedVersion) : ICommand<VersionResponse>;

/// <summary>Drops the ERP's pending change, with a reason; the order stays as it is (D-066).</summary>
public sealed record RejectErpChangeCommand(Guid WorkOrderId, long ExpectedVersion, string Reason) : ICommand<VersionResponse>;

/// <summary>Sets a planned order's quantity to whole batches, releasing an order held because its ERP quantity wasn't (D-066).</summary>
public sealed record SetWorkOrderQuantityCommand(Guid WorkOrderId, long ExpectedVersion, decimal Quantity) : ICommand<VersionResponse>;

/// <summary>Sets a site's rule for ERP quantities that aren't whole batches (D-066).</summary>
public sealed record SetErpBatchRuleCommand(Guid SiteId, string Rule) : ICommand<VersionResponse>;

/// <summary>Sets a material's own rule for ERP quantities that aren't whole batches; null uses the site's (D-066).</summary>
public sealed record SetMaterialErpBatchRuleCommand(Guid MaterialId, long ExpectedVersion, string? Rule) : ICommand<VersionResponse>;

/// <summary>Results of one import row.</summary>
public static class ErpOrderAction
{
    public const string Created = "created";
    public const string Updated = "updated";
    public const string Unchanged = "unchanged";
    public const string Pending = "pending";
    public const string Cancelled = "cancelled";
    public const string Refused = "refused";
}

/// <summary>The planning steps the ERP import and the planner's decisions share (D-066).</summary>
internal sealed class ErpOrderWriter(
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IUnitRepository units,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    ILineBatchSizes? lineBatches)
{
    private static readonly HashSet<string> NotStarted = [JobStatus.Planned, JobStatus.Dispatched, JobStatus.Firmed];

    /// <summary>Locks the lines of the order's open jobs, then the order, then its jobs (D-017).</summary>
    public async Task<(Versioned<WorkOrder> Order, IReadOnlyList<SchedulingLine> Lines, IReadOnlyList<Job> Jobs)> LockAsync(Guid id, CancellationToken cancellationToken)
    {
        var lineIds = await scheduling.GetWorkOrderLinesAsync(id, cancellationToken);
        var lines = lineIds.Count == 0 ? [] : await scheduling.LockLinesAsync(lineIds, cancellationToken);
        var order = await workOrders.GetForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Work order {id} not found.");
        var jobs = (await scheduling.GetWorkOrderJobsForUpdateAsync(id, cancellationToken)).Select(j => j.Value).ToList();
        if (jobs.Any(j => !lineIds.Contains(j.EquipmentId)))
        {
            throw new ConflictException($"Work order {order.Value.OrderNumber}'s jobs moved while it was being changed. Try again.");
        }

        return (order, lines, jobs);
    }

    /// <summary>The quantity to plan for the ERP's, in the order's unit, under the ERP batch rule in force.</summary>
    public async Task<(decimal Planned, ErpQuantity Taken)> QuantityAsync(
        Guid siteId, Guid materialId, decimal quantity, Guid uomId, string what, CancellationToken cancellationToken)
    {
        var material = (await boms.GetMaterialsAsync([materialId], cancellationToken)).Single();
        var converter = UnitConversions.For(materialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([materialId], cancellationToken));
        if (!converter.CanConvert(uomId, material.BaseUomId))
        {
            throw new DomainRuleException($"{what}: {converter.Code(uomId)} doesn't convert to {converter.Code(material.BaseUomId)}, the base unit of {material.Code}.");
        }

        var rule = await workOrders.GetErpBatchRuleAsync(siteId, materialId, cancellationToken);
        var taken = ErpBatchRule.Apply(converter.Convert(quantity, uomId, material.BaseUomId), material.Batching, rule, what, converter.Code(material.BaseUomId));
        return (taken.Adjustment is null ? quantity : converter.Convert(taken.Quantity, material.BaseUomId, uomId), taken);
    }

    /// <summary>
    /// Re-plans the order's jobs in place for a new quantity and start (D-066): each operation's jobs are shared as when it was made, in
    /// whole batches of each job's line size unless held; components scale with each job; a new start moves every job's hold-until by as
    /// much, and a new due date ends department windows. Only while no job has started, and only when the jobs still match the process.
    /// </summary>
    public async Task<IReadOnlyList<string>> ReplanAsync(
        WorkOrder order, IReadOnlyList<Job> jobs, decimal quantity, bool held, TimeSpan startShift, DateTimeOffset? dueAt, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (jobs.Any(j => !NotStarted.Contains(j.Status)))
        {
            throw new DomainRuleException($"A job of {order.OrderNumber} has started; change its jobs on the schedule instead.");
        }

        var definition = (order.ProcessVersionId is { } versionId ? await processes.GetVersionAsync(versionId, cancellationToken) : null)?.Definition
            ?? throw new DomainRuleException($"{order.OrderNumber} wasn't made from a process, so its jobs can't be re-planned; change them on the schedule.");
        var material = (await boms.GetMaterialsAsync([order.MaterialId], cancellationToken)).Single();
        var converter = UnitConversions.For(order.MaterialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([order.MaterialId], cancellationToken));
        var batching = held ? null : material.Batching;
        var sizes = batching is null || lineBatches is null
            ? new Dictionary<Guid, decimal>()
            : await lineBatches.GetAsync(order.MaterialId, [.. jobs.Select(j => j.EquipmentId).Distinct()], now, cancellationToken);
        var notes = new List<string>();
        foreach (var op in definition.InFlowOrder())
        {
            var opJobs = jobs.Where(j => j.ProcessOperationId == op.Id).OrderBy(j => j.JobNumber, StringComparer.Ordinal).ToList();
            if (opJobs.Count != op.Equipment.Count)
            {
                throw new DomainRuleException(
                    $"Operation {op.Number} of {order.OrderNumber} has {opJobs.Count} open jobs where its process has {op.Equipment.Count}; they were changed by hand, so change them on the schedule.");
            }

            var shares = new decimal[opJobs.Count];
            if (batching is not null)
            {
                decimal Size(Job job) => converter.Convert(sizes.TryGetValue(job.EquipmentId, out var own) ? own : batching.Size, material.BaseUomId, order.UomId);
                var counts = Batching.ShareBySize(
                    quantity, [.. opJobs.Select((j, i) => (op.Equipment[i].SharePercent, Size(j)))], $"Work order {order.OrderNumber}, operation {op.Number}", converter.Code(order.UomId));
                for (var i = 0; i < opJobs.Count; i++)
                {
                    shares[i] = Math.Round(counts[i] * Size(opJobs[i]), Domain.Processes.ProcessJobPlan.QuantityScale);
                }
            }
            else
            {
                var left = quantity;
                for (var i = 0; i < opJobs.Count; i++)
                {
                    shares[i] = i == opJobs.Count - 1 ? left : Math.Round(quantity * op.Equipment[i].SharePercent / 100m, Domain.Processes.ProcessJobPlan.QuantityScale, MidpointRounding.ToZero);
                    left -= shares[i];
                }
            }

            for (var i = 0; i < opJobs.Count; i++)
            {
                var job = opJobs[i];
                var after = job with
                {
                    PlannedQty = shares[i],
                    EarliestStart = job.EarliestStart is { } earliest ? earliest + startShift : null,
                    LatestEnd = job.GroupEquipmentId is not null && job.EquipmentId == job.GroupEquipmentId && dueAt is not null ? dueAt : job.LatestEnd,
                };
                if (after == job)
                {
                    continue;
                }

                var envelope = events.Create(EventTypes.JobReplanned, "job", job.Id,
                    new { Before = new { job.PlannedQty, job.EarliestStart, job.LatestEnd }, After = new { after.PlannedQty, after.EarliestStart, after.LatestEnd } });
                await scheduling.UpdateJobAsync(after, envelope.OccurredAt, cancellationToken);
                if (job.PlannedQty is { } before && before > 0 && after.PlannedQty != before)
                {
                    await boms.ScaleJobMaterialsAsync(job.Id, after.PlannedQty!.Value / before, cancellationToken);
                }

                await eventLog.AppendAsync(envelope, cancellationToken);
                notes.Add($"{job.JobNumber}: {Batching.Qty(job.PlannedQty ?? 0m)} → {Batching.Qty(after.PlannedQty!.Value)}.");
            }
        }

        return notes;
    }

    /// <summary>Cancels an order that hasn't started, with its jobs.</summary>
    public async Task<long> CancelAsync(WorkOrder order, IReadOnlyList<Job> jobs, string reason, CancellationToken cancellationToken)
    {
        if (jobs.Any(j => !NotStarted.Contains(j.Status)))
        {
            throw new DomainRuleException($"A job of {order.OrderNumber} has started, so it can't be cancelled as a whole.");
        }

        foreach (var job in jobs)
        {
            var cancelled = job.Cancel();
            var jobEnvelope = events.Create(EventTypes.JobCancelled, "job", job.Id, new { Before = new { job.Status }, After = new { cancelled.Status } }, reason);
            await scheduling.UpdateJobAsync(cancelled, jobEnvelope.OccurredAt, cancellationToken);
            await scheduling.AppendStatusChangeAsync(job.Id, job.Status, cancelled.Status, jobEnvelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(jobEnvelope, cancellationToken);
        }

        var after = order.WithStatus(WorkOrderStatus.Cancelled);
        var envelope = events.Create(EventTypes.WorkOrderStatusChanged, "work_order", order.Id, new { Before = new { order.Status }, After = new { after.Status } }, reason);
        var version = await workOrders.UpdateStatusAsync(order.Id, after.Status, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return version;
    }

    /// <summary>Applies an ERP change to the order now (D-066): its fields, its jobs re-planned, and how the quantity was taken.</summary>
    public async Task<(long Version, IReadOnlyList<string> Notes)> ApplyAsync(
        WorkOrder order, IReadOnlyList<Job> jobs, ErpOrderChange change, string eventType, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (change.Cancelled)
        {
            return (await CancelAsync(order, jobs, reason ?? "Cancelled in the ERP", cancellationToken), ["Cancelled, with its jobs."]);
        }

        if (change.UomId != order.UomId)
        {
            throw new DomainRuleException($"{order.OrderNumber} is in {await workOrders.GetUomCodeAsync(order.UomId, cancellationToken)}; the ERP must keep sending that unit.");
        }

        var (planned, taken) = await QuantityAsync(order.SiteId, order.MaterialId, change.Quantity, change.UomId, $"Work order {order.OrderNumber}", cancellationToken);
        var held = taken.Adjustment == BatchAdjustment.Held;
        var startShift = order.PlannedStart is { } start ? change.PlannedStart - start : TimeSpan.Zero;
        var notes = new List<string>();
        if (planned != order.PlannedQty || startShift != TimeSpan.Zero || change.DueAt != order.DueAt || held != order.QuantityHeld)
        {
            notes.AddRange(await ReplanAsync(order, jobs, planned, held, startShift, change.DueAt, now, cancellationToken));
        }

        if (taken.Note is { } note)
        {
            notes.Add(note);
        }

        var after = order with
        {
            PlannedQty = planned,
            PlannedStart = change.PlannedStart,
            DueAt = change.DueAt,
            Priority = change.Priority,
            ExternalVersion = change.Version ?? order.ExternalVersion,
            ErpQuantity = taken.Adjustment is null ? null : change.Quantity,
            ErpUomId = taken.Adjustment is null ? null : change.UomId,
            BatchAdjustment = taken.Adjustment,
            QuantityHeld = held,
            PendingChange = null,
            ImportedAt = change.ReceivedAt,
        };
        var envelope = events.Create(eventType, "work_order", order.Id, new { Before = Fields(order), After = Fields(after), Erp = change }, reason);
        var version = await workOrders.UpdateFromErpAsync(after, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return (version, notes);
    }

    public static object Fields(WorkOrder o) =>
        new { o.PlannedQty, o.UomId, o.PlannedStart, o.DueAt, o.Priority, o.ExternalVersion, o.ErpQuantity, o.BatchAdjustment, o.QuantityHeld };
}

internal sealed class ImportWorkOrdersHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IUnitRepository units,
    IMaterialRepository materials,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILineBatchSizes? lineBatches = null) : ICommandHandler<ImportWorkOrdersCommand, ImportWorkOrdersResponse>
{
    public const int MaxOrders = 500;

    public async Task<ImportWorkOrdersResponse> HandleAsync(ImportWorkOrdersCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.Orders.Count is > 0 and <= MaxOrders, "Orders", $"Between 1 and {MaxOrders} orders.").ThrowIfInvalid();
        var results = new List<ErpOrderResultDto>();
        foreach (var order in command.Orders)
        {
            // Each order on its own: one refused doesn't stop the rest.
            try
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
                var result = await ImportAsync(order, command.Sites, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                results.Add(result);
            }
            catch (Exception ex) when (ex is DomainRuleException or ValidationException or ConflictException or NotFoundException or ForbiddenException)
            {
                results.Add(new ErpOrderResultDto { ExternalKey = order.ExternalKey, OrderNumber = order.OrderNumber, Action = ErpOrderAction.Refused, Messages = [Message(ex)] });
            }
        }

        return new ImportWorkOrdersResponse { Results = results };
    }

    private async Task<ErpOrderResultDto> ImportAsync(ErpWorkOrderRequest row, IReadOnlySet<Guid>? sites, CancellationToken cancellationToken)
    {
        var system = row.ExternalSystem.Trim();
        var key = row.ExternalKey.Trim();
        var number = row.OrderNumber.Trim();
        var version = string.IsNullOrWhiteSpace(row.ExternalVersion) ? null : row.ExternalVersion.Trim();
        new Validator()
            .RequireText(system, "ExternalSystem", 100)
            .RequireText(key, "ExternalKey", 200)
            .RequireText(number, "OrderNumber", 50)
            .RequireText(row.SiteCode, "SiteCode", 50)
            .RequireText(row.MaterialCode, "MaterialCode", 50)
            .RequireText(row.UomCode, "UomCode", 20)
            .Require(version is null || version.Length <= 100, "ExternalVersion", "At most 100 characters.")
            .Require(row.Quantity > 0, "Quantity", "Must be greater than zero.")
            .Require(row.PlannedStart != default, "PlannedStart", "Required.")
            .Require(row.DueAt is null || row.DueAt > row.PlannedStart, "DueAt", "Must be after the planned start.")
            .Require(row.Notes is null || row.Notes.Length <= CreateWorkOrderHandler.NotesMaxLength, "Notes", $"At most {CreateWorkOrderHandler.NotesMaxLength} characters.")
            .ThrowIfInvalid();

        var siteId = await workOrders.FindSiteAsync(row.SiteCode.Trim(), cancellationToken) ?? throw new DomainRuleException($"There is no site {row.SiteCode.Trim()}.");
        if (sites is not null && !sites.Contains(siteId))
        {
            throw new ForbiddenException($"You can't manage work orders at {row.SiteCode.Trim()}.");
        }

        var materialId = await workOrders.FindMaterialAsync(row.MaterialCode.Trim(), cancellationToken)
            ?? throw new DomainRuleException($"There is no active material {row.MaterialCode.Trim()}.");
        var uomId = await workOrders.FindUomAsync(row.UomCode.Trim(), cancellationToken) ?? throw new DomainRuleException($"There is no unit '{row.UomCode.Trim()}'.");
        var now = timeProvider.GetUtcNow();
        var change = new ErpOrderChange(row.Quantity, uomId, row.PlannedStart, row.DueAt, row.Priority ?? 0, version, row.Cancelled, now);
        var writer = new ErpOrderWriter(workOrders, processes, boms, units, scheduling, eventLog, events, lineBatches);

        var existingId = await workOrders.FindByExternalKeyAsync(system, key, cancellationToken);
        return existingId is null
            ? await CreateAsync(row, system, key, number, siteId, materialId, change, writer, cancellationToken)
            : await UpdateAsync(existingId.Value, row, materialId, change, writer, now, cancellationToken);
    }

    private async Task<ErpOrderResultDto> CreateAsync(
        ErpWorkOrderRequest row, string system, string key, string number, Guid siteId, Guid materialId, ErpOrderChange change, ErpOrderWriter writer,
        CancellationToken cancellationToken)
    {
        if (change.Cancelled)
        {
            return new ErpOrderResultDto { ExternalKey = key, OrderNumber = number, Action = ErpOrderAction.Unchanged, Messages = ["Cancelled in the ERP before Pulaski had it; nothing to do."] };
        }

        if (await workOrders.NumberExistsAsync(siteId, number, cancellationToken))
        {
            throw new DomainRuleException($"{row.SiteCode.Trim()} already has a work order {number} that the ERP didn't send; give the order another number.");
        }

        var candidates = new List<(Guid Id, string Code)>();
        foreach (var process in await workOrders.FindProcessesAsync(siteId, materialId, InventoryText(row.ProcessCode), cancellationToken))
        {
            if (await processes.CoversAsync(process.Id, materialId, cancellationToken))
            {
                candidates.Add(process);
            }
        }

        var chosen = candidates switch
        {
            [var only] => only,
            [] => throw new DomainRuleException(InventoryText(row.ProcessCode) is { } code
                ? $"There is no released process {code} at {row.SiteCode.Trim()} that makes {row.MaterialCode.Trim()}."
                : $"No released process at {row.SiteCode.Trim()} makes {row.MaterialCode.Trim()}; release one, or send the process code."),
            _ => throw new DomainRuleException(
                $"Several processes make {row.MaterialCode.Trim()} ({string.Join(", ", candidates.Select(c => c.Code))}); send the process code, or set the material's default process."),
        };

        var (planned, taken) = await writer.QuantityAsync(siteId, materialId, change.Quantity, change.UomId, $"Work order {number}", cancellationToken);
        var maker = new CreateWorkOrderHandler(unitOfWork, workOrders, processes, boms, units, materials, scheduling, eventLog, events, ids, timeProvider, lineBatches);
        var (order, _) = await maker.MakeAsync(
            new CreateWorkOrderCommand(chosen.Id, number, materialId, planned, change.UomId, change.PlannedStart, change.DueAt, change.Priority, InventoryText(row.Notes)),
            new ErpOrderOrigin(system, key, change.Version, change.Quantity, change.UomId, taken.Adjustment, taken.Adjustment == BatchAdjustment.Held, change.ReceivedAt),
            cancellationToken);
        return new ErpOrderResultDto
        {
            ExternalKey = key,
            OrderNumber = number,
            Action = ErpOrderAction.Created,
            WorkOrderId = order.Id,
            Messages = [$"Made from process {chosen.Code}.", .. taken.Note is { } note ? new[] { note } : []],
        };
    }

    private async Task<ErpOrderResultDto> UpdateAsync(
        Guid id, ErpWorkOrderRequest row, Guid materialId, ErpOrderChange change, ErpOrderWriter writer, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var (locked, _, jobs) = await writer.LockAsync(id, cancellationToken);
        var order = locked.Value;
        ErpOrderResultDto Result(string action, params string[] messages) =>
            new() { ExternalKey = order.ExternalKey!, OrderNumber = order.OrderNumber, Action = action, WorkOrderId = order.Id, Messages = messages };

        if (change.Version is not null && change.Version == order.ExternalVersion)
        {
            return Result(ErpOrderAction.Unchanged, $"Version {change.Version} was applied already.");
        }

        if (order.Status is WorkOrderStatus.Closed or WorkOrderStatus.Cancelled or WorkOrderStatus.Completed)
        {
            return Result(ErpOrderAction.Unchanged, $"{order.OrderNumber} is {order.Status} in Pulaski; the ERP's change comes too late and is left out.");
        }

        if (materialId != order.MaterialId)
        {
            throw new DomainRuleException($"{order.OrderNumber} makes another material in Pulaski; the ERP can't change an order's product. Cancel it and send a new order.");
        }

        var sentQty = order.ErpQuantity ?? order.PlannedQty;
        var sentUom = order.ErpUomId ?? order.UomId;
        if (!change.Cancelled && change.Quantity == sentQty && change.UomId == sentUom && change.PlannedStart == order.PlannedStart
            && change.DueAt == order.DueAt && change.Priority == order.Priority && order.PendingChange is null)
        {
            if (change.Version is not null)
            {
                await workOrders.UpdateFromErpAsync(order with { ExternalVersion = change.Version, ImportedAt = change.ReceivedAt }, now, cancellationToken);
            }

            return Result(ErpOrderAction.Unchanged, "Nothing changed.");
        }

        // While the order is planned, the ERP's word applies; once dispatched it waits for a planner (D-066).
        if (order.Status == WorkOrderStatus.Planned)
        {
            var (_, notes) = await writer.ApplyAsync(order, jobs, change, EventTypes.WorkOrderErpChanged, row.ExternalSystem.Trim(), now, cancellationToken);
            return Result(change.Cancelled ? ErpOrderAction.Cancelled : ErpOrderAction.Updated, [.. notes]);
        }

        var pending = order with { PendingChange = change, ImportedAt = change.ReceivedAt };
        var envelope = events.Create(EventTypes.WorkOrderErpChangePending, "work_order", order.Id, new { Erp = change, order.Status }, row.ExternalSystem.Trim());
        await workOrders.UpdateFromErpAsync(pending, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return Result(ErpOrderAction.Pending, $"{order.OrderNumber} is {order.Status}; the change waits for a planner to accept or reject it.");
    }

    private static string? InventoryText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Message(Exception ex) => ex switch
    {
        ValidationException v => string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))),
        _ => ex.Message,
    };
}

internal sealed class AcceptErpChangeHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IUnitRepository units,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider,
    ILineBatchSizes? lineBatches = null) : ICommandHandler<AcceptErpChangeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(AcceptErpChangeCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var writer = new ErpOrderWriter(workOrders, processes, boms, units, scheduling, eventLog, events, lineBatches);
        var (locked, _, jobs) = await writer.LockAsync(command.WorkOrderId, cancellationToken);
        ErpDecisions.EnsurePending(locked, command.ExpectedVersion);
        var (version, _) = await writer.ApplyAsync(locked.Value, jobs, locked.Value.PendingChange!, EventTypes.WorkOrderErpChangeAccepted, null, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class RejectErpChangeHandler(
    IUnitOfWork unitOfWork, IWorkOrderRepository workOrders, IEventLog eventLog, IEventFactory events) : ICommandHandler<RejectErpChangeCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(RejectErpChangeCommand command, CancellationToken cancellationToken)
    {
        var reason = command.Reason.Trim();
        new Validator().RequireText(reason, "Reason", 500).ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var locked = await workOrders.GetForUpdateAsync(command.WorkOrderId, cancellationToken) ?? throw new NotFoundException($"Work order {command.WorkOrderId} not found.");
        ErpDecisions.EnsurePending(locked, command.ExpectedVersion);
        var envelope = events.Create(EventTypes.WorkOrderErpChangeRejected, "work_order", locked.Value.Id, new { Erp = locked.Value.PendingChange }, reason);
        var version = await workOrders.UpdateFromErpAsync(locked.Value with { PendingChange = null }, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetWorkOrderQuantityHandler(
    IUnitOfWork unitOfWork,
    IWorkOrderRepository workOrders,
    IProcessRepository processes,
    IBomRepository boms,
    IUnitRepository units,
    ISchedulingRepository scheduling,
    IEventLog eventLog,
    IEventFactory events,
    TimeProvider timeProvider,
    ILineBatchSizes? lineBatches = null) : ICommandHandler<SetWorkOrderQuantityCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetWorkOrderQuantityCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.Quantity > 0, "Quantity", "Must be greater than zero.").ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var writer = new ErpOrderWriter(workOrders, processes, boms, units, scheduling, eventLog, events, lineBatches);
        var (locked, _, jobs) = await writer.LockAsync(command.WorkOrderId, cancellationToken);
        if (locked.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"Work order {locked.Value.OrderNumber} was changed by someone else. Reload and try again.");
        }

        var order = locked.Value;
        if (order.Status != WorkOrderStatus.Planned)
        {
            throw new DomainRuleException($"{order.OrderNumber} is {order.Status}; only a planned order's quantity is changed here.");
        }

        if (order.IsFromErp && !order.QuantityHeld)
        {
            throw new DomainRuleException($"The ERP owns {order.OrderNumber}'s quantity; change it there.");
        }

        var notes = await writer.ReplanAsync(order, jobs, command.Quantity, held: false, TimeSpan.Zero, null, timeProvider.GetUtcNow(), cancellationToken);
        var erpQty = order.ErpQuantity ?? order.PlannedQty;
        var after = order with
        {
            PlannedQty = command.Quantity,
            QuantityHeld = false,
            BatchAdjustment = !order.IsFromErp || command.Quantity == erpQty ? null
                : command.Quantity > erpQty ? BatchAdjustment.RoundedUp : BatchAdjustment.RoundedDown,
            ErpQuantity = order.IsFromErp && command.Quantity != erpQty ? erpQty : null,
            ErpUomId = order.IsFromErp && command.Quantity != erpQty ? order.ErpUomId ?? order.UomId : null,
        };
        var envelope = events.Create(EventTypes.WorkOrderQuantitySet, "work_order", order.Id,
            new { Before = ErpOrderWriter.Fields(order), After = ErpOrderWriter.Fields(after), Jobs = notes });
        var version = await workOrders.UpdateFromErpAsync(after, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class SetErpBatchRuleHandler(IUnitOfWork unitOfWork, IWorkOrderRepository workOrders, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetErpBatchRuleCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetErpBatchRuleCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(ErpBatchRule.All.Contains(command.Rule), "Rule", "reject, round_up, round_down or hold.").ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var envelope = events.Create(EventTypes.ErpBatchRuleSet, "equipment", command.SiteId, new { command.Rule });
        await workOrders.SetSiteErpBatchRuleAsync(command.SiteId, command.Rule, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(0);
    }
}

internal sealed class SetMaterialErpBatchRuleHandler(
    IUnitOfWork unitOfWork, IMaterialAdminRepository materials, IWorkOrderRepository workOrders, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<SetMaterialErpBatchRuleCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(SetMaterialErpBatchRuleCommand command, CancellationToken cancellationToken)
    {
        new Validator().Require(command.Rule is null || ErpBatchRule.All.Contains(command.Rule), "Rule", "reject, round_up, round_down or hold; blank for the site's.").ThrowIfInvalid();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await materials.GetForUpdateAsync(command.MaterialId, cancellationToken) ?? throw new NotFoundException($"Material {command.MaterialId} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var envelope = events.Create(EventTypes.ErpBatchRuleSet, "material", command.MaterialId, new { command.Rule });
        var version = await workOrders.SetMaterialErpBatchRuleAsync(command.MaterialId, command.Rule, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version!.Value);
    }
}

internal static class ErpDecisions
{
    public static void EnsurePending(Versioned<WorkOrder> order, long expectedVersion)
    {
        if (order.Version != expectedVersion)
        {
            throw new ConflictException($"Work order {order.Value.OrderNumber} was changed by someone else. Reload and try again.");
        }

        if (order.Value.PendingChange is null)
        {
            throw new DomainRuleException($"{order.Value.OrderNumber} has no change from the ERP waiting.");
        }
    }
}
