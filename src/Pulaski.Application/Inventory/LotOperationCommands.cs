using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Splits part of a lot into a new lot (D-030); returns the new lot.</summary>
public sealed record SplitLotCommand(
    Guid LotId, long ExpectedVersion, decimal Quantity, Guid UomId, string? NewLotNumber = null, Guid? ToLocationId = null, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Merges the source lots into the target lot (D-030); returns the target.</summary>
public sealed record MergeLotsCommand(Guid TargetLotId, long ExpectedVersion, IReadOnlyList<Guid> SourceLotIds, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Ships quantity of an available lot out of the site; a null quantity ships it all.</summary>
public sealed record ShipLotCommand(
    Guid LotId, long ExpectedVersion, decimal? Quantity, Guid? UomId, string Reference, string? Comment = null)
    : ICommand<LotTransactionResponse>;

/// <summary>Corrects a lot's number, supplier lot and expiry, saying why.</summary>
public sealed record CorrectLotCommand(
    Guid LotId, long ExpectedVersion, string? LotNumber, string? SupplierLot, DateTimeOffset? ExpiresAt, string Comment)
    : ICommand<LotTransactionResponse>;

/// <summary>
/// Reverses a movement of the lot, for an adjustment reason and a comment (D-030, D-063): its latest movement, an earlier change of
/// quantity when the stock allows, or a declaration (consumption, split, merge, transfer) with every lot it touched.
/// </summary>
public sealed record ReverseMovementCommand(Guid LotId, long ExpectedVersion, Guid EventId, Guid ReasonCodeId, string? Comment = null)
    : ICommand<LotTransactionResponse>;

internal sealed class SplitLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<SplitLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(SplitLotCommand command, CancellationToken cancellationToken)
    {
        var number = InventoryRules.Text(command.NewLotNumber);
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero.")
            .Require(command.UomId != Guid.Empty, nameof(command.UomId), "Required.")
            .RequireLotNumber(number, nameof(command.NewLotNumber))
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        var destination = command.ToLocationId is { } to ? await InventoryRules.LocationAsync(inventory, to, lot.SiteId, cancellationToken) : null;
        if (destination is not null)
        {
            await InventoryRules.EnsureAcceptsAsync(inventory, destination, lot.MaterialId, cancellationToken);
        }

        var locationId = destination?.Id
            ?? lot.LocationId ?? throw new DomainRuleException($"{lot.Name} has no location; choose where the new lot goes.");
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, command.Quantity, command.UomId, cancellationToken);

        if (number is not null)
        {
            if (await inventory.FindLotForUpdateAsync(lot.SiteId, lot.MaterialId, number, cancellationToken) is not null)
            {
                throw new ConflictException($"Lot {number} of {material.Code} already exists in this site. Use another number.");
            }
        }
        else
        {
            var n = 0;
            do
            {
                number = $"{lot.LotNumber}-{++n}";
            }
            while (await inventory.FindLotForUpdateAsync(lot.SiteId, lot.MaterialId, number, cancellationToken) is not null);
        }

        var (source, outgoing, part, incoming) = lot.Split(quantity, ids.NewId(), number, locationId, material.BaseUomCode);
        var sent = events.Create(
            EventTypes.LotSplit, "lot", source.Id,
            new { source.LotNumber, NewLotId = part.Id, NewLotNumber = part.LotNumber, Quantity = quantity, QuantityAfter = source.Quantity, ToLocationId = locationId, Comment = comment });
        var made = events.Create(
            EventTypes.LotSplit, "lot", part.Id,
            new { part.LotNumber, FromLotId = source.Id, FromLotNumber = source.LotNumber, Quantity = quantity, LocationId = locationId, Comment = comment });

        await inventory.UpdateLotAsync(source, sent.OccurredAt, cancellationToken);
        var version = await inventory.InsertLotAsync(part, made.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), source.Id, outgoing, lot.UomId, sent.ActorId, Comment: comment, RelatedLotId: part.Id), sent.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), part.Id, incoming, lot.UomId, made.ActorId, Comment: comment, RelatedLotId: source.Id), made.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync([new GenealogyEdge(ids.NewId(), source.Id, part.Id, null, GenealogyEdge.Split)], made.OccurredAt, cancellationToken);

        // A part of the lot is the same material: its certificates and attributes go with it (D-056).
        await inventory.CarryLotRecordsAsync(source.Id, part.Id, "split", withAttributes: true, made.ActorId, made.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(sent, cancellationToken);
        await eventLog.AppendAsync(made, cancellationToken);

        // Split off to where lots mix, the new lot joins the balance there (D-031).
        if (destination is { ComminglesLots: true })
        {
            var (balance, balanceVersion, note) = await Commingling.MergeArrivalAsync(
                inventory, eventLog, events, ids, part, destination.Code, comment, timeProvider.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return InventoryRules.Response(balance, balanceVersion, [note]);
        }

        await transaction.CommitAsync(cancellationToken);
        return InventoryRules.Response(part, version);
    }
}

internal sealed class MergeLotsHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<MergeLotsCommand, LotTransactionResponse>
{
    private const int MaxSources = 50;

