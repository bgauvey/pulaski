using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// A new lot of a material at a location (D-027). A material that isn't lot-tracked takes no lot
/// number: the quantity is added to its balance at the location.
/// </summary>
public sealed record ReceiveLotCommand(
    Guid MaterialId,
    Guid LocationId,
    string? LotNumber,
    decimal Quantity,
    Guid UomId,
    string? SupplierLot = null,
    DateTimeOffset? ExpiresAt = null,
    string? Comment = null,
    Guid? RunId = null,
    DateTimeOffset? EffectiveAt = null,
    Guid? ExpectedReceiptId = null) : ICommand<LotTransactionResponse>;

/// <summary>Adds found or miscounted stock to a lot, for an adjustment reason; back-dated to <see cref="EffectiveAt"/> when given (D-057).</summary>
public sealed record WriteUpLotCommand(
    Guid LotId, long ExpectedVersion, decimal Quantity, Guid UomId, Guid ReasonCodeId, string? Comment = null, DateTimeOffset? EffectiveAt = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Removes stock from a lot for an adjustment reason; a null quantity takes all of it. Back-dated when <see cref="EffectiveAt"/> is given.</summary>
public sealed record WriteOffLotCommand(
    Guid LotId, long ExpectedVersion, decimal? Quantity, Guid? UomId, Guid ReasonCodeId, string? Comment = null, DateTimeOffset? EffectiveAt = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Moves a lot to another location; a bulk balance moves <see cref="Quantity"/> of itself.</summary>
public sealed record MoveLotCommand(
    Guid LotId, long ExpectedVersion, Guid ToLocationId, decimal? Quantity = null, Guid? UomId = null, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Holds, quarantines (a hold reason is required) or releases a lot.</summary>
public sealed record ChangeLotStatusCommand(
    Guid LotId, long ExpectedVersion, string Status, Guid? ReasonCodeId = null, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>
/// Declares quantity of an available lot used by a production job, in its open run or in the run named
/// (D-035): material is used while a job runs, so a job with no run open needs the run it belongs to.
/// </summary>
/// <remarks>Back-dated to <see cref="EffectiveAt"/> (D-057), it goes into the run going on then.</remarks>
public sealed record ConsumeLotCommand(
    Guid LotId, Guid JobId, decimal Quantity, Guid UomId, string? Comment = null, Guid? RunId = null, DateTimeOffset? EffectiveAt = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Declares a production job's output into a lot at a location.</summary>
public sealed record ProduceLotCommand(
    Guid JobId,
    Guid LocationId,
    decimal Quantity,
    Guid UomId,
    string? LotNumber = null,
    DateTimeOffset? ExpiresAt = null,
    string? Comment = null,
    Guid? RunId = null,
    DateTimeOffset? EffectiveAt = null) : ICommand<LotTransactionResponse>;

/// <summary>Shared by the movement handlers (D-057).</summary>
internal static class MovementTime
{
    /// <summary>The effective time when the movement is back-dated; null when it counts as recorded.</summary>
    public static DateTimeOffset? Backdated(DateTimeOffset effective, DateTimeOffset now) => effective < now ? effective : null;
}

internal sealed class ReceiveLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IExpectedReceiptRepository expectedReceipts) : ICommandHandler<ReceiveLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(ReceiveLotCommand command, CancellationToken cancellationToken)
    {
        var lotNumber = InventoryRules.Text(command.LotNumber);
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.MaterialId != Guid.Empty, nameof(command.MaterialId), "Required.")
            .Require(command.LocationId != Guid.Empty, nameof(command.LocationId), "Required.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .Require(command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .RequireLotNumber(lotNumber, nameof(command.LotNumber))
            .Require(command.SupplierLot is null || command.SupplierLot.Length <= InventoryRules.MaxLotNumber, nameof(command.SupplierLot), $"At most {InventoryRules.MaxLotNumber} characters.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, command.MaterialId, cancellationToken);
        if (!material.IsActive)
        {
            throw new DomainRuleException($"{material.Code} is inactive.");
        }

        // A lot-tracked material needs a number, except where lots mix: there a blank one goes straight into the balance (D-031).
        var location = await InventoryRules.LocationAsync(inventory, command.LocationId, null, cancellationToken);
        await InventoryRules.EnsureAcceptsAsync(inventory, location, material.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var effective = Backdating.EffectiveAt(command.EffectiveAt, now, comment);
        var expiresAt = Lot.ExpiryFor(command.ExpiresAt, material.ShelfLifeDays, effective);
        new Validator()
            .Require(!material.IsLotTracked || location.ComminglesLots || lotNumber is not null, nameof(command.LotNumber), $"Required: {material.Code} is lot-tracked.")
            .Require(material.IsLotTracked || lotNumber is null, nameof(command.LotNumber), $"Leave blank: {material.Code} isn't lot-tracked, so it is kept as one balance per location.")
            .ThrowIfInvalid();

        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);
        var siteId = location.SiteId!.Value;

        Lot lot;
        bool isNew;
        if (lotNumber is not null)
        {
            if (await inventory.FindLotForUpdateAsync(siteId, material.Id, lotNumber!, cancellationToken) is not null)
            {
                throw new ConflictException($"Lot {lotNumber} of {material.Code} already exists in this site. Use another number, or write up the existing lot.");
            }

            lot = Lot.Create(ids.NewId(), siteId, material.Id, lotNumber!, material.BaseUomId, location.Id, isBulk: false,
                LotOrigin.Received, InventoryRules.Text(command.SupplierLot), expiresAt);
            isNew = true;
        }
        else
        {
            (lot, isNew) = await Commingling.BalanceAsync(inventory, ids, siteId, material.Id, material.BaseUomId, location.Id, LotOrigin.Received, cancellationToken);
            lot = lot with { ExpiresAt = Commingling.Earliest(lot.ExpiresAt, expiresAt) };
        }

        if (!isNew)
        {
            await Backdating.EnsureFitsAsync(inventory, lot, quantity, effective, now, material.BaseUomCode, cancellationToken);
        }

        var (received, movement) = lot.Add(quantity, LotEventKind.Received);
        var envelope = events.Create(
            EventTypes.LotReceived,
            "lot",
            received.Id,
            new
            {
                received.LotNumber,
                received.MaterialId,
                LocationId = location.Id,
                Entered = new { command.Quantity, command.UomId },
                Quantity = quantity,
                received.UomId,
                QuantityAfter = received.Quantity,
                received.SupplierLot,
                received.ExpiresAt,
                EffectiveAt = MovementTime.Backdated(effective, now),
                Comment = comment,
            });
        var version = isNew
            ? await inventory.InsertLotAsync(received, envelope.OccurredAt, cancellationToken)
            : await inventory.UpdateLotAsync(received, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), received.Id, movement, received.UomId, envelope.ActorId, Comment: comment, EffectiveAt: MovementTime.Backdated(effective, now)),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        // What was on its way is here: the expected receipt closes (D-058).
        if (command.ExpectedReceiptId is { } expected)
        {
            await ExpectedReceiving.CloseAsync(expectedReceipts, eventLog, events, expected, received, cancellationToken);
        }

        // A numbered receipt where lots mix is kept, for its genealogy, then joins the balance (D-031).
        if (location.ComminglesLots && !received.IsBulk)
        {
            var (balance, balanceVersion, note) = await Commingling.MergeArrivalAsync(inventory, eventLog, events, ids, received, location.Code, comment, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InventoryRules.Response(balance, balanceVersion, [note]);
        }

        await transaction.CommitAsync(cancellationToken);
        return InventoryRules.Response(received, version);
    }
}

internal sealed class WriteUpLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<WriteUpLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(WriteUpLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose a reason.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var effective = Backdating.EffectiveAt(command.EffectiveAt, now, comment);
        await Backdating.EnsureFitsAsync(inventory, lot, quantity, effective, now, material.BaseUomCode, cancellationToken);

        var (after, movement) = lot.Add(quantity, LotEventKind.Adjusted);
        var envelope = events.Create(
            EventTypes.LotWrittenUp,
            "lot",
            lot.Id,
            new
            {
                lot.LotNumber,
                Entered = new { command.Quantity, command.UomId },
                Quantity = quantity,
                QuantityAfter = after.Quantity,
                ReasonCodeId = reason.Id,
                EffectiveAt = MovementTime.Backdated(effective, now),
                Comment = comment,
            },
            reason.Code);
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason.Id, Comment: comment, EffectiveAt: MovementTime.Backdated(effective, now)),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version);
    }
}

internal sealed class WriteOffLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<WriteOffLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(WriteOffLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.Quantity is null || command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero, or blank for the whole lot.")
            .Require(command.Quantity is null || command.UomId is { } u && u != Guid.Empty, nameof(command.UomId), "Required with a quantity.")
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose a reason.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = command.Quantity is { } entered
            ? await InventoryRules.ToBaseAsync(units, material, entered, command.UomId!.Value, cancellationToken)
            : lot.Quantity;
        if (quantity == 0)
        {
            throw new DomainRuleException($"{lot.Name} is empty; there is nothing to write off.");
        }

        var now = timeProvider.GetUtcNow();
        var effective = Backdating.EffectiveAt(command.EffectiveAt, now, comment);
        await Backdating.EnsureFitsAsync(inventory, lot, -quantity, effective, now, material.BaseUomCode, cancellationToken);
        var (after, movement) = lot.Take(quantity, LotEventKind.Scrapped, material.BaseUomCode);
        var envelope = events.Create(
            EventTypes.LotWrittenOff,
            "lot",
            lot.Id,
            new
            {
                lot.LotNumber,
                Entered = new { command.Quantity, command.UomId },
                Quantity = quantity,
                QuantityAfter = after.Quantity,
                ReasonCodeId = reason.Id,
                EffectiveAt = MovementTime.Backdated(effective, now),
                Comment = comment,
            },
            reason.Code);
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason.Id, Comment: comment, EffectiveAt: MovementTime.Backdated(effective, now)),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version);
    }
}

