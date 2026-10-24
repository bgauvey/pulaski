using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Active materials with their stock, for the inventory list grouped by class (D-027).</summary>
public sealed record ListStockQuery(StockFilter Filter) : IQuery<IReadOnlyList<MaterialStockDto>>;

public sealed record GetMaterialStockQuery(Guid MaterialId, Guid? SiteId = null) : IQuery<MaterialStockDetailDto>;

/// <summary>Lots by the filter and, when <paramref name="Attribute"/> is given, by an attribute's value (D-056).</summary>
public sealed record SearchLotsQuery(LotFilter Filter, LotAttributeSearch? Attribute = null) : IQuery<IReadOnlyList<LotSummaryDto>>;

/// <summary>An attribute search as typed: a value (text contains it, a choice, yes/no or exact number or date), or a range.</summary>
public sealed record LotAttributeSearch(Guid AttributeId, string? Value, string? Min, string? Max);

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

internal sealed class SearchLotsHandler(IInventoryReadStore store, ILotRecordsReadStore records) : IQueryHandler<SearchLotsQuery, IReadOnlyList<LotSummaryDto>>
{
    public async Task<IReadOnlyList<LotSummaryDto>> HandleAsync(SearchLotsQuery query, CancellationToken cancellationToken)
    {
        new Validator()
            .Require(query.Filter.Offset >= 0, "Offset", "Can't be negative.")
            .Require(query.Filter.Limit is > 0 and <= 500, "Limit", "Between 1 and 500.")
            .ThrowIfInvalid();
        var filter = query.Attribute is { } search ? query.Filter with { Attribute = await MatchAsync(search, cancellationToken) } : query.Filter;
        return await store.SearchLotsAsync(filter, cancellationToken);
    }

    /// <summary>The search in the attribute's own type: an unparsable value is a validation error, not an empty list.</summary>
    private async Task<LotAttributeMatch> MatchAsync(LotAttributeSearch search, CancellationToken cancellationToken)
    {
        var attribute = await records.GetAttributeAsync(search.AttributeId, cancellationToken)
            ?? throw new NotFoundException($"Lot attribute {search.AttributeId} not found.");
        LotAttributeValue Parse(string? text, string field)
        {
            try
            {
                return attribute.Parse(text);
            }
            catch (DomainRuleException ex)
            {
                throw new ValidationException(new Dictionary<string, string[]> { [field] = [ex.Message] });
            }
        }

        var value = Parse(search.Value, "attributeValue");
        var min = Parse(search.Min, "attributeMin");
        var max = Parse(search.Max, "attributeMax");
        return new LotAttributeMatch(
            attribute.Id,
            value.Text,
            attribute.DataType == LotAttributeTypes.Text,
            value.Number ?? min.Number,
            value.Number ?? max.Number,
            value.Date ?? min.Date,
            value.Date ?? max.Date,
            value.Boolean);
    }
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
        return nodes.Count == 0
            ? throw new NotFoundException($"Lot {query.LotId} not found.")
            : await TraceNarrowing.ApplyAsync(nodes, query.Forward, query.LotId, store, cancellationToken);
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
