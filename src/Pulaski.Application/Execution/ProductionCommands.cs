using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Units;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Materials;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Execution;

/// <summary>
/// Good and scrap made by a production job (D-035); for a material made in batches, whole batches and at most
/// one partial batch per run (D-036). Without <paramref name="RunId"/> it goes into the
/// job's open run; with one, into that run, also after it ended, until the job is closed. The run's
/// total can't pass its elapsed time at the line's ideal rate plus the site's tolerance.
/// </summary>
public sealed record ReportProductionCommand(
    Guid JobId,
    Guid? RunId,
    decimal GoodQty,
    decimal ScrapQty,
    Guid? UomId,
    Guid? ScrapReasonCodeId,
    Guid? LocationId,
    string? LotNumber = null,
    DateTimeOffset? ExpiresAt = null,
    string? Comment = null) : ICommand<ReportProductionResponse>
{
    /// <summary>For a material made in batches (D-036): each good batch's weight in the base unit, one lot each.</summary>
    public IReadOnlyList<decimal> GoodBatches { get; init; } = [];

    /// <summary>Each rejected batch's weight in the base unit.</summary>
    public IReadOnlyList<decimal> ScrapBatches { get; init; } = [];

    /// <summary>A partial batch in the base unit: as the line stops, or later into a run that ended with a reason.</summary>
    public decimal? PartialQty { get; init; }
}

/// <summary>The run a report or a consumption belongs to, and the rules every report shares (D-035).</summary>
internal static class RunReporting
{
    /// <summary>
    /// The job's open run, or the run named: it must be the job's. Consumption and production happen
    /// in a run (D-035), so a job with no run open needs the run they belong to.
    /// </summary>
    public static async Task<JobRun> RunForAsync(IExecutionRepository execution, InventoryJob job, Guid? runId, string what, CancellationToken cancellationToken)
    {
        if (runId is { } id)
        {
            var run = await execution.GetRunForUpdateAsync(id, cancellationToken);
            return run is not null && run.JobId == job.Id
                ? run
                : throw new DomainRuleException($"That run isn't one of {job.JobNumber}'s.");
        }

        return await execution.GetOpenRunForUpdateAsync(job.Id, cancellationToken)
            ?? throw new DomainRuleException(
                $"{job.JobNumber} isn't running, so {what} can't be recorded now. Start the job, or choose the run it belongs to.");
    }

    /// <summary>When a report counts as made: now for an open run, the run's end for one entered afterwards.</summary>
    public static DateTimeOffset OccurredAt(JobRun run, DateTimeOffset now) => run.EndedAt ?? now;

    /// <summary>The material's converter, for the job's unit, the entered unit and the rate's unit.</summary>
    public static async Task<UnitConverter> ConverterAsync(IUnitRepository units, Guid materialId, CancellationToken cancellationToken) =>
        UnitConversions.For(materialId, await units.GetUnitsAsync(cancellationToken), await units.GetConversionsAsync([materialId], cancellationToken));

    /// <summary>The ideal rate in the job's unit, from the capability in effect when the run started; null with a warning when it can't be had.</summary>
    public static async Task<(decimal? Rate, InventoryWarningDto? Warning)> IdealRateAsync(
        IExecutionRepository execution, UnitConverter converter, JobRun run, Guid materialId, Guid jobUomId, string lineCode, CancellationToken cancellationToken)
    {
        if (await execution.GetIdealRateAsync(run.EquipmentId, materialId, run.StartedAt, cancellationToken) is not { } ideal)
        {
            return (null, new("not_checked", $"{lineCode} had no capability for the material when the run started, so the rate wasn't checked."));
        }

        if (!converter.CanConvert(ideal.UomId, jobUomId))
        {
            return (null, new("not_checked", $"The ideal rate is in {converter.Code(ideal.UomId)}, which doesn't convert to {converter.Code(jobUomId)}; the rate wasn't checked."));
        }

        return (converter.Convert(ideal.RatePerHour, ideal.UomId, jobUomId), null);
    }
}