internal sealed class MoveLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<MoveLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(MoveLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.ToLocationId != Guid.Empty, nameof(command.ToLocationId), "Required.")
            .Require(command.Quantity is null || command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .Require(command.Quantity is null || command.UomId is { } u && u != Guid.Empty, nameof(command.UomId), "Required with a quantity.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        var to = await InventoryRules.LocationAsync(inventory, command.ToLocationId, lot.SiteId, cancellationToken);
        await InventoryRules.EnsureAcceptsAsync(inventory, to, lot.MaterialId, cancellationToken);

        if (!lot.IsBulk)
        {
            var (moved, movement) = lot.MoveTo(to.Id);
            var envelope = events.Create(
                EventTypes.LotMoved, "lot", lot.Id, new { lot.LotNumber, FromLocationId = lot.LocationId, ToLocationId = to.Id, lot.Quantity, Comment = comment });
            var version = await inventory.UpdateLotAsync(moved, envelope.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, Comment: comment), envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);

            // Moved where lots mix, the lot joins the balance there (D-031).
            if (to.ComminglesLots)
            {
                var (balance, balanceVersion, note) = await Commingling.MergeArrivalAsync(
                    inventory, eventLog, events, ids, moved, to.Code, comment, timeProvider.GetUtcNow(), cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return InventoryRules.Response(balance, balanceVersion, [note]);
            }

            await transaction.CommitAsync(cancellationToken);
            return InventoryRules.Response(moved, version);
        }

        // A balance sends a quantity to the destination's balance, made on its first transfer in: mixed stock stays mixed.
        if (command.Quantity is not { } entered)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                [nameof(command.Quantity)] = ["Required: a balance moves by quantity."],
            });
        }

        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, entered, command.UomId!.Value, cancellationToken);
        var (source, outgoing) = lot.TransferOut(quantity, to.Id, material.BaseUomCode);
        var (target, isNewTarget) = await Commingling.BalanceAsync(
            inventory, ids, lot.SiteId, lot.MaterialId, lot.UomId, to.Id, LotOrigin.Received, cancellationToken, lot.Status);
        if (target.Status != lot.Status)
        {
            throw new DomainRuleException(
                $"The balance at {to.Code} is {target.Status.Replace('_', ' ')} and this one is {lot.Status.Replace('_', ' ')}; stock moves between balances in the same status.");
        }

        var (destination, incoming) = target.TransferIn(quantity, lot.LocationId!.Value);

        var sent = events.Create(
            EventTypes.LotMoved, "lot", source.Id,
            new { source.LotNumber, FromLocationId = lot.LocationId, ToLocationId = to.Id, Quantity = quantity, QuantityAfter = source.Quantity, ToLotId = destination.Id, Comment = comment });
        var arrived = events.Create(
            EventTypes.LotMoved, "lot", destination.Id,
            new { destination.LotNumber, FromLocationId = lot.LocationId, ToLocationId = to.Id, Quantity = quantity, QuantityAfter = destination.Quantity, FromLotId = source.Id, Comment = comment });
        var sourceVersion = await inventory.UpdateLotAsync(source, sent.OccurredAt, cancellationToken);
        if (isNewTarget)
        {
            await inventory.InsertLotAsync(destination, arrived.OccurredAt, cancellationToken);
        }
        else
        {
            await inventory.UpdateLotAsync(destination, arrived.OccurredAt, cancellationToken);
        }

        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), source.Id, outgoing, lot.UomId, sent.ActorId, Comment: comment, RelatedLotId: destination.Id), sent.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), destination.Id, incoming, lot.UomId, arrived.ActorId, Comment: comment, RelatedLotId: source.Id), arrived.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(sent, cancellationToken);
        await eventLog.AppendAsync(arrived, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(source, sourceVersion);
    }
}

