using Dapper;
using Npgsql;
using Pulaski.Application.Boms;
using Pulaski.Contracts.Boms;
using Pulaski.Infrastructure.Processes;

namespace Pulaski.Infrastructure.Boms;

/// <summary>Read side for bills of materials (D-025): one connection per query, no locks.</summary>
internal sealed class BomQueries(NpgsqlDataSource dataSource) : IBomReadStore
{
    private const string List = """
        SELECT b.id, b.material_id, m.code AS material_code, m.name AS material_name,
               b.process_id, p.code AS process_code, b.is_active, b.updated_at,
               (SELECT v.version_number FROM core.bom_version v WHERE v.bom_id = b.id AND v.status = 'released') AS released_version,
               (SELECT v.version_number FROM core.bom_version v WHERE v.bom_id = b.id AND v.status = 'draft') AS draft_version,
               (SELECT count(*)::int FROM core.bom_item i JOIN core.bom_version v ON v.id = i.bom_version_id
                WHERE v.bom_id = b.id AND v.status = 'released') AS released_items
        FROM core.bom b
        JOIN core.material m ON m.id = b.material_id
        LEFT JOIN core.process p ON p.id = b.process_id
        WHERE @MaterialId::uuid IS NULL OR b.material_id = @MaterialId
        ORDER BY m.code, p.code NULLS FIRST
        """;

    private const string Get = """
        SELECT b.id, b.material_id, m.code AS material_code, m.name AS material_name, m.base_uom_id AS material_base_uom_id,
               b.process_id, p.code AS process_code, p.name AS process_name, b.is_active, b.xmin::text::bigint AS version
        FROM core.bom b
        JOIN core.material m ON m.id = b.material_id
        LEFT JOIN core.process p ON p.id = b.process_id
        WHERE b.id = @Id
        """;

    private const string Versions = """
        SELECT id, version_number AS number, status, released_at, note
        FROM core.bom_version
        WHERE bom_id = @Id
        ORDER BY version_number DESC
        """;

    private const string GetVersion = """
        SELECT id, bom_id, version_number AS number, status, base_qty, base_uom_id, definition::text AS definition_json,
               released_at, note, xmin::text::bigint AS version
        FROM core.bom_version
        WHERE bom_id = @BomId AND id = @VersionId
        """;

    private const string PlannedComponents = """
        SELECT j.id AS job_id, j.job_number, e.code AS equipment_code, o.operation_number, i.line_no,
               jm.material_id, m.code AS material_code, m.name AS material_name, jm.planned_qty, u.code AS uom_code,
               jm.consumption_method
        FROM core.job_material jm
        JOIN core.job j       ON j.id = jm.job_id
        JOIN core.equipment e ON e.id = j.equipment_id
        JOIN core.material m  ON m.id = jm.material_id
        JOIN core.uom u       ON u.id = jm.uom_id
        LEFT JOIN core.process_operation o ON o.id = j.process_operation_id
        LEFT JOIN core.bom_item i ON i.id = jm.bom_item_id
        WHERE j.work_order_id = @WorkOrderId AND jm.direction = 'consume'
        ORDER BY o.operation_number NULLS FIRST, j.job_number, i.line_no
        """;

    public async Task<IReadOnlyList<BomSummaryDto>> ListAsync(Guid? materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomSummaryDto>(
            new CommandDefinition(List, new { MaterialId = materialId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<BomDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var bom = await connection.QuerySingleOrDefaultAsync<BomDto>(new CommandDefinition(Get, new { Id = id }, cancellationToken: cancellationToken));
        if (bom is null)
        {
            return null;
        }

        var versions = await connection.QueryAsync<VersionSummaryRow>(new CommandDefinition(Versions, new { Id = id }, cancellationToken: cancellationToken));
        return bom with { Versions = [.. versions.Select(v => new BomVersionSummaryDto(v.Id, v.Number, v.Status, v.ReleasedAt, v.Note))] };
    }

    public async Task<BomVersionDto?> GetVersionAsync(Guid bomId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(
            new CommandDefinition(GetVersion, new { BomId = bomId, VersionId = versionId }, cancellationToken: cancellationToken));
        return row is null ? null : new BomVersionDto
        {
            Id = row.Id,
            BomId = row.BomId,
            Number = row.Number,
            Status = row.Status,
            ReleasedAt = row.ReleasedAt,
            Note = row.Note,
            Version = row.Version,
            Definition = new BomDefinitionDto
            {
                BaseQty = row.BaseQty,
                BaseUomId = row.BaseUomId,
                Items = ProcessJson.Read<ItemsDocument>(row.DefinitionJson).Items,
            },
        };
    }

    public async Task<IReadOnlyList<BomMaterialRow>> GetMaterialsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<BomMaterialRow>(
            new CommandDefinition(BomRepository.Materials, new { Ids = ids.ToArray() }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<PlannedComponentDto>> ListPlannedComponentsAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<PlannedComponentDto>(
            new CommandDefinition(PlannedComponents, new { WorkOrderId = workOrderId }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    private sealed record ItemsDocument(IReadOnlyList<BomItemDto> Items);

    private sealed class VersionSummaryRow
    {
        public Guid Id { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? Note { get; init; }
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
    }
}
