using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Application.Inventory;

public sealed record ListStockCountsQuery(Guid SiteId) : IQuery<IReadOnlyList<StockCountSummaryDto>>;

public sealed record GetStockCountQuery(Guid Id) : IQuery<StockCountDto>;

/// <summary>Every reason, for the reason-code screen (D-030).</summary>
public sealed record ListAllReasonCodesQuery(Guid? SiteId = null) : IQuery<IReadOnlyList<ReasonCodeAdminDto>>;

/// <summary>Each material's on-hand quantity at <see cref="At"/>, which must be in the past (D-030).</summary>
public sealed record StockOnDateQuery(Guid SiteId, DateTimeOffset At) : IQuery<IReadOnlyList<StockOnDateDto>>;

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
    public Task<IReadOnlyList<StockOnDateDto>> HandleAsync(StockOnDateQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.SiteId != Guid.Empty, nameof(query.SiteId), "Required.")
            .Require(query.At <= time.GetUtcNow(), nameof(query.At), "Must be in the past; today's stock is on the Stock tab.")
            .ThrowIfInvalid();
        return store.StockOnDateAsync(query.SiteId, query.At, cancellationToken);
    }
}

internal sealed class AgingHandler(IInventoryReadStore store, TimeProvider time) : IQueryHandler<AgingQuery, IReadOnlyList<AgingRowDto>>
{
    public Task<IReadOnlyList<AgingRowDto>> HandleAsync(AgingQuery query, CancellationToken cancellationToken) =>
        store.AgingAsync(query.SiteId, time.GetUtcNow(), cancellationToken);
}
