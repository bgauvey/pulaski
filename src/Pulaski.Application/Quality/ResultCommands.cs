using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Quality;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Quality;

/// <summary>
/// Records a result at the site against a lot, a job (its run at the time) or both (#65). It is checked against the material's released
/// specification; a failure of a characteristic that holds on failure puts the lot on hold.
/// </summary>
public sealed record RecordQualityResultCommand(
    Guid SiteId,
    Guid CharacteristicId,
    Guid? LotId,
    Guid? JobId,
    DateTimeOffset? OccurredAt,
    IReadOnlyList<decimal>? Values,
    int? Inspected,
    int? Defects,
    string Source,
    string? Comment) : ICommand<QualityResultResponse>;

/// <summary>Leaves a wrong result out, with a reason; it stays on record and is never changed otherwise.</summary>
public sealed record ExcludeQualityResultCommand(Guid SiteId, Guid Id, DateTimeOffset OccurredAt, Guid ReasonCodeId, string? Comment) : ICommand<QualityResultDto>;

/// <summary>
/// Rejects a held lot on quality (#65): what is left is written off as scrap, and, for a lot made in a run, taken off that run's good output
/// as a defective count at the time it was made, so OEE quality shows it.
/// </summary>
public sealed record RejectLotCommand(Guid LotId, long ExpectedVersion, Guid ScrapReasonCodeId, string? Comment) : ICommand<LotTransactionResponse>;

/// <summary>Checks a result, writes it and holds its lot when it must (#65). Runs in the caller's transaction, after the lot is locked.</summary>
internal sealed class QualityRecorder(
    IQualityRepository quality, IInventoryRepository inventory, IEventLog eventLog, IEventFactory events, IIdGenerator ids, IRequestContext request)
{
    public const string HoldReason = "OUT-OF-SPEC";

    public async Task<QualityResultResponse> RecordAsync(
        Guid siteId,
        Characteristic characteristic,
        Lot? lot,
        QualityJobContext? job,
        QualityMeasurement measurement,
        DateTimeOffset occurredAt,
        string source,
        string? comment,
        CancellationToken cancellationToken)
    {
        var materialId = lot?.MaterialId ?? job?.MaterialId ?? throw new DomainRuleException("The job makes no material, so there is nothing to check the result against.");
        var inForce = await quality.GetItemInForceAsync(materialId, characteristic.Id, cancellationToken);
        var conforms = QualityCheck.Conforms(measurement, inForce?.Item);
        var row = new QualityResultRow(
            ids.NewId(), occurredAt, siteId, characteristic.Id, job?.RunEquipmentId ?? job?.EquipmentId, job?.JobId, job?.RunId, lot?.Id, materialId,
            measurement, SampleStatistics.Of(measurement), inForce?.Item.Id, conforms, source, request.UserId, "", occurredAt, comment);
        var envelope = events.Create(
            EventTypes.QualityResultRecorded, "quality_result", row.Id,
            new
            {
                characteristic.Code,
                LotId = lot?.Id,
                LotNumber = lot?.LotNumber,
                JobId = job?.JobId,
                job?.JobNumber,
                OccurredAt = occurredAt,
                measurement.Values,
                measurement.Inspected,
                measurement.Defects,
                SpecItemId = inForce?.Item.Id,
                SpecVersion = inForce?.VersionNumber,
                Conforms = conforms,
                Source = source,
                Comment = comment,
            });
        row = row with { RecordedBy = envelope.ActorId, RecordedAt = envelope.OccurredAt };
        await quality.InsertResultAsync(row, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        var notes = new List<string>();
        var held = false;
        if (inForce is { Item: var item } && conforms == false)
        {
            var failure = QualityCheck.Failure(measurement, item);
            notes.Add($"{characteristic.Code} is out of specification: {failure} (version {inForce.Value.VersionNumber}: {QualityCheck.Limits(item)}).");
            if (item.HoldOnFail && lot is not null)
            {
                held = await HoldAsync(lot, characteristic, failure, cancellationToken);
                notes.Add(held ? $"{lot.Name} was put on hold." : $"{lot.Name} is {Display(lot)}, so it wasn't put on hold.");
            }
        }
        else if (inForce is null)
        {
            notes.Add($"{characteristic.Code} has no limits in the material's released specification, so the result wasn't checked.");
        }

        return new QualityResultResponse
        {
            Id = row.Id,
            OccurredAt = occurredAt,
            Conforms = conforms,
            Limits = inForce is { Item: var limits } ? QualityCheck.Limits(limits) : null,
            LotHeld = held,
            Notes = notes,
        };
    }

    // Only available stock is held; a lot already held, used up or gone is left as it is.
    private async Task<bool> HoldAsync(Lot lot, Characteristic characteristic, string failure, CancellationToken cancellationToken)
    {
        if (lot.Status != LotStatus.Available || lot.Quantity <= 0)
        {
            return false;
        }

        var reasonId = await quality.FindReasonAsync(ReasonKinds.Hold, HoldReason, lot.SiteId, cancellationToken);
        var comment = $"{characteristic.Code} out of specification: {failure}";
        var (after, movement) = lot.ChangeStatus(LotStatus.OnHold);
        var envelope = events.Create(
            EventTypes.LotStatusChanged, "lot", lot.Id,
            new { lot.LotNumber, From = lot.Status, To = after.Status, ReasonCodeId = reasonId, Comment = comment, Quality = true },
            HoldReason);
        await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reasonId, Comment: comment), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return true;
    }

    private static string Display(Lot lot) => lot.Quantity <= 0 ? "used up" : lot.Status.Replace('_', ' ');
}