internal sealed class ChangeLotStatusHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<ChangeLotStatusCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(ChangeLotStatusCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        var holding = command.Status is LotStatus.OnHold or LotStatus.Quarantine;
        new Validator()
            .Require(LotStatus.IsOnHand(command.Status), nameof(command.Status), "Must be available, on_hold or quarantine.")
            .Require(!holding || command.ReasonCodeId is { } r && r != Guid.Empty, nameof(command.ReasonCodeId), "Choose a hold reason.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        var reason = command.ReasonCodeId is { } reasonId && reasonId != Guid.Empty
            ? await InventoryRules.ReasonAsync(inventory, reasonId, ReasonKinds.Hold, lot.SiteId, comment, cancellationToken)
            : null;

        var (after, movement) = lot.ChangeStatus(command.Status);
        var envelope = events.Create(
            EventTypes.LotStatusChanged,
            "lot",
            lot.Id,
            new { lot.LotNumber, From = lot.Status, To = after.Status, ReasonCodeId = reason?.Id, Comment = comment },
            reason?.Code);
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason?.Id, Comment: comment),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version);
    }
}

internal sealed class ConsumeLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ConsumeLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(ConsumeLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.JobId != Guid.Empty, nameof(command.JobId), "Choose a job.")
            .Require(command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // The job first, then the lot (D-027): declarations on one job serialize, so consumption
        // and production on it see each other's genealogy.
        var now = timeProvider.GetUtcNow();
        var effective = Backdating.EffectiveAt(command.EffectiveAt, now, comment);
        var job = await InventoryRules.JobAsync(inventory, command.JobId, null, cancellationToken);
        var run = await RunReporting.RunForAsync(execution, job, command.RunId, "consumption", cancellationToken, effective < now ? effective : null);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, null, cancellationToken);
        if (lot.SiteId != job.SiteId)
        {
            throw new DomainRuleException($"{lot.Name} is in another site than {job.JobNumber}.");
        }

        // Usable then, not necessarily now: a back-dated consumption is checked at the time it counts (D-057).
        InventoryRules.EnsureNotExpired(lot, effective, "consumed");
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);
        await Backdating.EnsureFitsAsync(inventory, lot, -quantity, effective, now, material.BaseUomCode, cancellationToken);
        var (after, movement) = lot.Take(quantity, LotEventKind.Consumed, material.BaseUomCode);

        List<InventoryWarningDto> warnings = [];
        if (!await inventory.PlansComponentAsync(job.Id, material.Id, cancellationToken))
        {
            warnings.Add(new("not_planned", $"{job.JobNumber} has no planned component of {material.Code}; it was recorded as a substitution."));
        }
        else if (await OverPlannedAsync(job, material, quantity, cancellationToken) is { } over)
        {
            warnings.Add(new("over_planned", over));
        }

        var edges = InventoryRules.Edges(ids, [lot.Id], await inventory.ListLotsProducedByJobAsync(job.Id, cancellationToken), job.Id);
        var envelope = events.Create(
            EventTypes.LotConsumed,
            "lot",
            lot.Id,
            new
            {
                lot.LotNumber,
                JobId = job.Id,
                job.JobNumber,
                Entered = new { command.Quantity, command.UomId },
                Quantity = quantity,
                QuantityAfter = after.Quantity,
                ChildLotIds = edges.Select(e => e.ChildLotId),
                RunId = run.Id,
                EffectiveAt = MovementTime.Backdated(effective, now),
                Comment = comment,
            });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, JobId: job.Id, Comment: comment, RunId: run.Id, EffectiveAt: MovementTime.Backdated(effective, now)),
            envelope.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(edges, envelope.OccurredAt, cancellationToken, MovementTime.Backdated(effective, now));
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version, warnings);
    }

    /// <summary>
    /// Using more of a component than the job's plan allows, for what it has made, is recorded with a
    /// warning (D-035): the floor knows what it used, but someone should look.
    /// </summary>
    private async Task<string?> OverPlannedAsync(InventoryJob job, InventoryMaterial material, decimal quantity, CancellationToken cancellationToken)
    {
        if (await execution.GetPlannedComponentAsync(job.Id, material.Id, cancellationToken) is not { } planned
            || job.PlannedQty is not { } jobPlanned)
        {
            return null;
        }

        var converter = await RunReporting.ConverterAsync(units, material.Id, cancellationToken);
        if (!converter.CanConvert(planned.UomId, material.BaseUomId))
        {
            return null;
        }

        var policy = await execution.GetPolicyAsync(job.SiteId, cancellationToken);
        return ConsumptionCheck.Warning(
            converter.Convert(planned.Quantity, planned.UomId, material.BaseUomId),
            jobPlanned,
            await execution.GetMadeAsync(job.Id, cancellationToken),
            await execution.GetConsumedAsync(job.Id, material.Id, cancellationToken),
            quantity,
            policy.ConsumptionTolerancePct,
            material.Code,
            material.BaseUomCode);
    }
}

