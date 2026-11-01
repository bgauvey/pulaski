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
        CancellationToken cancellationToken, Guid? relatedLotId = null, Guid? reportId = null, Guid? containerId = null, string? details = null)
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
                EffectiveAt: MovementTime.Backdated(original.EffectiveAt, envelope.OccurredAt), ReportId: reportId, ContainerId: containerId, Details: details),
            envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return version;
    }

    /// <summary>What has happened to a lot since a movement, in words, for a message: "consumed into WO-1-10, moved".</summary>
    public static string Since(IReadOnlyList<LotEventDetail> later) =>
        string.Join(", ", later.Select(e => e.Movement.Kind.Replace('_', ' ') + (e.JobNumber is { } job ? $" into {job}" : "")).Distinct());
}

/// <summary>Which movement to reverse, of which lot, and for what reason.</summary>
internal sealed record ReverseRequest(Guid LotId, long? ExpectedVersion, Guid EventId, Guid ReasonCodeId);

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
        var response = await ReverseAsync(new ReverseRequest(command.LotId, command.ExpectedVersion, command.EventId, command.ReasonCodeId), comment!, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return response;
    }

    /// <summary>
    /// Reverses one movement inside the caller's transaction: a chain undo (#79) runs one after another, then commits or, for its preview,
    /// rolls everything back. A null expected version doesn't check the lot's.
    /// </summary>
    internal async Task<LotTransactionResponse> ReverseAsync(ReverseRequest request, string comment, CancellationToken cancellationToken)
    {
        // Read the movement first, without a lock, to know what it touched and so what to lock, in the usual order (D-027).
        var original = await inventory.GetEventAsync(request.LotId, request.EventId, cancellationToken)
            ?? throw new NotFoundException($"That movement of lot {request.LotId} wasn't found.");
        if (original.ReversesEventId is not null)
        {
            throw new DomainRuleException("That movement is a reversal already; it can't be reversed.");
        }

        return original.Movement.Kind switch
        {
            LotEventKind.Reversed => throw new DomainRuleException("That movement is a reversal already; it can't be reversed."),
            LotEventKind.Corrected => await CorrectionAsync(request, original, comment, cancellationToken),
            LotEventKind.Packed or LotEventKind.Unpacked => await PackingAsync(request, original, comment, cancellationToken),
            LotEventKind.Consumed => await ConsumptionAsync(request, original, comment, cancellationToken),
            LotEventKind.Produced => await ProductionAsync(request, original, comment, cancellationToken),
            LotEventKind.Split or LotEventKind.Merged => await PairAsync(request, original, comment, cancellationToken),
            LotEventKind.Moved when original.RelatedLotId is not null => await PairAsync(request, original, comment, cancellationToken),
            _ => await SingleAsync(request, original, comment, cancellationToken),
        };
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
    private async Task<LotTransactionResponse> ConsumptionAsync(ReverseRequest request, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var job = await InventoryRules.JobAsync(inventory, original.JobId!.Value, null, cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, request.LotId, request.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, request.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var (after, movement) = lot.Restore(-original.Movement.QtyDelta!.Value);
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(lot, after, movement, original, reason, comment, material.BaseUomCode, cancellationToken);
        if (!await inventory.HasDeclarationAsync(lot.Id, job.Id, LotEventKind.Consumed, cancellationToken))
        {
            await inventory.SupersedeGenealogyAsync(lot.Id, null, GenealogyEdge.Declared, job.Id, timeProvider.GetUtcNow(), cancellationToken);
            await inventory.SupersedeGenealogyAsync(lot.Id, null, GenealogyEdge.MadeFrom, job.Id, timeProvider.GetUtcNow(), cancellationToken);
        }

        return InventoryRules.Response(after, version);
    }

    // A report's production is reversed with the report, which also offsets its counts; only production recorded before reports had ids
    // is reversed lot by lot.
    private async Task<LotTransactionResponse> ProductionAsync(ReverseRequest request, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        if (original.ReportId is not null)
        {
            throw new DomainRuleException(
                $"That lot was made by a production report on {original.JobNumber}. Reverse the report on the job's page: it takes back every lot it made and its counts.");
        }

        var job = await InventoryRules.JobAsync(inventory, original.JobId!.Value, null, cancellationToken);
        var lot = await InventoryRules.LotAsync(inventory, request.LotId, request.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, request.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var (after, movement) = Take(lot, original.Movement.QtyDelta!.Value, lot.Origin == LotOrigin.Produced, material.BaseUomCode,
            await inventory.ListEventsAfterAsync(lot.Id, original, cancellationToken));
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(lot, after, movement, original, reason, comment, material.BaseUomCode, cancellationToken);
        if (!await inventory.HasDeclarationAsync(lot.Id, job.Id, LotEventKind.Produced, cancellationToken))
        {
            await inventory.SupersedeGenealogyAsync(null, lot.Id, GenealogyEdge.Declared, job.Id, timeProvider.GetUtcNow(), cancellationToken);
            await inventory.SupersedeGenealogyAsync(null, lot.Id, GenealogyEdge.MadeFrom, job.Id, timeProvider.GetUtcNow(), cancellationToken);
        }

        return InventoryRules.Response(after, version,
            [new("counts_unchanged", $"This production was recorded before reports could be reversed whole, so {job.JobNumber}'s reported output still counts it.")]);
    }

    // Split, merge or a transfer between balances: both lots, locked in id order, put back as they were.
    private async Task<LotTransactionResponse> PairAsync(ReverseRequest request, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var other = await inventory.GetCounterpartAsync(original, cancellationToken)
            ?? throw new DomainRuleException("The other lot's half of that movement wasn't found; put it right with other movements.");
        var locked = (await inventory.LockLotsAsync([original.LotId, other.LotId], cancellationToken)).ToDictionary(l => l.Lot.Id);
        if (request.ExpectedVersion is { } expected && locked[request.LotId].Version != expected)
        {
            throw new ConflictException($"{locked[request.LotId].Lot.Name} was changed by someone else. Reload and try again.");
        }

        // The half that gave stock and the half that got it.
        var (gaveRow, gotRow) = original.Movement.QtyDelta < 0 ? (original, other) : (other, original);
        EnsureStanding(await inventory.GetEventAsync(gaveRow.LotId, gaveRow.Id, cancellationToken) ?? gaveRow);
        EnsureStanding(await inventory.GetEventAsync(gotRow.LotId, gotRow.Id, cancellationToken) ?? gotRow);
        var gave = locked[gaveRow.LotId].Lot;
        var got = locked[gotRow.LotId].Lot;
        var quantity = gotRow.Movement.QtyDelta!.Value;
        var reason = await InventoryRules.ReasonAsync(inventory, request.ReasonCodeId, ReasonKinds.InventoryAdjustment, gave.SiteId, comment, cancellationToken);
        var material = await InventoryRules.MaterialAsync(inventory, gave.MaterialId, cancellationToken);

        bool cancelWhenEmpty;
        string? gaveStatus = null;
        switch (original.Movement.Kind)
        {
            case LotEventKind.Split:
                // The new lot goes back whole, so it must be just as it was made (anything since already reversed doesn't count).
                if (await inventory.ListStandingAfterAsync(got.Id, gotRow, cancellationToken) is { Count: > 0 } later)
                {
                    throw new DomainRuleException($"{got.Name} has changed since the split ({ReversalWriter.Since(later)}); reverse that first.");
                }

                cancelWhenEmpty = true;
                break;
            case LotEventKind.Merged:
                // A lot that joined a balance where lots mix comes back out as itself (#79): the balance gives its quantity back, if it still
                // has it at every time since.
                if (await inventory.ListStandingAfterAsync(gave.Id, gaveRow, cancellationToken) is { Count: > 0 } since)
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

        return request.LotId == gave.Id ? InventoryRules.Response(restored, gaveVersion) : InventoryRules.Response(taken, gotVersion);
    }

    // One lot (D-030, #79): any receipt, write-up, write-off, move, hold, release or shipment, when what it changed hasn't changed since. A
    // move must be the lot's latest move and a status change its latest status change; any quantity it put back must fit the lot's history.
    private async Task<LotTransactionResponse> SingleAsync(ReverseRequest request, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var lot = await InventoryRules.LotAsync(inventory, request.LotId, request.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, request.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        if (!LotEventKind.Reversible.Contains(original.Movement.Kind))
        {
            throw new DomainRuleException($"A {original.Movement.Kind.Replace('_', ' ')} movement can't be reversed; put it right with another movement.");
        }

        if (original.Movement.Kind == LotEventKind.Moved && original.ContainerId is not null)
        {
            throw new DomainRuleException($"{lot.Name} moved with its container; move the container back instead, so its lots stay together.");
        }

        var later = await inventory.ListStandingAfterAsync(lot.Id, original, cancellationToken);
        if (Relocates(original.Movement) && later.Where(e => Relocates(e.Movement)).ToList() is [_, ..] moves)
        {
            throw new DomainRuleException($"{lot.Name} has moved since ({ReversalWriter.Since(moves)}); reverse that first.");
        }

        if (ChangesStatus(original.Movement) && later.Where(e => ChangesStatus(e.Movement)).ToList() is [_, ..] changes)
        {
            throw new DomainRuleException($"{lot.Name}'s status has changed since ({ReversalWriter.Since(changes)}); reverse that first.");
        }

        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        (Lot, LotMovement) reversed;
        try
        {
            reversed = lot.Reverse(original.Movement);
        }
        catch (DomainRuleException ex) when (later.Count > 0)
        {
            throw new DomainRuleException($"{ex.Message} Since then: {ReversalWriter.Since(later)}.");
        }

        var (after, movement) = reversed;
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(lot, after, movement, original, reason, comment, material.BaseUomCode, cancellationToken);
        return InventoryRules.Response(after, version);
    }

    // Packing or unpacking (#79): the lot comes out of the container it was packed into (back into any it was in before, and back to where it
    // was if packing moved it), or goes back into the one it was unpacked from, which must still be where the lot is.
    private async Task<LotTransactionResponse> PackingAsync(ReverseRequest request, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var lot = await InventoryRules.LotAsync(inventory, request.LotId, request.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, request.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var container = original.ContainerId ?? throw new DomainRuleException("That movement doesn't name its container, so it can't be put back.");
        if ((await inventory.ListStandingAfterAsync(lot.Id, original, cancellationToken))
            .Where(e => Relocates(e.Movement) || e.Movement.Kind is LotEventKind.Packed or LotEventKind.Unpacked).ToList() is [_, ..] since)
        {
            throw new DomainRuleException($"{lot.Name} has moved or changed container since ({ReversalWriter.Since(since)}); reverse that first.");
        }

        // The container it was in just before a pack, from its history.
        var events = await inventory.ListPastEventsAsync(lot.Id, cancellationToken);
        var before = LotPastState.At(LotNow.Of(lot), events.Where(e => e.Id != original.Id), original.EffectiveAt).ContainerId;
        var location = original.Movement.Kind == LotEventKind.Unpacked ? await inventory.GetContainerLocationAsync(container, cancellationToken) : null;
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var (after, movement, rowContainer) = lot.ReversePacking(original.Movement, container, before, location);
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(
            lot, after, movement, original, reason, comment, material.BaseUomCode, cancellationToken, containerId: rowContainer);
        return InventoryRules.Response(after, version);
    }

    // A correction of the lot's details (#79): they go back to what it replaced, as long as nothing has corrected them since.
    private async Task<LotTransactionResponse> CorrectionAsync(ReverseRequest request, LotEventDetail original, string comment, CancellationToken cancellationToken)
    {
        var lot = await InventoryRules.LotAsync(inventory, request.LotId, request.ExpectedVersion, cancellationToken);
        EnsureStanding(await inventory.GetEventAsync(lot.Id, original.Id, cancellationToken) ?? original);
        var reason = await InventoryRules.ReasonAsync(inventory, request.ReasonCodeId, ReasonKinds.InventoryAdjustment, lot.SiteId, comment, cancellationToken);
        var (before, after) = CorrectionDetails.Read(original.Details)
            ?? throw new DomainRuleException("That correction was made before Pulaski kept what it replaced, so it can't be reversed; correct the lot again instead.");
        if (before.LotNumber is { } number && number != lot.LotNumber
            && await inventory.FindLotForUpdateAsync(lot.SiteId, lot.MaterialId, number, cancellationToken) is not null)
        {
            throw new ConflictException($"Lot {number} of this material exists again in this site, so the old number can't come back.");
        }

        var (reverted, movement) = lot.RevertCorrection(before, after);
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var version = await Writer(timeProvider.GetUtcNow()).WriteAsync(
            lot, reverted, movement, original, reason, comment, material.BaseUomCode, cancellationToken, details: CorrectionDetails.Write(after, before));
        return InventoryRules.Response(reverted, version);
    }

    private static bool Relocates(LotMovement m) => m.QtyDelta is null && m.FromLocationId is not null && m.ToLocationId is not null;

    private static bool ChangesStatus(LotMovement m) => m.FromStatus is not null && m.ToStatus is not null;

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