    public async Task<LotTransactionResponse> HandleAsync(MergeLotsCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        var sources = command.SourceLotIds.Distinct().ToList();
        new Validator()
            .Require(sources.Count > 0, nameof(command.SourceLotIds), "Choose at least one lot to merge in.")
            .Require(sources.Count <= MaxSources, nameof(command.SourceLotIds), $"At most {MaxSources} lots at a time.")
            .Require(!sources.Contains(command.TargetLotId), nameof(command.SourceLotIds), "A lot can't be merged into itself.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // Every lot, in id order, so two merges over the same lots can't deadlock.
        var locked = (await inventory.LockLotsAsync([command.TargetLotId, .. sources], cancellationToken)).ToDictionary(l => l.Lot.Id);
        if (!locked.TryGetValue(command.TargetLotId, out var targetRow))
        {
            throw new NotFoundException($"Lot {command.TargetLotId} not found.");
        }

        if (targetRow.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{targetRow.Lot.Name} was changed by someone else. Reload and try again.");
        }

        if (sources.Where(id => !locked.ContainsKey(id)).ToList() is [var missing, ..])
        {
            throw new DomainRuleException($"Lot {missing} not found.");
        }

        var target = targetRow.Lot;
        var now = timeProvider.GetUtcNow();
        InventoryRules.EnsureNotExpired(target, now, "merged into");
        foreach (var id in sources)
        {
            InventoryRules.EnsureNotExpired(locked[id].Lot, now, "merged");
        }
        var merged = new List<(Lot Source, LotMovement Out, LotMovement In)>();
        foreach (var source in sources.Select(id => locked[id].Lot).OrderBy(l => l.LotNumber, StringComparer.Ordinal))
        {
            var (next, incoming, emptied, outgoing) = target.Absorb(source);
            target = next;
            merged.Add((emptied, outgoing, incoming));
        }

        foreach (var (source, outgoing, incoming) in merged)
        {
            var envelope = events.Create(
                EventTypes.LotMerged, "lot", source.Id,
                new { source.LotNumber, IntoLotId = target.Id, IntoLotNumber = target.LotNumber, Quantity = -outgoing.QtyDelta!.Value, Comment = comment });
            await inventory.UpdateLotAsync(source, envelope.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(ids.NewId(), source.Id, outgoing, source.UomId, envelope.ActorId, Comment: comment, RelatedLotId: target.Id), envelope.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(ids.NewId(), target.Id, incoming, target.UomId, envelope.ActorId, Comment: comment, RelatedLotId: source.Id), envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
        }

        var summary = events.Create(
            EventTypes.LotMerged, "lot", target.Id,
            new { target.LotNumber, FromLotIds = merged.Select(m => m.Source.Id), QuantityAfter = target.Quantity, target.ExpiresAt, Comment = comment });
        var version = await inventory.UpdateLotAsync(target, summary.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(
            [.. merged.Select(m => new GenealogyEdge(ids.NewId(), m.Source.Id, target.Id, null, GenealogyEdge.Merge))], summary.OccurredAt, cancellationToken);

        // The merged lot now holds what each lot was, so it carries their certificates; its own attributes stay (D-056).
        foreach (var (source, _, _) in merged)
        {
            await inventory.CarryLotRecordsAsync(source.Id, target.Id, "merge", withAttributes: false, summary.ActorId, summary.OccurredAt, cancellationToken);
        }
        await eventLog.AppendAsync(summary, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(target, version);
    }
}

internal sealed class ShipLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ShipLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(ShipLotCommand command, CancellationToken cancellationToken)
    {
        var reference = InventoryRules.Text(command.Reference);
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.Quantity is null || command.Quantity > 0, nameof(command.Quantity), "Must be greater than zero, or blank for all of it.")
            .Require(command.Quantity is null || command.UomId is { } u && u != Guid.Empty, nameof(command.UomId), "Required with a quantity.")
            .RequireText(reference, nameof(command.Reference), InventoryRules.MaxReference)
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        InventoryRules.EnsureNotExpired(lot, timeProvider.GetUtcNow(), "shipped");
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = command.Quantity is { } entered
            ? await InventoryRules.ToBaseAsync(units, material, entered, command.UomId!.Value, cancellationToken)
            : lot.Quantity;
        if (quantity == 0)
        {
            throw new DomainRuleException($"{lot.Name} is empty; there is nothing to ship.");
        }

        var (after, movement) = lot.Take(quantity, LotEventKind.Shipped, material.BaseUomCode);
        var envelope = events.Create(
            EventTypes.LotShipped, "lot", lot.Id,
            new { lot.LotNumber, Reference = reference, Quantity = quantity, QuantityAfter = after.Quantity, LocationId = lot.LocationId, Comment = comment });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, Comment: comment, Reference: reference), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version);
    }
}

internal sealed class CorrectLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CorrectLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(CorrectLotCommand command, CancellationToken cancellationToken)
    {
        var number = InventoryRules.Text(command.LotNumber);
        var supplierLot = InventoryRules.Text(command.SupplierLot);
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .RequireLotNumber(number, nameof(command.LotNumber))
            .Require(supplierLot is null || supplierLot.Length <= InventoryRules.MaxLotNumber, nameof(command.SupplierLot), $"At most {InventoryRules.MaxLotNumber} characters.")
            .RequireText(comment, nameof(command.Comment), InventoryRules.MaxComment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        if (number is not null && number != lot.LotNumber
            && await inventory.FindLotForUpdateAsync(lot.SiteId, lot.MaterialId, number, cancellationToken) is not null)
        {
            throw new ConflictException($"Lot {number} of this material already exists in this site.");
        }

        var (after, movement) = lot.Correct(number, supplierLot, command.ExpiresAt);
        var envelope = events.Create(
            EventTypes.LotCorrected, "lot", lot.Id,
            new
            {
                Before = new { lot.LotNumber, lot.SupplierLot, lot.ExpiresAt },
                After = new { after.LotNumber, after.SupplierLot, after.ExpiresAt },
                Comment = comment,
            });
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, Comment: comment), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return InventoryRules.Response(after, version);
    }
}