internal sealed class ProduceLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IExecutionRepository execution,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ProduceLotCommand, LotTransactionResponse>
{
    /// <summary>Production is a report of good output, so it goes through the same run and rate checks (D-035).</summary>
    public async Task<LotTransactionResponse> HandleAsync(ProduceLotCommand command, CancellationToken cancellationToken)
    {
        var report = await new ReportProductionHandler(unitOfWork, inventory, units, execution, eventLog, events, ids, timeProvider).HandleAsync(
            new ReportProductionCommand(
                command.JobId, command.RunId, command.Quantity, 0m, command.UomId, null, command.LocationId, command.LotNumber, command.ExpiresAt, command.Comment)
            {
                EffectiveAt = command.EffectiveAt,
            },
            cancellationToken);
        return report.Lot!;
    }
}

/// <summary>Puts a production job's good output into a lot (D-027, D-031): the lot-making half of a production report.</summary>
internal static class ProductionOutput
{
    /// <summary>Runs inside the caller's transaction, with the job already locked.</summary>
    public static async Task<LotTransactionResponse> ProduceAsync(
        IInventoryRepository inventory,
        IUnitRepository units,
        IEventLog eventLog,
        IEventFactory events,
        IIdGenerator ids,
        InventoryJob job,
        Guid locationId,
        decimal enteredQuantity,
        Guid enteredUomId,
        string? lotNumber,
        DateTimeOffset? expiresAtEntered,
        string? comment,
        Guid? runId,
        DateTimeOffset now,
        DateTimeOffset? effectiveAt,
        CancellationToken cancellationToken)
    {
        var location = await InventoryRules.LocationAsync(inventory, locationId, job.SiteId, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, job.MaterialId!.Value, cancellationToken);
        await InventoryRules.EnsureAcceptsAsync(inventory, location, material.Id, cancellationToken);
        var effective = effectiveAt ?? now;
        var expiresAt = Lot.ExpiryFor(expiresAtEntered, material.ShelfLifeDays, effective);
        var quantity = await InventoryRules.ToBaseAsync(units, material, enteredQuantity, enteredUomId, cancellationToken);
        var produced = await inventory.ListLotsProducedByJobAsync(job.Id, cancellationToken);
        // Where lots mix, a blank number goes straight into the balance; a number makes a lot that then joins it (D-031).
        Versioned<Lot>? existing;
        Lot lot;
        if (material.IsLotTracked && !(location.ComminglesLots && lotNumber is null))
        {
            if (lotNumber is null)
            {
                // The next free <job>-<nn>; a number someone typed by hand is skipped.
                var sequence = produced.Count;
                do
                {
                    lotNumber = Lot.ProducedNumber(job.JobNumber, ++sequence);
                    existing = await inventory.FindLotForUpdateAsync(job.SiteId, material.Id, lotNumber, cancellationToken);
                }
                while (existing is not null);
            }
            else
            {
                existing = await inventory.FindLotForUpdateAsync(job.SiteId, material.Id, lotNumber, cancellationToken);
                if (existing is { Value: var found } && (found.ProducedByJobId != job.Id || found.LocationId != location.Id))
                {
                    throw new ConflictException(found.ProducedByJobId == job.Id
                        ? $"Lot {lotNumber} is at another location; produce into it there, or use another number."
                        : $"Lot {lotNumber} of {material.Code} already exists and wasn't made by {job.JobNumber}. Use another number.");
                }
            }

            lot = existing?.Value ?? Lot.Create(ids.NewId(), job.SiteId, material.Id, lotNumber, material.BaseUomId, location.Id,
                isBulk: false, LotOrigin.Produced, expiresAt: expiresAt, producedByJobId: job.Id);
        }
        else
        {
            if (lotNumber is not null && !material.IsLotTracked)
            {
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["LotNumber"] = [$"Leave blank: {material.Code} isn't lot-tracked, so it is kept as one balance per location."],
                });
            }