internal sealed class ReportProductionHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ReportProductionCommand, ReportProductionResponse>
{
    public async Task<ReportProductionResponse> HandleAsync(ReportProductionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var result = await new ProductionReporter(inventory, units, execution, eventLog, events, ids)
            .ReportAsync(command, onStop: false, timeProvider.GetUtcNow(), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }
}

/// <summary>
/// Records a production report inside the caller's transaction (D-035, D-036): checks the run and the
/// rate, writes the counts, and puts good output into lots. Pausing and completing use it for the partial
/// batch of a line that stopped.
/// </summary>
internal sealed class ProductionReporter(
    IInventoryRepository inventory,
    IUnitRepository units,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids)
{
    /// <param name="command">The report.</param>
    /// <param name="onStop">The report is the partial batch of a run being ended now, by a pause or completion with a reason.</param>
    /// <param name="now">The time of the report.</param>
    /// <param name="cancellationToken">Cancels the work.</param>
    public async Task<ReportProductionResponse> ReportAsync(ReportProductionCommand command, bool onStop, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        var lotNumber = InventoryRules.Text(command.LotNumber);
        var hasBatches = command.GoodBatches.Count > 0 || command.ScrapBatches.Count > 0 || command.PartialQty is not null;
        new Validator()
            .Require(command.JobId != Guid.Empty, nameof(command.JobId), "Choose a job.")
            .Require(command.GoodQty >= 0, nameof(command.GoodQty), "Can't be negative.")
            .Require(command.ScrapQty >= 0, nameof(command.ScrapQty), "Can't be negative.")
            .Require(hasBatches || command.GoodQty + command.ScrapQty > 0, nameof(command.GoodQty), "Report a good or scrap quantity greater than zero.")
            .Require(!hasBatches || command.GoodQty + command.ScrapQty == 0, nameof(command.GoodQty), "Report batches or quantities, not both.")
            .Require(command.GoodBatches.All(b => b > 0) && command.ScrapBatches.All(b => b > 0), "Batches", "Each batch must weigh more than zero.")
            .Require(command.ScrapQty == 0 && command.ScrapBatches.Count == 0 || command.ScrapReasonCodeId is not null, nameof(command.ScrapReasonCodeId), "Choose why it was scrapped.")
            .Require(command.GoodQty == 0 && command.GoodBatches.Count == 0 && command.PartialQty is null || command.LocationId is not null, nameof(command.LocationId), "Choose where the good output goes.")
            .RequireLotNumber(lotNumber, nameof(command.LotNumber))
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        // The job, then its run, then the lots (D-027): reports on one job serialize.
        var job = await InventoryRules.JobAsync(inventory, command.JobId, null, cancellationToken);
        var run = await RunReporting.RunForAsync(execution, job, command.RunId, "production", cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, job.MaterialId!.Value, cancellationToken);
        var jobUom = job.UomId ?? throw new DomainRuleException($"{job.JobNumber} has no unit.");
        var converter = await RunReporting.ConverterAsync(units, material.Id, cancellationToken);

        // What was made, in the entered unit: the quantities, or for a batch material the batches in its base unit.
        Guid unit;
        decimal goodEntered;
        decimal scrapEntered;
        if (material.Batching is { } batching)
        {
            await EnsureBatchesAsync(command, batching, material, job, run, onStop, cancellationToken);
            unit = material.BaseUomId;
            goodEntered = command.GoodBatches.Sum() + (command.PartialQty ?? 0m);
            scrapEntered = command.ScrapBatches.Sum();
        }
        else
        {
            if (hasBatches)
            {
                throw new DomainRuleException($"{material.Code} isn't made in batches; report good and scrap quantities.");
            }

            unit = command.UomId ?? jobUom;
            goodEntered = command.GoodQty;
            scrapEntered = command.ScrapQty;
        }

        if (!converter.CanConvert(unit, jobUom))
        {
            throw new DomainRuleException($"{converter.Code(unit)} doesn't convert to {converter.Code(jobUom)}, the unit of {job.JobNumber}; enter it in another unit.");
        }

        var good = converter.Convert(goodEntered, unit, jobUom);
        var scrap = converter.Convert(scrapEntered, unit, jobUom);
        if (command.ScrapReasonCodeId is { } reasonId)
        {
            await InventoryRules.ReasonAsync(inventory, reasonId, ReasonKinds.Scrap, job.SiteId, comment, cancellationToken);
        }

        List<InventoryWarningDto> warnings = [];
        var (rate, rateWarning) = await RunReporting.IdealRateAsync(execution, converter, run, material.Id, jobUom, job.LineCode, cancellationToken);
        var policy = await execution.GetPolicyAsync(job.SiteId, cancellationToken);
        if (rate is { } idealRate)
        {
            RateCap.Ensure(idealRate, policy.RateTolerancePct, run.Elapsed(now), run.TotalQty, good + scrap, converter.Code(jobUom), job.LineCode);
        }
        else
        {
            warnings.Add(rateWarning!);
        }

        var after = run.Add(good, scrap);
        var occurredAt = RunReporting.OccurredAt(run, now);
        var envelope = events.Create(
            EventTypes.ProductionReported, "job", job.Id,
            new
            {
                RunId = run.Id,
                run.RunNo,
                Entered = new { GoodQty = goodEntered, ScrapQty = scrapEntered, UomId = unit },
                command.GoodBatches,
                command.ScrapBatches,
                command.PartialQty,
                GoodQty = good,
                ScrapQty = scrap,
                command.ScrapReasonCodeId,
                Late = !run.IsOpen,
                OccurredAt = occurredAt,
                Comment = comment,
            });
        await execution.UpdateRunAsync(after, cancellationToken);
        await execution.InsertCountAsync(
            new ProductionCountRow(ids.NewId(), occurredAt, run.EquipmentId, run.Id, job.Id, CountKind.Processed, good + scrap, jobUom, null, envelope.ActorId, comment, now),
            cancellationToken);
        if (scrap > 0)
        {
            await execution.InsertCountAsync(
                new ProductionCountRow(ids.NewId(), occurredAt, run.EquipmentId, run.Id, job.Id, CountKind.Defective, scrap, jobUom, command.ScrapReasonCodeId, envelope.ActorId, comment, now),
                cancellationToken);
        }

        await eventLog.AppendAsync(envelope, cancellationToken);

        // Good output into lots: one for a quantity, one per batch, and one for a partial batch.
        var lots = new List<LotTransactionResponse>();
        var outputs = material.Batching is null
            ? (goodEntered > 0 ? [goodEntered] : [])
            : command.GoodBatches.ToList();
        foreach (var quantity in outputs)
        {
            lots.Add(await ProduceAsync(job, command, quantity, unit, outputs.Count == 1 && command.PartialQty is null ? lotNumber : null, comment, run, now, cancellationToken));
        }

        if (command.PartialQty is { } partialQty)
        {
            var partialLot = await ProduceAsync(job, command, partialQty, unit, outputs.Count == 0 ? lotNumber : null, comment, run, now, cancellationToken);
            lots.Add(partialLot);
            warnings.Add(await RecordPartialAsync(job, material, run, partialLot, partialQty, occurredAt, envelope.ActorId, cancellationToken));
        }

        warnings.AddRange(lots.SelectMany(l => l.Warnings));
        return new ReportProductionResponse
        {
            RunId = run.Id,
            RunGoodQty = after.GoodQty,
            RunScrapQty = after.ScrapQty,
            CapQty = rate is { } r ? RateCap.Cap(r, policy.RateTolerancePct, run.Elapsed(now)) : null,
            Lot = lots.FirstOrDefault(),
            Lots = lots,
            Warnings = warnings,
        };
    }

    /// <summary>Only whole batches, and a partial batch only as the line stops or into a run that stopped with a reason (D-036).</summary>
    private async Task EnsureBatchesAsync(
        ReportProductionCommand command, Batching batching, InventoryMaterial material, InventoryJob job, JobRun run, bool onStop, CancellationToken cancellationToken)
    {
        if (command.GoodBatches.Count + command.ScrapBatches.Count == 0 && command.PartialQty is null)
        {
            throw new DomainRuleException($"{material.Code} is made in batches of {Batching.Qty(batching.Size)} {material.BaseUomCode}; report each batch.");
        }

        foreach (var batch in command.GoodBatches.Concat(command.ScrapBatches))
        {
            batching.EnsureBatch(batch, material.BaseUomCode);
        }

        if (command.PartialQty is not { } partial)
        {
            return;
        }

        batching.EnsurePartial(partial, material.BaseUomCode);
        if (!onStop && (run.IsOpen || run.EndReasonCodeId is null))
        {
            throw new DomainRuleException(run.IsOpen
                ? "A partial batch is reported when the line stops: pause or complete the job with a reason, and report it there."
                : $"Run {run.RunNo} ended without a reason, so it can't take a partial batch: a partial batch is an exception for a line that stopped.");
        }

        if (await execution.RunHasPartialBatchAsync(run.Id, cancellationToken))
        {
            throw new DomainRuleException($"Run {run.RunNo} of {job.JobNumber} already has its partial batch; a run stops once.");
        }

        if (batching.IsExact && (!material.IsLotTracked || await LocationMixesAsync(command.LocationId!.Value, job.SiteId, cancellationToken)))
        {
            throw new DomainRuleException(
                $"A partial batch of {material.Code} waits for approval in a lot of its own; choose a location that keeps lots apart.");
        }
    }

    private async Task<bool> LocationMixesAsync(Guid locationId, Guid siteId, CancellationToken cancellationToken) =>
        (await InventoryRules.LocationAsync(inventory, locationId, siteId, cancellationToken)).ComminglesLots;

    private Task<LotTransactionResponse> ProduceAsync(
        InventoryJob job, ReportProductionCommand command, decimal quantity, Guid unit, string? lotNumber, string? comment, JobRun run, DateTimeOffset now, CancellationToken cancellationToken) =>
        ProductionOutput.ProduceAsync(
            inventory, units, eventLog, events, ids, job, command.LocationId!.Value, quantity, unit, lotNumber, command.ExpiresAt, comment, run.Id, now, cancellationToken);

    /// <summary>The partial batch's record; under the exact rule its lot goes on hold until someone approves it (D-036).</summary>
    private async Task<InventoryWarningDto> RecordPartialAsync(
        InventoryJob job, InventoryMaterial material, JobRun run, LotTransactionResponse lot, decimal quantity, DateTimeOffset occurredAt, string actorId, CancellationToken cancellationToken)
    {
        var batching = material.Batching!;
        var partial = PartialBatch.Report(ids.NewId(), job.Id, run.Id, lot.LotId, quantity, material.BaseUomId, batching, occurredAt, actorId);
        var envelope = events.Create(EventTypes.PartialBatchReported, "partial_batch", partial.Id, new { After = partial, job.JobNumber });
        await execution.InsertPartialBatchAsync(partial, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        var label = $"{Batching.Qty(quantity)} {material.BaseUomCode} of a {Batching.Qty(batching.Size)} {material.BaseUomCode} batch";
        if (!batching.IsExact)
        {
            return new("partial_batch", $"Partial batch recorded: {label}, in lot {lot.LotNumber ?? "its balance"}.");
        }

        var current = await InventoryRules.LotAsync(inventory, lot.LotId, null, cancellationToken);
        var (held, movement) = current.ChangeStatus(LotStatus.OnHold);
        var reasonId = await execution.FindReasonAsync(ReasonKinds.Hold, "PARTIAL", job.SiteId, cancellationToken);
        var holdEvent = events.Create(
            EventTypes.LotStatusChanged, "lot", held.Id,
            new { held.LotNumber, Before = new { current.Status }, After = new { held.Status }, ReasonCodeId = reasonId, PartialBatchId = partial.Id });
        await inventory.UpdateLotAsync(held, holdEvent.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), held.Id, movement, held.UomId, holdEvent.ActorId, JobId: job.Id, ReasonCodeId: reasonId, RunId: run.Id),
            holdEvent.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(holdEvent, cancellationToken);
        return new("partial_batch_pending", $"Partial batch on hold for approval: {label}, in lot {held.LotNumber}. {material.Code} is sold in whole batches only.");
    }
}
