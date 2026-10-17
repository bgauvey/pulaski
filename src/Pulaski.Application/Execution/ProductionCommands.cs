using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Application.Units;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Execution;

/// <summary>
/// Good and scrap made by a production job (D-035). Without <paramref name="RunId"/> it goes into the
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
    string? Comment = null) : ICommand<ReportProductionResponse>;

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
        var comment = InventoryRules.Text(command.Comment);
        var lotNumber = InventoryRules.Text(command.LotNumber);
        new Validator()
            .Require(command.JobId != Guid.Empty, nameof(command.JobId), "Choose a job.")
            .Require(command.GoodQty >= 0, nameof(command.GoodQty), "Can't be negative.")
            .Require(command.ScrapQty >= 0, nameof(command.ScrapQty), "Can't be negative.")
            .Require(command.GoodQty + command.ScrapQty > 0, nameof(command.GoodQty), "Report a good or scrap quantity greater than zero.")
            .Require(command.ScrapQty == 0 || command.ScrapReasonCodeId is not null, nameof(command.ScrapReasonCodeId), "Choose why it was scrapped.")
            .Require(command.GoodQty == 0 || command.LocationId is not null, nameof(command.LocationId), "Choose where the good output goes.")
            .RequireLotNumber(lotNumber, nameof(command.LotNumber))
            .RequireComment(command.Comment)
            .ThrowIfInvalid();
        var now = timeProvider.GetUtcNow();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The job, then its run, then the lot (D-027): reports on one job serialize.
        var job = await InventoryRules.JobAsync(inventory, command.JobId, null, cancellationToken);
        var run = await RunReporting.RunForAsync(execution, job, command.RunId, "production", cancellationToken);
        var jobUom = job.UomId ?? throw new DomainRuleException($"{job.JobNumber} has no unit.");
        var unit = command.UomId ?? jobUom;
        var converter = await RunReporting.ConverterAsync(units, job.MaterialId!.Value, cancellationToken);
        if (!converter.CanConvert(unit, jobUom))
        {
            throw new DomainRuleException($"{converter.Code(unit)} doesn't convert to {converter.Code(jobUom)}, the unit of {job.JobNumber}; enter it in another unit.");
        }

        var good = converter.Convert(command.GoodQty, unit, jobUom);
        var scrap = converter.Convert(command.ScrapQty, unit, jobUom);
        if (command.ScrapReasonCodeId is { } reasonId)
        {
            await InventoryRules.ReasonAsync(inventory, reasonId, ReasonKinds.Scrap, job.SiteId, comment, cancellationToken);
        }

        List<InventoryWarningDto> warnings = [];
        var (rate, rateWarning) = await RunReporting.IdealRateAsync(execution, converter, run, job.MaterialId!.Value, jobUom, job.LineCode, cancellationToken);
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
                Entered = new { command.GoodQty, command.ScrapQty, UomId = unit },
                GoodQty = good,
                ScrapQty = scrap,
                command.ScrapReasonCodeId,
                Late = !run.IsOpen,
                OccurredAt = occurredAt,
                Comment = comment,
            });
        await execution.UpdateRunAsync(after, cancellationToken);
        await execution.InsertCountAsync(
            new ProductionCountRow(ids.NewId(), occurredAt, run.EquipmentId, run.Id, job.Id, CountKind.Processed, good + scrap, jobUom, null, envelope.ActorId, comment),
            cancellationToken);
        if (scrap > 0)
        {
            await execution.InsertCountAsync(
                new ProductionCountRow(ids.NewId(), occurredAt, run.EquipmentId, run.Id, job.Id, CountKind.Defective, scrap, jobUom, command.ScrapReasonCodeId, envelope.ActorId, comment),
                cancellationToken);
        }

        await eventLog.AppendAsync(envelope, cancellationToken);

        LotTransactionResponse? lot = null;
        if (command.GoodQty > 0)
        {
            lot = await ProductionOutput.ProduceAsync(
                inventory, units, eventLog, events, ids, job, command.LocationId!.Value, command.GoodQty, unit, lotNumber, command.ExpiresAt, comment, run.Id, now, cancellationToken);
            warnings.AddRange(lot.Warnings);
        }

        await transaction.CommitAsync(cancellationToken);
        return new ReportProductionResponse
        {
            RunId = run.Id,
            RunGoodQty = after.GoodQty,
            RunScrapQty = after.ScrapQty,
            CapQty = rate is { } r ? RateCap.Cap(r, policy.RateTolerancePct, run.Elapsed(now)) : null,
            Lot = lot,
            Warnings = warnings,
        };
    }
}