            var (balance, isNew) = await Commingling.BalanceAsync(
                inventory, ids, job.SiteId, material.Id, material.BaseUomId, location.Id, LotOrigin.Produced, cancellationToken);
            existing = isNew ? null : new Versioned<Lot>(balance, Version: 0); // only whether it exists matters below
            lot = balance with { ExpiresAt = Commingling.Earliest(balance.ExpiresAt, expiresAt) };
        }

        if (existing is not null)
        {
            await Backdating.EnsureFitsAsync(inventory, lot, quantity, effective, now, material.BaseUomCode, cancellationToken);
        }

        var (after, movement) = lot.Add(quantity, LotEventKind.Produced);
        var edges = InventoryRules.Edges(ids, await inventory.ListLotsConsumedByJobAsync(job.Id, cancellationToken), [after.Id], job.Id);
        var envelope = events.Create(
            EventTypes.LotProduced,
            "lot",
            after.Id,
            new
            {
                after.LotNumber,
                after.MaterialId,
                JobId = job.Id,
                job.JobNumber,
                LocationId = location.Id,
                Entered = new { Quantity = enteredQuantity, UomId = enteredUomId },
                Quantity = quantity,
                QuantityAfter = after.Quantity,
                ParentLotIds = edges.Select(e => e.ParentLotId),
                RunId = runId,
                EffectiveAt = MovementTime.Backdated(effective, now),
                Comment = comment,
            });
        var version = existing is null
            ? await inventory.InsertLotAsync(after, envelope.OccurredAt, cancellationToken)
            : await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), after.Id, movement, after.UomId, envelope.ActorId, JobId: job.Id, Comment: comment, RunId: runId, EffectiveAt: MovementTime.Backdated(effective, now)),
            envelope.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(edges, envelope.OccurredAt, cancellationToken, MovementTime.Backdated(effective, now));
        await eventLog.AppendAsync(envelope, cancellationToken);

        if (location.ComminglesLots && !after.IsBulk)
        {
            var (balance, balanceVersion, note) = await Commingling.MergeArrivalAsync(inventory, eventLog, events, ids, after, location.Code, comment, now, cancellationToken);
            return InventoryRules.Response(balance, balanceVersion, [note]);
        }

        return InventoryRules.Response(after, version);
    }
}