internal sealed class RecordQualityResultHandler(
    IUnitOfWork unitOfWork,
    IQualityRepository quality,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    IRequestContext request,
    TimeProvider timeProvider) : ICommandHandler<RecordQualityResultCommand, QualityResultResponse>
{
    public const int MaxBackDays = 31;

    public async Task<QualityResultResponse> HandleAsync(RecordQualityResultCommand command, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var occurredAt = command.OccurredAt ?? now;
        var measurement = new QualityMeasurement(command.Values, command.Inspected, command.Defects);
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.CharacteristicId != Guid.Empty, nameof(command.CharacteristicId), "Choose a characteristic.")
            .Require(command.LotId is not null || command.JobId is not null, nameof(command.LotId), "Choose a lot, a job or both.")
            .Require(QualitySources.Entered.Contains(command.Source), nameof(command.Source), "manual or lab.")
            .Require(occurredAt <= now.AddMinutes(5), nameof(command.OccurredAt), "Can't be in the future.")
            .Require(occurredAt >= now.AddDays(-MaxBackDays), nameof(command.OccurredAt), $"At most {MaxBackDays} days ago.")
            .Require(comment is null || comment.Length <= 1000, nameof(command.Comment), "At most 1000 characters.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var characteristic = (await quality.GetCharacteristicsAsync([command.CharacteristicId], cancellationToken)).SingleOrDefault()
            ?? throw new NotFoundException($"Characteristic {command.CharacteristicId} not found.");
        if (!characteristic.IsActive)
        {
            throw new DomainRuleException($"{characteristic.Code} is switched off.");
        }

        if (measurement.Problem(characteristic.DataKind) is { } problem)
        {
            throw new ValidationException(new Dictionary<string, string[]> { [characteristic.DataKind == CharacteristicKinds.Variable ? "Values" : "Inspected"] = [problem] });
        }

        // The job is only read; the lot is locked, as every lot change does, since a failure may hold it.
        var job = command.JobId is { } jobId
            ? await quality.GetJobContextAsync(jobId, occurredAt, cancellationToken) ?? throw new NotFoundException($"Job {jobId} not found.")
            : null;
        var lot = command.LotId is { } lotId ? await InventoryRules.LotAsync(inventory, lotId, null, cancellationToken) : null;
        if (job is not null && job.SiteId != command.SiteId || lot is not null && lot.SiteId != command.SiteId)
        {
            throw new DomainRuleException("The lot and job must be at the site the result is recorded for.");
        }

        var response = await new QualityRecorder(quality, inventory, eventLog, events, ids, request)
            .RecordAsync(command.SiteId, characteristic, lot, job, measurement, occurredAt, command.Source, comment, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }
}

internal sealed class ExcludeQualityResultHandler(
    IUnitOfWork unitOfWork, IQualityRepository quality, IQualityReadStore reads, IInventoryRepository inventory, IEventLog eventLog, IEventFactory events)
    : ICommandHandler<ExcludeQualityResultCommand, QualityResultDto>
{
    public async Task<QualityResultDto> HandleAsync(ExcludeQualityResultCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose why it is left out.")
            .Require(comment is null || comment.Length <= 1000, nameof(command.Comment), "At most 1000 characters.")
            .ThrowIfInvalid();

        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken))
        {
            var result = await quality.GetResultForUpdateAsync(command.Id, command.OccurredAt, cancellationToken) is { } found && found.SiteId == command.SiteId
                ? found
                : throw new NotFoundException($"Result {command.Id} not found at the site.");
            if (result.IsExcluded)
            {
                throw new DomainRuleException("The result is already left out.");
            }

            var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.SampleExclusion, result.SiteId, comment, cancellationToken);
            var envelope = events.Create(
                EventTypes.QualityResultExcluded, "quality_result", result.Id, new { result.OccurredAt, ReasonCodeId = reason.Id, Comment = comment }, reason.Code);
            await quality.ExcludeResultAsync(result.Id, result.OccurredAt, reason.Id, comment, envelope.ActorId, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return await reads.GetResultAsync(command.Id, command.OccurredAt, cancellationToken) ?? throw new NotFoundException($"Result {command.Id} not found.");
    }
}

