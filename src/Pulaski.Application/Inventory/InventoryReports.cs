using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

public sealed record ListStockCountsQuery(Guid SiteId) : IQuery<IReadOnlyList<StockCountSummaryDto>>;

public sealed record GetStockCountQuery(Guid Id) : IQuery<StockCountDto>;

/// <summary>Every reason, for the reason-code screen (D-030).</summary>
public sealed record ListAllReasonCodesQuery(Guid? SiteId = null) : IQuery<IReadOnlyList<ReasonCodeAdminDto>>;

/// <summary>
/// Each material's on-hand quantity at <see cref="At"/>, which must be in the past (D-030): by location and with what was on hold then, each
/// lot's state worked back from now in the order movements count (#77, D-076), so it agrees with the journal.
/// </summary>
public sealed record StockOnDateQuery(Guid SiteId, DateTimeOffset At) : IQuery<IReadOnlyList<StockOnDateDto>>;

/// <summary>A lot's quantity, location, container and status at <see cref="At"/> (#77, D-076).</summary>
public sealed record GetLotStateAtQuery(Guid LotId, DateTimeOffset At) : IQuery<LotStateAtDto>;

/// <summary>Each material's on-hand stock by the age of its lots (D-030).</summary>
public sealed record AgingQuery(Guid SiteId) : IQuery<IReadOnlyList<AgingRowDto>>;

internal sealed class ListStockCountsHandler(IInventoryReadStore store) : IQueryHandler<ListStockCountsQuery, IReadOnlyList<StockCountSummaryDto>>
{
    public Task<IReadOnlyList<StockCountSummaryDto>> HandleAsync(ListStockCountsQuery query, CancellationToken cancellationToken) =>
        store.ListStockCountsAsync(query.SiteId, cancellationToken);
}

internal sealed class GetStockCountHandler(IInventoryReadStore store) : IQueryHandler<GetStockCountQuery, StockCountDto>
{
    public async Task<StockCountDto> HandleAsync(GetStockCountQuery query, CancellationToken cancellationToken) =>
        await store.GetStockCountAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Count {query.Id} not found.");
}

internal sealed class ListAllReasonCodesHandler(IInventoryReadStore store) : IQueryHandler<ListAllReasonCodesQuery, IReadOnlyList<ReasonCodeAdminDto>>
{
    public Task<IReadOnlyList<ReasonCodeAdminDto>> HandleAsync(ListAllReasonCodesQuery query, CancellationToken cancellationToken) =>
        store.ListAllReasonCodesAsync(query.SiteId, cancellationToken);
}

internal sealed class StockOnDateHandler(IInventoryReadStore store, TimeProvider time) : IQueryHandler<StockOnDateQuery, IReadOnlyList<StockOnDateDto>>
{
    public async Task<IReadOnlyList<StockOnDateDto>> HandleAsync(StockOnDateQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.SiteId != Guid.Empty, nameof(query.SiteId), "Required.")
            .Require(query.At <= time.GetUtcNow(), nameof(query.At), "Must be in the past; today's stock is on the Stock tab.")
            .ThrowIfInvalid();

        // Each lot as it was then; lots at zero, or used up, don't count.
        var lots = (await store.ListPastLotsAsync(query.SiteId, query.At, cancellationToken))
            .Select(l => (Lot: l, State: LotPastState.At(l.Now, l.EventsAfter, query.At, existed: true)))
            .Where(x => x.State.Existed && x.State.Quantity > 0 && LotStatus.IsOnHand(x.State.Status))
            .ToList();
        var codes = await store.ListCodesAsync([.. lots.Select(x => x.State.LocationId).OfType<Guid>().Distinct()], cancellationToken);
        return [.. lots.GroupBy(x => x.Lot.MaterialId)
            .Select(g =>
            {
                var first = g.First().Lot;
                return new StockOnDateDto
                {
                    MaterialId = first.MaterialId,
                    Code = first.MaterialCode,
                    Name = first.MaterialName,
                    MaterialClassName = first.MaterialClassName,
                    UomCode = first.UomCode,
                    Quantity = g.Sum(x => x.State.Quantity),
                    LotCount = g.Count(),
                    OnHoldQuantity = g.Where(x => x.State.Status != LotStatus.Available).Sum(x => x.State.Quantity),
                    Locations = [.. g.GroupBy(x => x.State.LocationId)
                        .Select(l => new StockAtLocationDto(l.Key, l.Key is { } id && codes.TryGetValue(id, out var code) ? code : "", l.Sum(x => x.State.Quantity),
                            l.Where(x => x.State.Status != LotStatus.Available).Sum(x => x.State.Quantity)))
                        .OrderBy(l => l.LocationCode, StringComparer.Ordinal)],
                };
            })
            .OrderBy(r => r.MaterialClassName is null).ThenBy(r => r.MaterialClassName, StringComparer.Ordinal).ThenBy(r => r.Code, StringComparer.Ordinal)];
    }
}

internal sealed class GetLotStateAtHandler(IInventoryReadStore store, TimeProvider time) : IQueryHandler<GetLotStateAtQuery, LotStateAtDto>
{
    public async Task<LotStateAtDto> HandleAsync(GetLotStateAtQuery query, CancellationToken cancellationToken)
    {
        new Validator().Require(query.At <= time.GetUtcNow(), nameof(query.At), "Must be in the past; the lot's page shows it now.").ThrowIfInvalid();
        var source = await store.GetLotPastAsync(query.LotId, cancellationToken) ?? throw new NotFoundException($"Lot {query.LotId} not found.");
        var state = LotPastState.At(source.Now, source.Events, query.At);
        var codes = await store.ListCodesAsync([.. new[] { state.LocationId, state.ContainerId }.OfType<Guid>()], cancellationToken);
        return new LotStateAtDto
        {
            LotId = source.LotId,
            At = query.At,
            Existed = state.Existed,
            Quantity = state.Quantity,
            UomCode = source.UomCode,
            LocationId = state.LocationId,
            LocationCode = state.LocationId is { } location && codes.TryGetValue(location, out var lc) ? lc : null,
            Status = state.Status,
            ContainerId = state.ContainerId,
            ContainerCode = state.ContainerId is { } container && codes.TryGetValue(container, out var cc) ? cc : null,
        };
    }
}

internal sealed class AgingHandler(IInventoryReadStore store, TimeProvider time) : IQueryHandler<AgingQuery, IReadOnlyList<AgingRowDto>>
{
    public Task<IReadOnlyList<AgingRowDto>> HandleAsync(AgingQuery query, CancellationToken cancellationToken) =>
        store.AgingAsync(query.SiteId, time.GetUtcNow(), cancellationToken);
}
