using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Execution;

/// <summary>Approves a partial batch waiting for approval (D-036): its lot is released for use.</summary>
public sealed record ApprovePartialBatchCommand(Guid Id, string? Comment = null) : ICommand<PartialBatchDecisionResponse>;

/// <summary>Rejects a partial batch waiting for approval (D-036): its lot is written off, and its quantity moves from good to scrap.</summary>
public sealed record RejectPartialBatchCommand(Guid Id, Guid ScrapReasonCodeId, string? Comment = null) : ICommand<PartialBatchDecisionResponse>;

/// <summary>Locks a partial batch in the reporting order (D-027, D-036): its job, then it, then its run, then its lot.</summary>
internal static class PartialBatchLocks
{
    /// <summary>Whoever reported a partial batch doesn't decide on it (D-037): someone else approves or rejects.</summary>
    public static void EnsureNotOwn(PartialBatch partial, string actorId)
    {
        if (partial.ReportedBy == actorId && actorId != "anonymous")
        {
            throw new ForbiddenException("You reported this partial batch, so someone else must approve or reject it.");
        }
    }

    public static async Task<(InventoryJob Job, PartialBatch Partial, Lot Lot)> LockAsync(
        IInventoryRepository inventory, IExecutionRepository execution, Guid id, CancellationToken cancellationToken)
    {
        var peek = await execution.GetPartialBatchForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Partial batch {id} not found.");
        var job = await inventory.GetJobForUpdateAsync(peek.JobId, cancellationToken) ?? throw new NotFoundException($"Job {peek.JobId} not found.");
        var partial = (await execution.GetPartialBatchForUpdateAsync(id, cancellationToken))!;
        var lot = await InventoryRules.LotAsync(inventory, partial.LotId, null, cancellationToken);
        return (job, partial, lot);
    }
}

internal sealed class ApprovePartialBatchHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ApprovePartialBatchCommand, PartialBatchDecisionResponse>
{
    public async Task<PartialBatchDecisionResponse> HandleAsync(ApprovePartialBatchCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator().RequireComment(command.Comment).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (job, partial, lot) = await PartialBatchLocks.LockAsync(inventory, execution, command.Id, cancellationToken);
        var envelope = events.Create(EventTypes.PartialBatchApproved, "partial_batch", partial.Id, new { Before = new { partial.Status }, LotId = lot.Id, Comment = comment });
        PartialBatchLocks.EnsureNotOwn(partial, envelope.ActorId);
        var approved = partial.Approve(timeProvider.GetUtcNow(), envelope.ActorId, comment);
        await execution.UpdatePartialBatchAsync(approved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        // Released for use; someone may already have released it by hand.
        if (lot.Status == LotStatus.OnHold)
        {
            var (released, movement) = lot.ChangeStatus(LotStatus.Available);
            var change = events.Create(
                EventTypes.LotStatusChanged, "lot", lot.Id,
                new { lot.LotNumber, Before = new { lot.Status }, After = new { released.Status }, PartialBatchId = partial.Id, Comment = comment });
            await inventory.UpdateLotAsync(released, change.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, change.ActorId, JobId: job.Id, Comment: comment, RunId: partial.RunId),
                change.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(change, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return new PartialBatchDecisionResponse(partial.Id, approved.Status, [$"Partial batch of {job.JobNumber} approved: lot {lot.LotNumber} is available."]);
    }
}

internal sealed class RejectPartialBatchHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IExecutionRepository execution,
    Units.IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<RejectPartialBatchCommand, PartialBatchDecisionResponse>
{
    public async Task<PartialBatchDecisionResponse> HandleAsync(RejectPartialBatchCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ScrapReasonCodeId != Guid.Empty, nameof(command.ScrapReasonCodeId), "Choose why it is scrapped.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var (job, partial, lot) = await PartialBatchLocks.LockAsync(inventory, execution, command.Id, cancellationToken);
        await InventoryRules.ReasonAsync(inventory, command.ScrapReasonCodeId, ReasonKinds.Scrap, job.SiteId, comment, cancellationToken);
        var envelope = events.Create(
            EventTypes.PartialBatchRejected, "partial_batch", partial.Id,
            new { Before = new { partial.Status }, LotId = lot.Id, command.ScrapReasonCodeId, Comment = comment });
        PartialBatchLocks.EnsureNotOwn(partial, envelope.ActorId);
        var rejected = partial.Reject(timeProvider.GetUtcNow(), envelope.ActorId, command.ScrapReasonCodeId, comment);
        await execution.UpdatePartialBatchAsync(rejected, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        // Whatever is left of the lot is written off, and counts as scrap of the run it was made in.
        var notes = new List<string>();
        if (lot.Quantity > 0)
        {
            var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
            var (scrapped, movement) = lot.Take(lot.Quantity, LotEventKind.Scrapped, material.BaseUomCode);
            var writeOff = events.Create(
                EventTypes.LotWrittenOff, "lot", lot.Id,
                new { lot.LotNumber, Quantity = lot.Quantity, ReasonCodeId = command.ScrapReasonCodeId, PartialBatchId = partial.Id, Comment = comment });
            await inventory.UpdateLotAsync(scrapped, writeOff.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, writeOff.ActorId, JobId: job.Id, ReasonCodeId: command.ScrapReasonCodeId, Comment: comment, RunId: partial.RunId),
                writeOff.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(writeOff, cancellationToken);

            var run = await execution.GetRunForUpdateAsync(partial.RunId, cancellationToken);
            if (run is not null && job.UomId is { } jobUom)
            {
                var converter = await RunReporting.ConverterAsync(units, lot.MaterialId, cancellationToken);
                var quantity = converter.CanConvert(lot.UomId, jobUom) ? converter.Convert(lot.Quantity, lot.UomId, jobUom) : 0m;
                if (quantity > 0)
                {
                    await execution.UpdateRunAsync(run.Reclassify(quantity), cancellationToken);
                    await execution.InsertCountAsync(
                        new ProductionCountRow(ids.NewId(), partial.OccurredAt, run.EquipmentId, run.Id, job.Id, CountKind.Defective, quantity, jobUom,
                            command.ScrapReasonCodeId, envelope.ActorId, comment ?? "Partial batch rejected", timeProvider.GetUtcNow()),
                        cancellationToken);
                }
            }

            notes.Add($"Lot {lot.LotNumber} written off: {Batching.Qty(lot.Quantity)} {material.BaseUomCode} now counts as scrap of run {run?.RunNo}.");
        }

        await transaction.CommitAsync(cancellationToken);
        return new PartialBatchDecisionResponse(partial.Id, rejected.Status, [$"Partial batch of {job.JobNumber} rejected.", .. notes]);
    }
}
