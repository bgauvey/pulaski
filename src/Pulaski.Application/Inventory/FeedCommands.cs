using Microsoft.Extensions.Logging;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Loads a lot at a feed point (#33): moves the whole lot there if it is elsewhere, else records it loaded again, so it feeds from now.</summary>
public sealed record LoadLotCommand(Guid LotId, long ExpectedVersion, Guid FeedPointId, string? Comment = null) : ICommand<LotTransactionResponse>;

/// <summary>Resolves a shortfall by consuming its quantity from a lot of its material into its job and run, now.</summary>
public sealed record ResolveShortfallCommand(Guid Id, long ExpectedVersion, Guid LotId, string? Comment) : ICommand<VersionResponse>;

/// <summary>Dismisses a shortfall, saying why: nothing is consumed.</summary>
public sealed record DismissShortfallCommand(Guid Id, long ExpectedVersion, string Comment) : ICommand<VersionResponse>;

/// <summary>A site's feed points with what is loaded at each now.</summary>
public sealed record ListFeedPointsQuery(Guid? SiteId) : IQuery<IReadOnlyList<FeedPointDto>>;

/// <summary>A site's shortfalls in a status (<c>open</c> by default).</summary>
public sealed record ListShortfallsQuery(Guid? SiteId, string Status = FeedShortfallStatus.Open) : IQuery<IReadOnlyList<FeedShortfallDto>>;

internal sealed class LoadLotHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider) : ICommandHandler<LoadLotCommand, LotTransactionResponse>
{
    public async Task<LotTransactionResponse> HandleAsync(LoadLotCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.FeedPointId != Guid.Empty, nameof(command.FeedPointId), "Choose a feed point.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        bool elsewhere;
        await using (var transaction = await unitOfWork.BeginAsync(cancellationToken))
        {
            var lot = await InventoryRules.LotAsync(inventory, command.LotId, command.ExpectedVersion, cancellationToken);
            var feedPoint = await InventoryRules.LocationAsync(inventory, command.FeedPointId, lot.SiteId, cancellationToken);
            if (!feedPoint.IsFeedPoint)
            {
                throw new DomainRuleException($"{feedPoint.Code} isn't a feed point; lots are loaded where they enter a line.");
            }

            elsewhere = lot.LocationId != feedPoint.Id;
            if (!elsewhere)
            {
                if (!LotStatus.IsOnHand(lot.Status) || lot.Quantity <= 0)
                {
                    throw new DomainRuleException($"{lot.Name} has nothing on hand to load.");
                }

                // Already there: it feeds from now, ending the load of the lot of its material before it.
                var envelope = events.Create(EventTypes.LotLoaded, "lot", lot.Id, new { lot.LotNumber, FeedPointId = feedPoint.Id, lot.Quantity, Comment = comment });
                var version = await inventory.UpdateLotAsync(lot, envelope.OccurredAt, cancellationToken);
                await inventory.InsertEventAsync(
                    new LotEventRow(ids.NewId(), lot.Id, new LotMovement(LotEventKind.Loaded, null, lot.Quantity, ToLocationId: feedPoint.Id), lot.UomId, envelope.ActorId, Comment: comment),
                    envelope.OccurredAt, cancellationToken);
                await eventLog.AppendAsync(envelope, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return InventoryRules.Response(lot, version);
            }
        }

        // Elsewhere: moving it there loads it (a balance moves by quantity, so it is moved instead).
        return await new MoveLotHandler(unitOfWork, inventory, units, eventLog, events, ids, timeProvider).HandleAsync(
            new MoveLotCommand(command.LotId, command.ExpectedVersion, command.FeedPointId, Comment: comment), cancellationToken);
    }
}

internal sealed class ResolveShortfallHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IFeedRepository feeds,
    IFeedReadStore feedReads,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IAllocationRepository? allocations = null) : ICommandHandler<ResolveShortfallCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(ResolveShortfallCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(command.LotId != Guid.Empty, nameof(command.LotId), "Choose a lot.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        // Its job never changes, so it is read before the locks: the job, then the lot (D-027), then the shortfall.
        var found = await feedReads.GetShortfallAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Shortfall {command.Id} not found.");
        if (found.JobId is not { } jobId)
        {
            throw new DomainRuleException("Nothing was running when it fell short, so there is no job to consume into; dismiss it instead.");
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var job = await inventory.GetJobForUpdateAsync(jobId, cancellationToken) ?? throw new NotFoundException($"Job {jobId} not found.");
        var lot = await InventoryRules.LotAsync(inventory, command.LotId, null, cancellationToken);
        var current = await Shortfalls.OpenAsync(feeds, command.Id, command.ExpectedVersion, cancellationToken);
        var shortfall = current.Value;
        if (shortfall.MaterialId is { } materialId && lot.MaterialId != materialId)
        {
            throw new DomainRuleException($"{lot.Name} is another material than the one that fell short.");
        }

        if (lot.SiteId != shortfall.SiteId)
        {
            throw new DomainRuleException($"{lot.Name} is in another site.");
        }

        var now = timeProvider.GetUtcNow();
        InventoryRules.EnsureNotExpired(lot, now, "consumed");
        var material = await InventoryRules.MaterialAsync(inventory, lot.MaterialId, cancellationToken);
        var quantity = await InventoryRules.ToBaseAsync(units, material, shortfall.Quantity, shortfall.UomId ?? material.BaseUomId, cancellationToken);
        var (after, movement) = lot.Take(quantity, LotEventKind.Consumed, material.BaseUomCode);
        var edges = InventoryRules.Edges(ids, [lot.Id], await inventory.ListLotsProducedByJobAsync(job.Id, cancellationToken), job.Id);

        var consumed = events.Create(
            EventTypes.LotConsumed, "lot", lot.Id,
            new { lot.LotNumber, JobId = job.Id, job.JobNumber, Quantity = quantity, QuantityAfter = after.Quantity, ChildLotIds = edges.Select(e => e.ChildLotId), shortfall.RunId, ShortfallId = shortfall.Id, Comment = comment });

        // Like any consumption (D-064, #82): uses up the job's own allocation; under hard reservation, another job's is refused.
        if (allocations is not null)
        {
            await AllocationUse.ConsumeAsync(allocations, lot, job.Id, quantity, consumed.ActorId, consumed.OccurredAt, material.BaseUomCode, cancellationToken);
        }

        await inventory.UpdateLotAsync(after, consumed.OccurredAt, cancellationToken);
        await inventory.InsertEventAsync(
            new LotEventRow(ids.NewId(), lot.Id, movement, lot.UomId, consumed.ActorId, JobId: job.Id, Comment: comment, RunId: shortfall.RunId, SourceRef: shortfall.Id),
            consumed.OccurredAt, cancellationToken);
        await inventory.InsertGenealogyAsync(edges, consumed.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(consumed, cancellationToken);

        var resolved = shortfall with
        {
            Status = FeedShortfallStatus.Resolved,
            ResolvedLotId = lot.Id,
            ResolvedBy = consumed.ActorId,
            ResolvedAt = now,
            ResolutionNote = comment,
        };
        var envelope = events.Create(EventTypes.FeedShortfallResolved, "feed_shortfall", shortfall.Id, new { LotId = lot.Id, Quantity = quantity, Comment = comment });
        var version = await feeds.UpdateShortfallAsync(resolved, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class DismissShortfallHandler(
    IUnitOfWork unitOfWork, IFeedRepository feeds, IEventLog eventLog, IEventFactory events, TimeProvider timeProvider) : ICommandHandler<DismissShortfallCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(DismissShortfallCommand command, CancellationToken cancellationToken)
    {
        var comment = InventoryRules.Text(command.Comment);
        new Validator()
            .Require(comment is not null, nameof(command.Comment), "Say why it is dismissed.")
            .RequireComment(command.Comment)
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await Shortfalls.OpenAsync(feeds, command.Id, command.ExpectedVersion, cancellationToken);
        var envelope = events.Create(EventTypes.FeedShortfallDismissed, "feed_shortfall", command.Id, new { Comment = comment });
        var version = await feeds.UpdateShortfallAsync(Shortfalls.Dismiss(current.Value, envelope.ActorId, timeProvider.GetUtcNow(), comment!), envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal static class Shortfalls
{
    public static async Task<Versioned<FeedShortfall>> OpenAsync(IFeedRepository feeds, Guid id, long expectedVersion, CancellationToken cancellationToken)
    {
        var current = await feeds.GetShortfallForUpdateAsync(id, cancellationToken) ?? throw new NotFoundException($"Shortfall {id} not found.");
        if (current.Version != expectedVersion)
        {
            throw new ConflictException("That shortfall was changed by someone else. Reload and try again.");
        }

        return current.Value.Status == FeedShortfallStatus.Open
            ? current
            : throw new DomainRuleException($"That shortfall was {current.Value.Status} already.");
    }

    public static FeedShortfall Dismiss(FeedShortfall shortfall, string actorId, DateTimeOffset at, string note) =>
        shortfall with { Status = FeedShortfallStatus.Dismissed, ResolvedBy = actorId, ResolvedAt = at, ResolutionNote = note };
}

/// <summary>
/// Posts a feed point's machine counts (#33) after ingest has committed them: each in its own transaction, the job locked first, so ingest's
/// own locks (edge node, then equipment) never wait on lots. Counts left unposted (a failure, a crash) are picked up by the next ingest.
/// </summary>
internal sealed class FeedCounterPoster(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    IUnitRepository units,
    IFeedRepository feeds,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    ILogger<FeedCounterPoster>? logger = null,
    IAllocationRepository? allocations = null)
{
    public const int Batch = 500;

    public async Task<int> PostAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0)
        {
            return 0;
        }

        var now = timeProvider.GetUtcNow();
        IReadOnlyList<CounterRow> counts;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            counts = await feeds.ListUnpostedCountsAsync(equipmentIds, now.AddDays(-Backdating.MaxDays), Batch, cancellationToken);
        }

        var posted = 0;
        var automation = new FeedAutomation(inventory, units, feeds, eventLog, events, ids, allocations);
        foreach (var count in counts)
        {
            try
            {
                await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
                if (await automation.PostCountAsync(count, now, cancellationToken))
                {
                    posted++;
                }

                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is DomainRuleException or ConflictException or NotFoundException)
            {
                logger?.LogWarning(ex, "Machine count {CountId} at feed point {FeedPointId} wasn't posted; the next ingest tries again.", count.Id, count.FeedPointId);
            }
        }

        return posted;
    }
}

internal sealed class ListFeedPointsHandler(IFeedReadStore store) : IQueryHandler<ListFeedPointsQuery, IReadOnlyList<FeedPointDto>>
{
    public async Task<IReadOnlyList<FeedPointDto>> HandleAsync(ListFeedPointsQuery query, CancellationToken cancellationToken)
    {
        var points = await store.ListFeedPointsAsync(query.SiteId, cancellationToken);
        if (points.Count == 0)
        {
            return points;
        }

        var events = await store.ListFeedEventsAsync([.. points.Select(p => p.Id)], cancellationToken);
        var loads = points.ToDictionary(
            p => p.Id,
            p => FeedLoads.From(p.Id, events.Where(e => e.FromLocationId == p.Id || e.ToLocationId == p.Id)).Where(l => l.EndedAt is null).ToList());
        var lots = (await store.GetLotsAsync([.. loads.Values.SelectMany(l => l).Select(l => l.LotId).Distinct()], cancellationToken)).ToDictionary(l => l.Id);
        return [.. points.Select(p => p with
        {
            Loads = [.. loads[p.Id].Where(l => lots.ContainsKey(l.LotId)).OrderByDescending(l => l.LoadedAt).Select(l => lots[l.LotId] is var lot ? new FeedLoadDto
            {
                LotId = lot.Id,
                LotNumber = lot.LotNumber,
                IsBulk = lot.IsBulk,
                LocationCode = lot.LocationCode,
                MaterialCode = lot.MaterialCode,
                Quantity = lot.Quantity,
                UomCode = lot.UomCode,
                Status = lot.Status,
                LoadedAt = l.LoadedAt,
            } : null!)],
        })];
    }
}

internal sealed class ListShortfallsHandler(IFeedReadStore store) : IQueryHandler<ListShortfallsQuery, IReadOnlyList<FeedShortfallDto>>
{
    public Task<IReadOnlyList<FeedShortfallDto>> HandleAsync(ListShortfallsQuery query, CancellationToken cancellationToken)
    {
        new Validator().Require(FeedShortfallStatus.All.Contains(query.Status), nameof(query.Status), "open, resolved or dismissed.").ThrowIfInvalid();
        return store.ListShortfallsAsync(query.SiteId, query.Status, cancellationToken);
    }
}
