using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
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
    string? Comment = null) : ICommand<LotTransactionResponse>;

/// <summary>Adds found or miscounted stock to a lot, for an adjustment reason.</summary>
public sealed record WriteUpLotCommand(
    Guid LotId, long ExpectedVersion, decimal Quantity, Guid UomId, Guid ReasonCodeId, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Removes stock from a lot for an adjustment reason; a null quantity takes all of it.</summary>
public sealed record WriteOffLotCommand(
    Guid LotId, long ExpectedVersion, decimal? Quantity, Guid? UomId, Guid ReasonCodeId, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Moves a lot to another location; a bulk balance moves <see cref="Quantity"/> of itself.</summary>
public sealed record MoveLotCommand(
    Guid LotId, long ExpectedVersion, Guid ToLocationId, decimal? Quantity = null, Guid? UomId = null, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Holds, quarantines (a hold reason is required) or releases a lot.</summary>
public sealed record ChangeLotStatusCommand(
    Guid LotId, long ExpectedVersion, string Status, Guid? ReasonCodeId = null, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Declares quantity of an available lot used by an open production job.</summary>
public sealed record ConsumeLotCommand(Guid LotId, Guid JobId, decimal Quantity, Guid UomId, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Declares a production job's output into a lot at a location.</summary>
public sealed record ProduceLotCommand(
    Guid JobId,
    Guid LocationId,
    decimal Quantity,
    Guid UomId,
    string? LotNumber = null,
    DateTimeOffset? ExpiresAt = null,
    string? Comment = null) : ICommand<LotTransactionResponse>;

internal sealed class ReceiveLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ReceiveLotCommand, LotTransactionResponse>
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
        var expiresAt = Lot.ExpiryFor(command.ExpiresAt, material.ShelfLifeDays, now);
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
                Comment = comment,
            });
        var version = isNew
            ? await inventory.InsertLotAsync(received, envelope.OccurredAt, cancellationToken)
            : await inventory.UpdateLotAsync(received, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(new LotEventRow(ids.NewId(), received.Id, movement, received.UomId, envelope.ActorId, Comment: comment), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

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
    IIdGenerator ids) : ICommandHandler<WriteUpLotCommand, LotTransactionResponse>
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

        var (after, movement) = lot.Add(quantity, LotEventKind.Adjusted);
        var envelope = events.Create(
            EventTypes.LotWrittenUp,
            "lot",
            lot.Id,
            new { lot.LotNumber, Entered = new { command.Quantity, command.UomId }, Quantity = quantity, QuantityAfter = after.Quantity, ReasonCodeId = reason.Id, Comment = comment },
            reason.Code);
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason.Id, Comment: comment),
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
    IIdGenerator ids) : ICommandHandler<WriteOffLotCommand, LotTransactionResponse>
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

        var (after, movement) = lot.Take(quantity, LotEventKind.Scrapped, material.BaseUomCode);
        var envelope = events.Create(
            EventTypes.LotWrittenOff,
            "lot",
            lot.Id,
            new { lot.LotNumber, Entered = new { command.Quantity, command.UomId }, Quantity = quantity, QuantityAfter = after.Quantity, ReasonCodeId = reason.Id, Comment = comment },
            reason.Code);
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason.Id, Comment: comment),
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
        var job = await InventoryRules.JobAsync(inventory, command.JobId, null, cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, null, cancellationToken);
        if (lot.SiteId != job.SiteId)
        {
            throw new DomainRuleException($"{lot.Name} is in another site than {job.JobNumber}.");
        }

        InventoryRules.EnsureNotExpired(lot, timeProvider.GetUtcNow(), "consumed");
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);
        var (after, movement) = lot.Take(quantity, LotEventKind.Consumed, material.BaseUomCode);

        List<InventoryWarningDto> warnings = [];
        if (!await inventory.PlansComponentAsync(job.Id, material.Id, cancellationToken))
        {
            warnings.Add(new("not_planned", $"{job.JobNumber} has no planned component of {material.Code}; it was recorded as a substitution."));
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
                Comment = comment,
            });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, JobId: job.Id, Comment: comment),
            envelope.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(edges, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version, warnings);
    }
}

internal sealed class ProduceLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ProduceLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(ProduceLotCommand command, CancellationToken cancellationToken)
    {
        var lotNumber = InventoryRules.Text(command.LotNumber);
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.JobId != Guid.Empty, nameof(command.JobId), "Choose a job.")
            .Require(command.LocationId != Guid.Empty, nameof(command.LocationId), "Required.")
            .Require(command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .RequireLotNumber(lotNumber, nameof(command.LotNumber))
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var job = await InventoryRules.JobAsync(inventory, command.JobId, null, cancellationToken);
        var location = await InventoryRules.LocationAsync(inventory, command.LocationId, job.SiteId, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, job.MaterialId!.Value, cancellationToken);
        await InventoryRules.EnsureAcceptsAsync(inventory, location, material.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var expiresAt = Lot.ExpiryFor(command.ExpiresAt, material.ShelfLifeDays, now);
        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);
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
                    [nameof(command.LotNumber)] = [$"Leave blank: {material.Code} isn't lot-tracked, so it is kept as one balance per location."],
                });
            }

            var (balance, isNew) = await Commingling.BalanceAsync(
                inventory, ids, job.SiteId, material.Id, material.BaseUomId, location.Id, LotOrigin.Produced, cancellationToken);
            existing = isNew ? null : new Versioned<Lot>(balance, Version: 0); // only whether it exists matters below
            lot = balance with { ExpiresAt = Commingling.Earliest(balance.ExpiresAt, expiresAt) };
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
                Entered = new { command.Quantity, command.UomId },
                Quantity = quantity,
                QuantityAfter = after.Quantity,
                ParentLotIds = edges.Select(e => e.ParentLotId),
                Comment = comment,
            });
        var version = existing is null
            ? await inventory.InsertLotAsync(after, envelope.OccurredAt, cancellationToken)
            : await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), after.Id, movement, after.UomId, envelope.ActorId, JobId: job.Id, Comment: comment),
            envelope.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(edges, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        if (location.ComminglesLots && !after.IsBulk)
        {
            var (balance, balanceVersion, note) = await Commingling.MergeArrivalAsync(inventory, eventLog, events, ids, after, location.Code, comment, now, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InventoryRules.Response(balance, balanceVersion, [note]);
        }

        await transaction.CommitAsync(cancellationToken);
        return InventoryRules.Response(after, version);
    }
}