internal sealed class RejectLotHandler(
    IUnitOfWork unitOfWork,
    IQualityRepository quality,
    IInventoryRepository inventory,
    IExecutionRepository execution,
    Units.IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<RejectLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(RejectLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ScrapReasonCodeId != Guid.Empty, nameof(command.ScrapReasonCodeId), "Choose why it is scrapped.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // Where it was made never changes, so it is read first; then the job, its run and the lot, the reporting order (D-027, D-036).
        var made = await quality.GetLotProductionAsync(command.LotId, cancellationToken);
        var job = made is null ? null : await inventory.GetJobForUpdateAsync(made.JobId, cancellationToken);
        var run = made?.RunId is { } runId ? await execution.GetRunForUpdateAsync(runId, cancellationToken) : null;
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        if (lot.Status is not (LotStatus.OnHold or LotStatus.Quarantine))
        {
            throw new DomainRuleException($"{lot.Name} isn't on hold; hold it first, then reject it.");
        }

        if (lot.Quantity <= 0)
        {
            throw new DomainRuleException($"Nothing is left of {lot.Name} to reject.");
        }

        await InventoryRules.ReasonAsync(inventory, command.ScrapReasonCodeId, ReasonKinds.Scrap, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = lot.Quantity;
        var (scrapped, movement) = lot.Take(quantity, LotEventKind.Scrapped, material.BaseUomCode);
        var envelope = events.Create(
            EventTypes.LotWrittenOff, "lot", lot.Id,
            new { lot.LotNumber, Quantity = quantity, ReasonCodeId = command.ScrapReasonCodeId, RejectedOnQuality = true, JobId = job?.Id, RunId = run?.Id, Comment = comment });
        var version = await inventory.UpdateLotAsync(scrapped, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, JobId: job?.Id, ReasonCodeId: command.ScrapReasonCodeId, Comment: comment, RunId: run?.Id),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        // Made in a run: the quantity moves from good to scrap there, at the time it was made, as a rejected partial batch does (D-036).
        var warnings = new List<InventoryWarningDto>();
        if (run is not null && job is not null && made is not null && job.UomId is { } jobUom)
        {
            var converter = await RunReporting.ConverterAsync(units, lot.MaterialId, cancellationToken);
            var defective = converter.CanConvert(lot.UomId, jobUom) ? converter.Convert(quantity, lot.UomId, jobUom) : 0m;
            if (defective > 0)
            {
                await execution.UpdateRunAsync(run.Reclassify(defective), cancellationToken);
                await execution.InsertCountAsync(
                    new ProductionCountRow(ids.NewId(), made.EffectiveAt, run.EquipmentId, run.Id, job.Id, CountKind.Defective, defective, jobUom,
                        command.ScrapReasonCodeId, envelope.ActorId, comment ?? "Rejected on quality", timeProvider.GetUtcNow()),
                    cancellationToken);
                warnings.Add(new("counted_as_scrap",
                    $"{Qty(defective)} now counts as scrap of {job.JobNumber}'s run {run.RunNo}, so it is taken off its good output and OEE quality."));
            }
            else
            {
                warnings.Add(new("not_counted", $"{material.BaseUomCode} doesn't convert to the job's unit, so the run's good output wasn't changed."));
            }
        }
        else
        {
            warnings.Add(new("not_made_here", $"{lot.Name} wasn't made in a run, so only the stock was written off."));
        }

        await transaction.CommitAsync(cancellationToken);
        return InventoryRules.Response(scrapped, version, warnings);
    }

    private static string Qty(decimal value) => value.ToString("#,0.######", CultureInfo.InvariantCulture);
}
