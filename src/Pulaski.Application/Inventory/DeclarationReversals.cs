using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Writes one compensating movement (D-063): the lot as it is after it, a <c>reversed</c> row naming the movement it undoes, and an
/// event with the reason and comment. Nothing recorded before is changed. A reversal counts when the movement it undoes did, so stock
/// at every time is as if that movement never happened; it must fit the lot's history from then on (never negative, not before a count).
/// </summary>
internal sealed class ReversalWriter(IInventoryRepository inventory, IEventLog eventLog, IEventFactory events, IIdGenerator ids, DateTimeOffset now)
{
    public async Task<long> WriteAsync(
        Lot before, Lot after, LotMovement movement, LotEventDetail original, InventoryReason reason, string comment, string unitCode,
        CancellationToken cancellationToken, Guid? relatedLotId = null, Guid? reportId = null)
    {
        await Backdating.EnsureFitsAsync(inventory, before, movement.QtyDelta ?? 0m, original.EffectiveAt, now, unitCode, cancellationToken);
        var envelope = events.Create(
            EventTypes.LotReversed, "lot", before.Id,
            new
            {
                before.LotNumber,
                ReversesEventId = original.Id,
                ReversedKind = original.Movement.Kind,
                Quantity = movement.QtyDelta,
                QuantityAfter = after.Quantity,
                after.Status,
                original.JobId,
                RelatedLotId = relatedLotId,
                ReportId = reportId,
                ReasonCodeId = reason.Id,
                Comment = comment,
            },
            reason.Code);
        var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(
                ids.NewId(), before.Id, movement, before.UomId, envelope.ActorId, JobId: original.JobId, ReasonCodeId: reason.Id, Comment: comment,
                RelatedLotId: relatedLotId, ReversesEventId: original.Id, ReversesOccurredAt: original.OccurredAt, RunId: original.RunId,
                EffectiveAt: MovementTime.Backdated(original.EffectiveAt, envelope.OccurredAt), ReportId: reportId),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return version;
    }

    /// <summary>What has happened to a lot since a movement, in words, for a message: "consumed into WO-1-10, moved".</summary>
    public static string Since(IReadOnlyList<LotEventDetail> later) =>
        string.Join(", ", later.Select(e => e.Movement.Kind.Replace('_', ' ') + (e.JobNumber is { } job ? $" into {job}" : "")).Distinct());
}

