using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Active materials with their stock, for the inventory list grouped by class (D-027).</summary>
public sealed record ListStockQuery(StockFilter Filter) : IQuery<IReadOnlyList<MaterialStockDto>>;

public sealed record GetMaterialStockQuery(Guid MaterialId, Guid? SiteId = null) : IQuery<MaterialStockDetailDto>;

public sealed record SearchLotsQuery(LotFilter Filter) : IQuery<IReadOnlyList<LotSummaryDto>>;

public sealed record GetLotQuery(Guid Id) : IQuery<LotDto>;

/// <summary>Forward (what was made from the lot) or backward (what it was made from), all the way.</summary>
public sealed record TraceLotQuery(Guid LotId, bool Forward) : IQuery<IReadOnlyList<LotTraceNodeDto>>;

public sealed record WhereUsedQuery(Guid MaterialId) : IQuery<WhereUsedDto>;

public sealed record ListInventoryLocationsQuery(Guid? SiteId = null) : IQuery<IReadOnlyList<InventoryLocationDto>>;

public sealed record ListDeclarableJobsQuery(Guid? SiteId = null, string? Search = null, Guid? MaterialId = null)
    : IQuery<IReadOnlyList<DeclarableJobDto>>;

public sealed record GetJobMaterialsQuery(Guid JobId) : IQuery<JobMaterialsDto>;

public sealed record ListReasonCodesQuery(string Kind, Guid? SiteId = null) : IQuery<IReadOnlyList<ReasonCodeDto>>;

internal sealed class ListStockHandler(IInventoryReadStore store) : IQueryHandler<ListStockQuery, IReadOnlyList<MaterialStockDto>>
{
    public Task<IReadOnlyList<MaterialStockDto>> HandleAsync(ListStockQuery query, CancellationToken cancellationToken) =>
        store.ListStockAsync(query.Filter, cancellationToken);
}

internal sealed class GetMaterialStockHandler(IInventoryReadStore store) : IQueryHandler<GetMaterialStockQuery, MaterialStockDetailDto>
{
    public async Task<MaterialStockDetailDto> HandleAsync(GetMaterialStockQuery query, CancellationToken cancellationToken) =>
        await store.GetMaterialStockAsync(query.MaterialId, query.SiteId, cancellationToken)
            ?? throw new NotFoundException($"Material {query.MaterialId} not found.");
}

internal sealed class SearchLotsHandler(IInventoryReadStore store) : IQueryHandler<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>
{
    public Task<IReadOnlyList<LotSummaryDto>> HandleAsync(SearchLotsQuery query, CancellationToken cancellationToken) =>
        store.SearchLotsAsync(query.Filter, cancellationToken);
}

internal sealed class GetLotHandler(IInventoryReadStore store) : IQueryHandler<GetLotQuery, LotDto>
{
    public async Task<LotDto> HandleAsync(GetLotQuery query, CancellationToken cancellationToken) =>
        await store.GetLotAsync(query.Id, cancellationToken) ?? throw new NotFoundException($"Lot {query.Id} not found.");
}

internal sealed class TraceLotHandler(IInventoryReadStore store) : IQueryHandler<TraceLotQuery, IReadOnlyList<LotTraceNodeDto>>
{
    public async Task<IReadOnlyList<LotTraceNodeDto>> HandleAsync(TraceLotQuery query, CancellationToken cancellationToken)
    {
        var nodes = await store.TraceAsync(query.LotId, query.Forward, cancellationToken);
        return nodes.Count == 0 ? throw new NotFoundException($"Lot {query.LotId} not found.") : nodes;
    }
}

internal sealed class WhereUsedHandler(IInventoryReadStore store) : IQueryHandler<WhereUsedQuery, WhereUsedDto>
{
    public async Task<WhereUsedDto> HandleAsync(WhereUsedQuery query, CancellationToken cancellationToken) =>
        await store.WhereUsedAsync(query.MaterialId, cancellationToken)
            ?? throw new NotFoundException($"Material {query.MaterialId} not found.");
}

internal sealed class ListInventoryLocationsHandler(IInventoryReadStore store)
    : IQueryHandler<ListInventoryLocationsQuery, IReadOnlyList<InventoryLocationDto>>
{
    public Task<IReadOnlyList<InventoryLocationDto>> HandleAsync(ListInventoryLocationsQuery query, CancellationToken cancellationToken) =>
        store.ListLocationsAsync(query.SiteId, cancellationToken);
}

internal sealed class ListDeclarableJobsHandler(IInventoryReadStore store)
    : IQueryHandler<ListDeclarableJobsQuery, IReadOnlyList<DeclarableJobDto>>
{
    public Task<IReadOnlyList<DeclarableJobDto>> HandleAsync(ListDeclarableJobsQuery query, CancellationToken cancellationToken) =>
        store.ListDeclarableJobsAsync(query.SiteId, query.Search, query.MaterialId, cancellationToken);
}

internal sealed class GetJobMaterialsHandler(IInventoryReadStore store) : IQueryHandler<GetJobMaterialsQuery, JobMaterialsDto>
{
    public async Task<JobMaterialsDto> HandleAsync(GetJobMaterialsQuery query, CancellationToken cancellationToken) =>
        await store.GetJobMaterialsAsync(query.JobId, cancellationToken)
            ?? throw new NotFoundException($"Production job {query.JobId} not found.");
}

internal sealed class ListReasonCodesHandler(IInventoryReadStore store) : IQueryHandler<ListReasonCodesQuery, IReadOnlyList<ReasonCodeDto>>
{
    public Task<IReadOnlyList<ReasonCodeDto>> HandleAsync(ListReasonCodesQuery query, CancellationToken cancellationToken)
    {
        new Validator().RequireText(query.Kind, nameof(query.Kind), 50).ThrowIfInvalid();
        return store.ListReasonCodesAsync(query.Kind, query.SiteId, cancellationToken);
    }
}
