using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Filters for stock by material. Null fields don't filter.</summary>
public sealed record StockFilter(
    Guid? SiteId = null,
    string? Search = null,
    Guid? MaterialClassId = null,
    Guid? LocationId = null,
    bool InStockOnly = false);

/// <summary>
/// Filters for finding lots. Without a <see cref="Status"/> only lots with stock on hand are
/// found; ask for <c>consumed</c> or <c>scrapped</c> to see the used-up ones.
/// </summary>
public sealed record LotFilter(
    Guid? SiteId = null,
    string? Search = null,
    Guid? MaterialId = null,
    Guid? LocationId = null,
    string? Status = null,
    DateTimeOffset? ExpiresBefore = null);

/// <summary>Read side for inventory (D-027): DTOs straight from SQL, no locks.</summary>
public interface IInventoryReadStore
{
    /// <summary>Active materials with their stock, by class name then code.</summary>
    Task<IReadOnlyList<MaterialStockDto>> ListStockAsync(StockFilter filter, CancellationToken cancellationToken);

    /// <summary>One material's stock, on-hand lots and balance per location; null when the material doesn't exist.</summary>
    Task<MaterialStockDetailDto?> GetMaterialStockAsync(Guid materialId, Guid? siteId, CancellationToken cancellationToken);

    /// <summary>Lots matching the filter, at most 500, by material code then lot number.</summary>
    Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(LotFilter filter, CancellationToken cancellationToken);

    Task<LotDto?> GetLotAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every lot downstream (forward) or upstream of the lot, the lot itself first, nearest first.</summary>
    Task<IReadOnlyList<LotTraceNodeDto>> TraceAsync(Guid lotId, bool forward, CancellationToken cancellationToken);

    /// <summary>Released and draft BOMs listing the material, and open jobs planned to consume it; null when it doesn't exist.</summary>
    Task<WhereUsedDto?> WhereUsedAsync(Guid materialId, CancellationToken cancellationToken);

    Task<IReadOnlyList<InventoryLocationDto>> ListLocationsAsync(Guid? siteId, CancellationToken cancellationToken);

    /// <summary>
    /// Open production jobs, at most 200. With <paramref name="materialId"/> the jobs planned to
    /// consume it come first, marked <see cref="DeclarableJobDto.PlansComponent"/>.
    /// </summary>
    Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(
        Guid? siteId, string? search, Guid? materialId, CancellationToken cancellationToken);

    Task<JobMaterialsDto?> GetJobMaterialsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Active, selectable reasons of the kind, enterprise-wide or for the site.</summary>
    Task<IReadOnlyList<ReasonCodeDto>> ListReasonCodesAsync(string kind, Guid? siteId, CancellationToken cancellationToken);
}

/// <summary>
/// Write side: runs inside the current <see cref="IUnitOfWork"/>. Commands lock the job (for
/// consumption and production) and then the lots, in id order.
/// </summary>
public interface IInventoryRepository
{
    Task<Versioned<Lot>?> GetLotForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<Versioned<Lot>?> FindLotForUpdateAsync(Guid siteId, Guid materialId, string lotNumber, CancellationToken cancellationToken);

    /// <summary>The bulk balance of a material that isn't lot-tracked at a location, locked.</summary>
    Task<Versioned<Lot>?> FindBulkLotForUpdateAsync(Guid materialId, Guid locationId, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the status, quantity and location.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken);

    Task InsertEventAsync(LotEventRow row, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A location read <c>FOR SHARE</c>; null when the equipment doesn't exist.</summary>
    Task<InventoryLocation?> GetLocationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A material read <c>FOR SHARE</c>, with its base unit's code.</summary>
    Task<InventoryMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The job locked <c>FOR UPDATE</c>, so declarations on one job serialize (D-027).</summary>
    Task<InventoryJob?> GetJobForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The lots the job has produced (<c>core.lot.produced_by_job_id</c>).</summary>
    Task<IReadOnlyList<Guid>> ListLotsProducedByJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The lots the job has consumed, each once.</summary>
    Task<IReadOnlyList<Guid>> ListLotsConsumedByJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Adds declared edges; an edge already there is left alone.</summary>
    Task InsertGenealogyAsync(IReadOnlyList<GenealogyEdge> edges, DateTimeOffset at, CancellationToken cancellationToken);

    Task<InventoryReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when the job has a planned component (<c>consume</c>) of the material.</summary>
    Task<bool> PlansComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken);
}

/// <summary>A row of <c>ts.lot_event</c>: the lot's movement plus who, why and for which job.</summary>
public sealed record LotEventRow(
    Guid Id,
    Guid LotId,
    LotMovement Movement,
    Guid UomId,
    string ActorId,
    Guid? JobId = null,
    Guid? ReasonCodeId = null,
    string? Comment = null,
    Guid? RelatedLotId = null);

public sealed record GenealogyEdge(Guid Id, Guid ParentLotId, Guid ChildLotId, Guid JobId);

public sealed record InventoryLocation
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public Guid? SiteId { get; init; }
    public bool HoldsInventory { get; init; }
    public bool IsActive { get; init; }
}

public sealed record InventoryMaterial
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }
    public bool IsLotTracked { get; init; }
    public bool IsActive { get; init; }
}

public sealed record InventoryJob
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public Guid? MaterialId { get; init; }
}

public sealed record InventoryReason
{
    public required Guid Id { get; init; }
    public Guid? SiteId { get; init; }
    public required string Kind { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsSelectable { get; init; }
    public bool RequiresComment { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Reason kinds inventory uses. Values match <c>core.reason_code.reason_kind</c>.</summary>
public static class ReasonKinds
{
    public const string InventoryAdjustment = "inventory_adjustment";
    public const string Hold = "hold";
}
