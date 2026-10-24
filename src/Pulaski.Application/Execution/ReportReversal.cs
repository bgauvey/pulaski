using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Execution;

/// <summary>
/// Reverses a production report whole (D-063): correction rows offset its counts, the run's totals drop by what it reported, and every
/// lot it made gives the quantity back (each must still hold it). A partial batch it reported no longer counts. Nothing recorded is
/// changed. Like a report, only until the job is closed.
/// </summary>
public sealed record ReverseProductionReportCommand(Guid JobId, Guid ReportId, Guid ReasonCodeId, string Comment) : ICommand<ReverseProductionResponse>;

internal sealed class ReverseProductionReportHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ReverseProductionReportCommand, ReverseProductionResponse>
{
    public async Task<ReverseProductionResponse> HandleAsync(ReverseProductionReportCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose a reason.")
            .Require(comment is not null, nameof(command.Comment), "Say why it is reversed.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The job, then its run, then the lots, as a report locks them (D-027); the report is read again under the job's lock.
        var found = await execution.GetReportAsync(command.ReportId, cancellationToken);
        if (found is null || found.JobId != command.JobId)
        {
            throw new NotFoundException($"Report {command.ReportId} of that job wasn't found. A report recorded before reports could be reversed whole can't be.");
        }

        var job = await InventoryRules.JobAsync(inventory, found.JobId, null, cancellationToken);
        var run = await execution.GetRunForUpdateAsync(found.RunId, cancellationToken) ?? throw new NotFoundException($"Run {found.RunId} not found.");
        var report = (await execution.GetReportAsync(command.ReportId, cancellationToken))!;
        if (report.IsReversed)
        {
            throw new DomainRuleException("That report was reversed already.");
        }

        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, job.SiteId, comment, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var writer = new ReversalWriter(inventory, eventLog, events, ids, now);

        // Each lot the report made, and the balance it joined when it went where lots mix (D-031).
        var produced = (await inventory.ListReportEventsAsync(report.ReportId, cancellationToken))
            .Where(e => e.Movement.Kind == LotEventKind.Produced && e.ReversedBy is null).ToList();
        var joined = new Dictionary<Guid, (LotEventDetail LotRow, LotEventDetail BalanceRow)>();
        foreach (var row in produced)
        {
            var later = await inventory.ListEventsAfterAsync(row.LotId, row, cancellationToken);
            var next = later.Count > 0 ? later[0] : null;
            if (next is { Movement.Kind: LotEventKind.Merged, RelatedLotId: not null, ReversedBy: null } && next.Movement.QtyDelta < 0
                && await inventory.GetCounterpartAsync(next, cancellationToken) is { } balanceRow)
            {
                joined[row.LotId] = (next, balanceRow);
            }
        }

        var locked = (await inventory.LockLotsAsync([.. produced.Select(r => r.LotId).Concat(joined.Values.Select(j => j.BalanceRow.LotId)).Distinct()], cancellationToken))
            .ToDictionary(l => l.Lot.Id, l => l.Lot);
        var lots = new List<LotTransactionResponse>();
        foreach (var row in produced)
        {
            var lot = locked[row.LotId];
            var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
            if (joined.TryGetValue(lot.Id, out var join))
            {
                // Out of the balance first, back into the lot, then the lot's production is taken back.
                var balance = locked[join.BalanceRow.LotId];
                var quantity = join.BalanceRow.Movement.QtyDelta!.Value;
                var (balanceAfter, balanceMovement) = ReverseMovementHandler.TakeBack(balance, quantity, false, material.BaseUomCode,
                    await inventory.ListEventsAfterAsync(balance.Id, join.BalanceRow, cancellationToken));
                await writer.WriteAsync(balance, balanceAfter, balanceMovement, join.BalanceRow, reason, comment!, material.BaseUomCode, cancellationToken, lot.Id, report.ReportId);
                locked[balance.Id] = balanceAfter;
                var (restored, restoreMovement) = lot.Restore(quantity, join.LotRow.Movement.FromStatus);
                await writer.WriteAsync(lot, restored, restoreMovement, join.LotRow, reason, comment!, material.BaseUomCode, cancellationToken, balance.Id, report.ReportId);
                await inventory.SupersedeGenealogyAsync(lot.Id, balance.Id, GenealogyEdge.Merge, null, now, cancellationToken);
                lot = restored;
            }

            var (after, movement) = ReverseMovementHandler.TakeBack(lot, row.Movement.QtyDelta!.Value, lot.Origin == LotOrigin.Produced, material.BaseUomCode,
                joined.ContainsKey(lot.Id) ? [] : await inventory.ListEventsAfterAsync(lot.Id, row, cancellationToken));
            var version = await writer.WriteAsync(lot, after, movement, row, reason, comment!, material.BaseUomCode, cancellationToken, reportId: report.ReportId);
            locked[lot.Id] = after;
            if (!await inventory.HasDeclarationAsync(lot.Id, job.Id, LotEventKind.Produced, cancellationToken))
            {
                await inventory.SupersedeGenealogyAsync(null, lot.Id, GenealogyEdge.Declared, job.Id, now, cancellationToken);
            }

            if (await execution.GetPartialBatchOfLotForUpdateAsync(lot.Id, cancellationToken) is { } partial)
            {
                await execution.UpdatePartialBatchAsync(partial.Reverse(), now, cancellationToken);
            }

            lots.Add(InventoryRules.Response(after, version));
        }

        // The counts: each row offset by a correction at the same time, so production by shift and OEE see it as never made.
        var envelope = events.Create(
            EventTypes.ProductionReversed, "job", job.Id,
            new
            {
                report.ReportId,
                RunId = run.Id,
                run.RunNo,
                GoodQty = report.Good,
                ScrapQty = report.Scrap,
                LotIds = lots.Select(l => l.LotId),
                ReasonCodeId = reason.Id,
                Comment = comment,
            },
            reason.Code);
        foreach (var row in report.Rows)
        {
            await execution.InsertCountAsync(
                new ProductionCountRow(ids.NewId(), row.OccurredAt, row.EquipmentId, row.RunId, row.JobId, row.CountKind, -row.DeltaQty, row.UomId,
                    row.ScrapReasonCodeId, envelope.ActorId, comment, now)
                {
                    ReportId = report.ReportId,
                    CorrectsId = row.Id,
                    Source = "correction",
                },
                cancellationToken);
        }

        var runAfter = run.Remove(report.Good, report.Scrap);
        await execution.UpdateRunAsync(runAfter, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new ReverseProductionResponse { RunId = run.Id, RunGoodQty = runAfter.GoodQty, RunScrapQty = runAfter.ScrapQty, Lots = lots };
    }
}
