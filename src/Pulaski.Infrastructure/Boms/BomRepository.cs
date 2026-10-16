using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Domain.Boms;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Processes;

namespace Pulaski.Infrastructure.Boms;

/// <summary>Write side for bills of materials (D-025). Every statement runs in the command's transaction.</summary>
internal sealed class BomRepository(DbSession session) : IBomRepository
{
    private const string GetForUpdate = """
        SELECT id, material_id, process_id, is_active, xmin::text::bigint AS version
        FROM core.bom
        WHERE id = @Id
        FOR UPDATE
        """;

    private const string Insert = """
        INSERT INTO core.bom (id, material_id, process_id, is_active, created_at, updated_at)
        VALUES (@Id, @MaterialId, @ProcessId, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string VersionColumns = """
        v.id, v.bom_id, v.version_number AS number, v.status, v.base_qty, v.base_uom_id,
        v.definition::text AS definition_json, v.released_at, v.note, v.xmin::text::bigint AS version
        """;

    private const string GetVersionForUpdate = $"""
        SELECT {VersionColumns} FROM core.bom_version v
        WHERE v.bom_id = @BomId AND v.id = @VersionId
        FOR UPDATE
        """;

    private const string GetReleasedForUpdate = $"""
        SELECT {VersionColumns} FROM core.bom_version v
        WHERE v.bom_id = @BomId AND v.status = 'released'
        FOR UPDATE
        """;

    // The material's BOM for the process wins over its BOM for any process (D-025).
    private const string GetReleasedForOrder = $"""
        SELECT {VersionColumns}
        FROM core.bom b
        JOIN core.bom_version v ON v.bom_id = b.id AND v.status = 'released'
        WHERE b.material_id = @MaterialId AND b.is_active
          AND (b.process_id = @ProcessId OR b.process_id IS NULL)
        ORDER BY b.process_id NULLS LAST
        LIMIT 1
        FOR SHARE OF v
        """;

    private const string HasDraft = "SELECT EXISTS (SELECT 1 FROM core.bom_version WHERE bom_id = @BomId AND status = 'draft')";

    private const string InsertVersion = """
        INSERT INTO core.bom_version (id, bom_id, version_number, status, base_qty, base_uom_id, definition, released_at, note, created_at, updated_at)
        VALUES (@Id, @BomId, @Number, @Status, @BaseQty, @BaseUomId, @Definition::jsonb, @ReleasedAt, @Note, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateVersion = """
        UPDATE core.bom_version
        SET status = @Status, base_qty = @BaseQty, base_uom_id = @BaseUomId, definition = @Definition::jsonb,
            released_at = @ReleasedAt, note = @Note, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertItem = """
        INSERT INTO core.bom_item (id, bom_version_id, line_no, component_material_id, qty_per, uom_id,
                                   scrap_factor, consumption_method, operation_number)
        VALUES (@Id, @VersionId, @LineNo, @ComponentMaterialId, @QtyPer, @UomId,
                @ScrapFactor, @ConsumptionMethod, @OperationNumber)
        """;

    internal const string Materials = """
        SELECT id, code, base_uom_id, is_active
        FROM core.material
        WHERE id = ANY(@Ids)
        """;

    private const string InsertJobMaterial = """
        INSERT INTO core.job_material (id, job_id, material_id, direction, planned_qty, uom_id, consumption_method, bom_item_id)
        VALUES (@Id, @JobId, @MaterialId, @Direction, @PlannedQty, @UomId, @ConsumptionMethod, @BomItemId)
        """;

    public async Task<Versioned<Bom>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<BomRow>(GetForUpdate, new { Id = id }, cancellationToken);
        return row is null ? null : new(new Bom(row.Id, row.MaterialId, row.ProcessId, row.IsActive), row.Version);
    }

    public Task<long> InsertAsync(Bom bom, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, new { bom.Id, bom.MaterialId, bom.ProcessId, bom.IsActive, At = at }, cancellationToken);

    public async Task<Versioned<BomVersion>?> GetVersionForUpdateAsync(Guid bomId, Guid versionId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<VersionRow>(
            GetVersionForUpdate, new { BomId = bomId, VersionId = versionId }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<BomVersion>?> GetReleasedForUpdateAsync(Guid bomId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<VersionRow>(GetReleasedForUpdate, new { BomId = bomId }, cancellationToken))?.ToVersioned();

    public async Task<BomVersion?> GetReleasedForOrderAsync(Guid materialId, Guid processId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<VersionRow>(
            GetReleasedForOrder, new { MaterialId = materialId, ProcessId = processId }, cancellationToken))?.ToVersioned().Value;

    public Task<bool> HasDraftAsync(Guid bomId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(HasDraft, new { BomId = bomId }, cancellationToken);

    public Task<long> InsertVersionAsync(BomVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertVersion, VersionArgs(version, at), cancellationToken);

    public Task<long> UpdateVersionAsync(BomVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateVersion, VersionArgs(version, at), cancellationToken);

    public async Task InsertReleasedRowsAsync(BomVersion version, CancellationToken cancellationToken)
    {
        foreach (var item in version.Definition.Items)
        {
            await session.ExecuteAsync(
                InsertItem,
                new
                {
                    item.Id,
                    VersionId = version.Id,
                    item.LineNo,
                    item.ComponentMaterialId,
                    item.QtyPer,
                    item.UomId,
                    item.ScrapFactor,
                    item.ConsumptionMethod,
                    item.OperationNumber,
                },
                cancellationToken);
        }
    }

    public Task<IReadOnlyList<BomMaterialRow>> GetMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        session.QueryAsync<BomMaterialRow>(Materials, new { Ids = ids.ToArray() }, cancellationToken);

    public async Task InsertJobMaterialsAsync(IReadOnlyList<JobMaterial> rows, CancellationToken cancellationToken)
    {
        foreach (var row in rows)
        {
            await session.ExecuteAsync(InsertJobMaterial, row, cancellationToken);
        }
    }

    private static object VersionArgs(BomVersion version, DateTimeOffset at) => new
    {
        version.Id,
        version.BomId,
        version.Number,
        version.Status,
        version.Definition.BaseQty,
        version.Definition.BaseUomId,
        Definition = ProcessJson.Write(new BomItemsDocument(version.Definition.Items)),
        version.ReleasedAt,
        version.Note,
        At = at,
    };

    private sealed class BomRow
    {
        public Guid Id { get; init; }
        public Guid MaterialId { get; init; }
        public Guid? ProcessId { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class VersionRow
    {
        public Guid Id { get; init; }
        public Guid BomId { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public decimal BaseQty { get; init; }
        public Guid BaseUomId { get; init; }
        public string DefinitionJson { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? Note { get; init; }
        public long Version { get; init; }

        public Versioned<BomVersion> ToVersioned() => new(
            new BomVersion(
                Id, BomId, Number, Status,
                new BomDefinition(BaseQty, BaseUomId, ProcessJson.Read<BomItemsDocument>(DefinitionJson).Items),
                ReleasedAt, Note),
            Version);
    }
}

/// <summary>
/// The stored shape of <c>core.bom_version.definition</c>: the items, camelCase, the same as the
/// API's item DTOs. The base quantity and unit are columns.
/// </summary>
internal sealed record BomItemsDocument(IReadOnlyList<BomItem> Items);
