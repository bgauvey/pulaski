using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Boms;
using Pulaski.Domain.Boms;

namespace Pulaski.Application.Boms;

/// <summary>Read side for bills of materials (D-025): DTOs straight from SQL, no locks.</summary>
public interface IBomReadStore
{
    /// <summary>BOMs, only those of <paramref name="materialId"/> when it is given, by material then process.</summary>
    Task<IReadOnlyList<BomSummaryDto>> ListAsync(Guid? materialId, CancellationToken cancellationToken);

    /// <summary>The BOM with its versions, newest first.</summary>
    Task<BomDto?> GetAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>One version with its definition; <see cref="BomVersionDto.Problems"/> is not filled.</summary>
    Task<BomVersionDto?> GetVersionAsync(Guid bomId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>The materials a definition names, for checking them.</summary>
    Task<IReadOnlyList<BomMaterialRow>> GetMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The components planned for a work order's jobs, by job then BOM line.</summary>
    Task<IReadOnlyList<PlannedComponentDto>> ListPlannedComponentsAsync(Guid workOrderId, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>. Commands lock the BOM, then its version.</summary>
public interface IBomRepository
{
    Task<Versioned<Bom>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertAsync(Bom bom, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<BomVersion>?> GetVersionForUpdateAsync(Guid bomId, Guid versionId, CancellationToken cancellationToken);

    /// <summary>The BOM's released version, if it has one, locked.</summary>
    Task<Versioned<BomVersion>?> GetReleasedForUpdateAsync(Guid bomId, CancellationToken cancellationToken);

    Task<bool> HasDraftAsync(Guid bomId, CancellationToken cancellationToken);

    /// <summary>A version by its id, without a lock: what an order was planned with, which never changes once released.</summary>
    Task<BomVersion?> GetVersionAsync(Guid versionId, CancellationToken cancellationToken);

    /// <summary>
    /// The process's active BOMs whose released version names operations the process's released
    /// version doesn't have, read inside the transaction (after a process release, D-025).
    /// </summary>
    Task<IReadOnlyList<BomMissingOperationsRow>> ListMissingOperationsAsync(Guid processId, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertVersionAsync(BomVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the status, base quantity and unit, definition, release time and note.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateVersionAsync(BomVersion version, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes a released version's items as fixed rows (D-010).</summary>
    Task InsertReleasedRowsAsync(BomVersion version, CancellationToken cancellationToken);

    /// <summary>The materials a definition names (and the BOM's own), read inside the transaction.</summary>
    Task<IReadOnlyList<BomMaterialRow>> GetMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>
    /// The released version an order for the material made with the process is planned with, read
    /// <c>FOR SHARE</c>: the material's BOM for that process if it has a released version, or else
    /// its BOM for any process; null when neither has one (D-025). Inactive BOMs are skipped.
    /// </summary>
    Task<BomVersion?> GetReleasedForOrderAsync(Guid materialId, Guid processId, CancellationToken cancellationToken);

    Task InsertJobMaterialsAsync(IReadOnlyList<JobMaterial> rows, CancellationToken cancellationToken);
}

/// <summary>A BOM that names operations its process's released version no longer has.</summary>
public sealed record BomMissingOperationsRow
{
    public required Guid BomId { get; init; }
    public required string MaterialCode { get; init; }
    public int VersionNumber { get; init; }
    public int[] Operations { get; init; } = [];
}

public sealed record BomMaterialRow
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required Guid BaseUomId { get; init; }
    public bool IsActive { get; init; }
    public string MaterialType { get; init; } = "";
    public decimal? BatchSize { get; init; }
    public decimal BatchTolerancePct { get; init; }
    public string? BatchRule { get; init; }

    /// <summary>How the material is made in batches (D-036); null when it is made continuously.</summary>
    public Domain.Materials.Batching? Batching => BatchSize is { } size ? new(size, BatchTolerancePct, BatchRule ?? Domain.Materials.BatchRule.Near) : null;
}

/// <summary>A planned component of a job (<c>core.job_material</c>).</summary>
public sealed record JobMaterial(
    Guid Id,
    Guid JobId,
    Guid MaterialId,
    string Direction,
    decimal PlannedQty,
    Guid UomId,
    string? ConsumptionMethod,
    Guid? BomItemId)
{
    public const string Consume = "consume";
}