internal sealed class ReverseMovementHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<ReverseMovementCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(ReverseMovementCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.EventId != Guid.Empty, nameof(command.EventId), "Required.")
            .Require(command.ReasonCodeId != Guid.Empty, nameof(command.ReasonCodeId), "Choose a reason.")
            .Require(comment is not null, nameof(command.Comment), "Say why it is reversed.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);

        // Read the movement first, without a lock, to know what it touched and so what to lock, in the usual order (D-027).
        var original = await inventory.GetEventAsync(command.LotId, command.EventId, cancellationToken)
            ?? throw new NotFoundException($"That movement of lot {command.LotId} wasn't found.");
        var response = original.Movement.Kind switch
        {
            LotEventKind.Reversed => throw new DomainRuleException("That movement is a reversal already; it can't be reversed."),
            LotEventKind.Consumed => await ConsumptionAsync(command, original, comment!, cancellationToken),
            LotEventKind.Produced => await ProductionAsync(command, original, comment!, cancellationToken),
            LotEventKind.Split or LotEventKind.Merged => await PairAsync(command, original, comment!, cancellationToken),
            LotEventKind.Moved when original.RelatedLotId is not null => await PairAsync(command, original, comment!, cancellationToken),
            _ => await SingleAsync(command, original, comment!, cancellationToken),
        };
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    private ReversalWriter Writer(DateTimeOffset now) => new(inventory, eventLog, events, ids, now);

    private static void EnsureStanding(LotEventDetail movement)
    {
        if (movement.ReversedBy is not null)
        {
            throw new DomainRuleException("That movement was reversed already.");
        }
    }

    // Consumption into a job: the stock goes back to the lot, and the lot stops being a parent of what the job made once nothing of it
    // is consumed there any more.
    private async Task<LotTransactionResponse> ConsumptionAsync(ReverseMovementCommand command, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var job = await InventoryRules.JobAsync(inventory, original.JobId!.Value, null, cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var (after, movement) = lot.Restore(-original.Movement.QtyDelta!.Value);
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(lot, after, movement, original, reason, comment, material.BaseUomCode, cancellationToken);
        if (!await inventory.HasDeclarationAsync(lot.Id, job.Id, LotEventKind.Consumed, cancellationToken))
        {
            await inventory.SupersedeGenealogyAsync(lot.Id, null, GenealogyEdge.Declared, job.Id, timeProvider.GetUtcNow(), cancellationToken);
        }

        return InventoryRules.Response(after, version);
    }

    // A report's production is reversed with the report, which also offsets its counts; only production recorded before reports had ids
    // is reversed lot by lot.
    private async Task<LotTransactionResponse> ProductionAsync(ReverseMovementCommand command, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        if (original.ReportId is not null)
        {
            throw new DomainRuleException(
                $"That lot was made by a production report on {original.JobNumber}. Reverse the report on the job's page: it takes back every lot it made and its counts.");
        }

        var job = await InventoryRules.JobAsync(inventory, original.JobId!.Value, null, cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var (after, movement) = Take(lot, original.Movement.QtyDelta!.Value, lot.Origin == LotOrigin.Produced, material.BaseUomCode,
            await inventory.ListEventsAfterAsync(lot.Id, original, cancellationToken));
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(lot, after, movement, original, reason, comment, material.BaseUomCode, cancellationToken);
        if (!await inventory.HasDeclarationAsync(lot.Id, job.Id, LotEventKind.Produced, cancellationToken))
        {
            await inventory.SupersedeGenealogyAsync(null, lot.Id, GenealogyEdge.Declared, job.Id, timeProvider.GetUtcNow(), cancellationToken);
        }

        return InventoryRules.Response(after, version,
            [new("counts_unchanged", $"This production was recorded before reports could be reversed whole, so {job.JobNumber}'s reported output still counts it.")]);
    }

    // Split, merge or a transfer between balances: both lots, locked in id order, put back as they were.
    private async Task<LotTransactionResponse> PairAsync(ReverseMovementCommand command, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var other = await inventory.GetCounterpartAsync(original, cancellationToken)
            ?? throw new DomainRuleException("The other lot's half of that movement wasn't found; put it right with other movements.");
        var locked = (await inventory.LockLotsAsync([original.LotId, other.LotId], cancellationToken)).ToDictionary(l => l.Lot.Id);
        if (locked[command.LotId].Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{locked[command.LotId].Lot.Name} was changed by someone else. Reload and try again.");
        }

        // The half that gave stock and the half that got it.
        var (gaveRow, gotRow) = original.Movement.QtyDelta < 0 ? (original, other) : (other, original);
        EnsureStanding(await inventory.GetEventAsync(gaveRow.LotId, gaveRow.Id, cancellationToken) ?? gaveRow);
        EnsureStanding(await inventory.GetEventAsync(gotRow.LotId, gotRow.Id, cancellationToken) ?? gotRow);
        var gave = locked[gaveRow.LotId].Lot;
        var got = locked[gotRow.LotId].Lot;
        var quantity = gotRow.Movement.QtyDelta!.Value;
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, gave.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, gave.MaterialId, cancellationToken);

        bool cancelWhenEmpty;
        string? gaveStatus = null;
        switch (original.Movement.Kind)
        {
            case LotEventKind.Split:
                // The new lot goes back whole, so it must be just as it was made.
                if (await inventory.ListEventsAfterAsync(got.Id, gotRow, cancellationToken) is { Count: > 0 } later)
                {
                    throw new DomainRuleException($"{got.Name} has changed since the split ({ReversalWriter.Since(later)}); reverse that first.");
                }

                cancelWhenEmpty = true;
                break;
            case LotEventKind.Merged:
                if (got.IsBulk)
                {
                    throw new DomainRuleException(
                        $"{gave.Name} joined a balance where lots mix, so it can't be taken back out as itself; write the quantity off the balance instead.");
                }

                if (await inventory.ListEventsAfterAsync(gave.Id, gaveRow, cancellationToken) is { Count: > 0 } since)
                {
                    throw new DomainRuleException($"{gave.Name} has changed since the merge ({ReversalWriter.Since(since)}); reverse that first.");
                }

                cancelWhenEmpty = false;
                gaveStatus = gaveRow.Movement.FromStatus;
                break;
            default:
                cancelWhenEmpty = false;
                break;
        }

        var writer = Writer(timeProvider.GetUtcNow());
        var (taken, takeMovement) = Take(got, quantity, cancelWhenEmpty, material.BaseUomCode, await inventory.ListEventsAfterAsync(got.Id, gotRow, cancellationToken));
        var gotVersion = await writer.WriteAsync(got, taken, takeMovement, gotRow, reason, comment, material.BaseUomCode, cancellationToken, relatedLotId: gave.Id);
        var (restored, restoreMovement) = gave.Restore(quantity, gaveStatus);
        var gaveVersion = await writer.WriteAsync(gave, restored, restoreMovement, gaveRow, reason, comment, material.BaseUomCode, cancellationToken, relatedLotId: got.Id);

        var basis = original.Movement.Kind switch
        {
            LotEventKind.Split => GenealogyEdge.Split,
            LotEventKind.Merged => GenealogyEdge.Merge,
            _ => null,
        };
        if (basis is not null)
        {
            await inventory.SupersedeGenealogyAsync(gave.Id, got.Id, basis, null, timeProvider.GetUtcNow(), cancellationToken);
        }

        return command.LotId == gave.Id ? InventoryRules.Response(restored, gaveVersion) : InventoryRules.Response(taken, gotVersion);
    }

    // One lot: its latest movement as before (D-030), or an earlier change of quantity when the stock from then on allows it.
    private async Task<LotTransactionResponse> SingleAsync(ReverseMovementCommand command, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, command.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var latest = await inventory.GetLatestEventAsync(lot.Id, cancellationToken);
        var now = timeProvider.GetUtcNow();
        if (latest is not null && latest.Id == original.Id)
        {
            var (after, movement) = lot.Reverse(latest.Movement);
            var envelope = events.Create(
                EventTypes.LotReversed, "lot", lot.Id,
                new { lot.LotNumber, ReversesEventId = latest.Id, ReversedKind = latest.Movement.Kind, QuantityAfter = after.Quantity, after.Status, ReasonCodeId = reason.Id, Comment = comment },
                reason.Code);
            var version = await inventory.UpdateLotAsync(after, envelope.OccurredAt, cancellationToken);
            await inventory.InsertEventAsync(
                new LotEventRow(
                    ids.NewId(), lot.Id, movement, lot.UomId, envelope.ActorId, ReasonCodeId: reason.Id, Comment: comment,
                    ReversesEventId: latest.Id, ReversesOccurredAt: latest.OccurredAt,
                    // Undoing a back-dated movement counts when it did, so stock at every time is as if it never happened (D-057).
                    EffectiveAt: latest.EffectiveAt is { } effective && effective < latest.OccurredAt ? effective : null),
                envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            return InventoryRules.Response(after, version);
        }

        if (!LotEventKind.QuantityOnly.Contains(original.Movement.Kind))
        {
            throw new DomainRuleException(
                $"Only the latest movement of {lot.Name.ToLowerInvariant()} can be reversed when it is a {original.Movement.Kind.Replace('_', ' ')}; reverse the later ones first.");
        }

        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var delta = original.Movement.QtyDelta ?? 0m;
        var (earlierAfter, earlierMovement) = delta > 0
            ? Take(lot, delta, original.Movement.Kind == LotEventKind.Received && lot.Origin == LotOrigin.Received, material.BaseUomCode,
                await inventory.ListEventsAfterAsync(lot.Id, original, cancellationToken))
            : lot.Restore(-delta);
        var earlierVersion = await Writer(now).WriteAsync(lot, earlierAfter, earlierMovement, original, reason, comment, material.BaseUomCode, cancellationToken);
        return InventoryRules.Response(earlierAfter, earlierVersion);
    }

    // Takes stock back, saying what used it when the lot no longer holds it.
    private static (Lot Lot, LotMovement Movement) Take(Lot lot, decimal quantity, bool cancelWhenEmpty, string unitCode, IReadOnlyList<LotEventDetail> later)
    {
        try
        {
            return lot.TakeBack(quantity, cancelWhenEmpty, unitCode);
        }
        catch (DomainRuleException ex) when (later.Count > 0)
        {
            throw new DomainRuleException($"{ex.Message} Since then: {ReversalWriter.Since(later)}.");
        }
    }

    internal static (Lot Lot, LotMovement Movement) TakeBack(Lot lot, decimal quantity, bool cancelWhenEmpty, string unitCode, IReadOnlyList<LotEventDetail> later) =>
        Take(lot, quantity, cancelWhenEmpty, unitCode, later);
}
