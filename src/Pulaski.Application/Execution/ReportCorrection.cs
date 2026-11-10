using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Materials;
using Pulaski.Application.Units;
using Pulaski.Contracts.Execution;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Execution;

/// <summary>
/// Corrects a production report's figures (#80, D-096) as one action: in one transaction the report is reversed whole (D-063) and the new figures
/// reported into the same run at the same time, by every rule a report follows (rate cap, batches, partial batch, backflush). Its lots are new
/// lots; the old report and lots stay, reversed. Good, scrap, the scrap reason and batch weights may change; the run and the time can't.
/// </summary>
public sealed record CorrectProductionReportCommand(
    Guid JobId, Guid ReportId, decimal GoodQty, decimal ScrapQty, Guid? ScrapReasonCodeId, Guid ReasonCodeId, string Comment)
    : ICommand<CorrectProductionResponse>
{
    /// <summary>Batch materials (D-036): each good batch's weight in the base unit.</summary>
    public IReadOnlyList<decimal> GoodBatches { get; init; } = [];

    public IReadOnlyList<decimal> ScrapBatches { get; init; } = [];

    public decimal? PartialQty { get; init; }

    /// <summary>Where good output goes when the report made none before; else where its lots were.</summary>
    public Guid? LocationId { get; init; }
}

internal sealed class CorrectProductionReportHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILineBatchSizes? lineBatches = null,
    IFeedRepository? feeds = null,
    IAllocationRepository? allocations = null) : ICommandHandler<CorrectProductionReportCommand, CorrectProductionResponse>
{
    public async Task<CorrectProductionResponse> HandleAsync(CorrectProductionReportCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var report = await execution.GetReportAsync(command.ReportId, cancellationToken);
        if (report is null || report.JobId != command.JobId)
        {
            throw new NotFoundException($"Report {command.ReportId} of that job wasn't found. A report recorded before reports could be reversed whole can't be corrected.");
        }

        if (report.IsReversed)
        {
            throw new DomainRuleException("That report was reversed already; report the right figures instead.");
        }

        // What it was: its time, unit, scrap reason and where its lots went, so the new figures land as it did.
        var at = report.Rows.Min(r => r.OccurredAt);
        var uom = report.Rows[0].UomId;
        var scrapReason = report.Rows.FirstOrDefault(r => r.CountKind == CountKind.Defective)?.ScrapReasonCodeId;
        var produced = (await inventory.ListReportEventsAsync(report.ReportId, cancellationToken))
            .Where(e => e.Movement.Kind == LotEventKind.Produced && e.ReversedBy is null).ToList();
        var location = produced.Select(e => e.Movement.ToLocationId).FirstOrDefault(l => l is not null) ?? command.LocationId;
        var before = new { GoodQty = report.Good, ScrapQty = report.Scrap, ScrapReasonCodeId = scrapReason, GoodBatches = produced.Select(e => e.Movement.QtyDelta) };
        var unchanged = command.GoodQty == report.Good && command.ScrapQty == report.Scrap && command.ScrapReasonCodeId == scrapReason
            && command.GoodBatches.Count == 0 && command.ScrapBatches.Count == 0 && command.PartialQty is null;
        if (unchanged)
        {
            throw new DomainRuleException("Those are the report's figures already; nothing to correct.");
        }

        var reversed = await new ReverseProductionReportHandler(unitOfWork, inventory, execution, eventLog, events, ids, timeProvider, feeds)
            .ReverseAsync(new ReverseProductionReportCommand(command.JobId, command.ReportId, command.ReasonCodeId, command.Comment), cancellationToken);

        // The new figures, at the report's own time: back-dated only when that is not just now.
        var reported = await new ProductionReporter(inventory, units, execution, eventLog, events, ids, lineBatches, feeds, allocations).ReportAsync(
            new ReportProductionCommand(command.JobId, report.RunId, command.GoodQty, command.ScrapQty, uom, command.ScrapQty > 0 ? command.ScrapReasonCodeId : null,
                location, Comment: command.Comment)
            {
                EffectiveAt = at < now.AddMinutes(-1) ? at : null,
                GoodBatches = command.GoodBatches,
                ScrapBatches = command.ScrapBatches,
                PartialQty = command.PartialQty,
            },
            onStop: false, now, cancellationToken);

        await eventLog.AppendAsync(
            events.Create(
                EventTypes.ProductionCorrected, "job", command.JobId,
                new
                {
                    CorrectsReportId = report.ReportId,
                    reported.ReportId,
                    report.RunId,
                    Before = before,
                    After = new { command.GoodQty, command.ScrapQty, command.ScrapReasonCodeId, command.GoodBatches, command.ScrapBatches, command.PartialQty },
                    TakenBack = reversed.Lots.Select(l => l.LotId),
                    Made = reported.Lots.Select(l => l.LotId),
                    command.ReasonCodeId,
                    Comment = InventoryRules.Text(command.Comment),
                }),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CorrectProductionResponse
        {
            ReportId = reported.ReportId,
            CorrectsReportId = report.ReportId,
            RunId = reported.RunId,
            RunGoodQty = reported.RunGoodQty,
            RunScrapQty = reported.RunScrapQty,
            TakenBack = reversed.Lots,
            Lots = reported.Lots,
            Warnings = reported.Warnings,
        };
    }
}
