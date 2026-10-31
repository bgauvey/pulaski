using Dapper;
using Npgsql;
using Pulaski.Application.Processes;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.Processes;

namespace Pulaski.Infrastructure.Processes;

/// <summary>Read side for process routes: one connection per query, no locks.</summary>
internal sealed class ProcessQueries(NpgsqlDataSource dataSource) : IProcessReadStore
{
    private const string List = $$"""
        SELECT p.id, p.site_id, p.code, p.name, p.description, p.is_active, p.updated_at,
               (SELECT v.version_number FROM core.process_version v WHERE v.process_id = p.id AND v.status = 'released') AS released_version,
               (SELECT v.version_number FROM core.process_version v WHERE v.process_id = p.id AND v.status = 'draft') AS draft_version,
               coalesce((SELECT string_agg(m.code, ',' ORDER BY m.code) FROM core.material m WHERE m.default_process_id = p.id), '') AS default_for,
               p.makes_all_materials,
               coalesce((SELECT string_agg(c.code, ',' ORDER BY c.code)
                         FROM core.process_material_class pc JOIN core.material_class c ON c.id = pc.material_class_id
                         WHERE pc.process_id = p.id AND pc.is_active), '') AS makes_classes,
               coalesce((SELECT string_agg(m.code, ',' ORDER BY m.code)
                         FROM core.process_material pm JOIN core.material m ON m.id = pm.material_id
                         WHERE pm.process_id = p.id AND pm.is_active), '') AS makes_materials
        FROM core.process p
        WHERE (@SiteId::uuid IS NULL OR p.site_id = @SiteId)
          AND (@MaterialId::uuid IS NULL OR {{Covers}})
        ORDER BY p.code
        """;

    // Whether process p's scope covers material @MaterialId (D-023).
    internal const string Covers = """
        (p.makes_all_materials
         OR EXISTS (SELECT 1 FROM core.process_material pm
                    WHERE pm.process_id = p.id AND pm.material_id = @MaterialId AND pm.is_active)
         OR EXISTS (SELECT 1 FROM core.process_material_class pc
                    JOIN core.material m ON m.material_class_id = pc.material_class_id
                    WHERE pc.process_id = p.id AND m.id = @MaterialId AND pc.is_active))
        """;

    private const string MakesClasses = """
        SELECT c.id, c.code, c.name
        FROM core.process_material_class pc JOIN core.material_class c ON c.id = pc.material_class_id
        WHERE pc.process_id = @Id AND pc.is_active
        ORDER BY c.code
        """;

    private const string MakesMaterials = """
        SELECT m.id, m.code, m.name
        FROM core.process_material pm JOIN core.material m ON m.id = pm.material_id
        WHERE pm.process_id = @Id AND pm.is_active
        ORDER BY m.code
        """;

    private const string Get = """
        SELECT p.id, p.site_id, s.code AS site_code, p.code, p.name, p.description, p.is_active,
               p.makes_all_materials, p.xmin::text::bigint AS version
        FROM core.process p
        JOIN core.equipment s ON s.id = p.site_id
        WHERE p.id = @Id
        """;

    private const string Versions = """
        SELECT id, version_number AS number, status, released_at, note
        FROM core.process_version
        WHERE process_id = @Id
        ORDER BY version_number DESC
        """;

    private const string DefaultFor = """
        SELECT id, code, name FROM core.material WHERE default_process_id = @Id ORDER BY code
        """;

    private const string GetVersion = """
        SELECT id, process_id, version_number AS number, status, released_at, note,
               definition::text AS definition_json, xmin::text::bigint AS version
        FROM core.process_version
        WHERE process_id = @ProcessId AND id = @VersionId
        """;

    internal const string Equipment = """
        SELECT id, site_id, code, level, is_schedulable, can_run_job, is_active
        FROM core.equipment
        WHERE id = ANY(@Ids)
        """;

    public async Task<IReadOnlyList<ProcessSummaryDto>> ListAsync(Guid? siteId, Guid? materialId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SummaryRow>(
            new CommandDefinition(List, new { SiteId = siteId, MaterialId = materialId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new ProcessSummaryDto
        {
            Id = r.Id,
            SiteId = r.SiteId,
            Code = r.Code,
            Name = r.Name,
            Description = r.Description,
            IsActive = r.IsActive,
            ReleasedVersion = r.ReleasedVersion,
            DraftVersion = r.DraftVersion,
            DefaultFor = Codes(r.DefaultFor),
            MakesAllMaterials = r.MakesAllMaterials,
            MakesClasses = Codes(r.MakesClasses),
            MakesMaterials = Codes(r.MakesMaterials),
            UpdatedAt = r.UpdatedAt,
        })];
    }

    public async Task<ProcessDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = id };
        var process = await connection.QuerySingleOrDefaultAsync<ProcessDto>(new CommandDefinition(Get, args, cancellationToken: cancellationToken));
        if (process is null)
        {
            return null;
        }

        var versions = await connection.QueryAsync<ProcessVersionSummaryDto>(new CommandDefinition(Versions, args, cancellationToken: cancellationToken));
        var defaultFor = await connection.QueryAsync<ProcessMaterialDto>(new CommandDefinition(DefaultFor, args, cancellationToken: cancellationToken));
        var classes = await connection.QueryAsync<ProcessMaterialClassDto>(new CommandDefinition(MakesClasses, args, cancellationToken: cancellationToken));
        var materials = await connection.QueryAsync<ProcessMaterialDto>(new CommandDefinition(MakesMaterials, args, cancellationToken: cancellationToken));
        return process with
        {
            Versions = versions.AsList(),
            DefaultFor = defaultFor.AsList(),
            MakesClasses = classes.AsList(),
            MakesMaterials = materials.AsList(),
        };
    }

    public async Task<ProcessVersionDto?> GetVersionAsync(Guid processId, Guid versionId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<VersionRow>(
            new CommandDefinition(GetVersion, new { ProcessId = processId, VersionId = versionId }, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new ProcessVersionDto
            {
                Id = row.Id,
                ProcessId = row.ProcessId,
                Number = row.Number,
                Status = row.Status,
                ReleasedAt = row.ReleasedAt,
                Note = row.Note,
                Definition = ProcessJson.Read<ProcessDefinitionDto>(row.DefinitionJson),
                Version = row.Version,
            };
    }

    // A released version's lines and departments, and whether each can run @MaterialId at @At:
    // a line by its own capability, a department by one on an active line under it that runs
    // jobs. The same tests as scheduling's eligibility checks (D-020, D-022).
    internal const string OrderEquipment = """
        SELECT o.id AS operation_id, o.operation_number, o.name AS operation_name,
               e.id AS equipment_id, e.code AS equipment_code, e.site_id, e.is_schedulable, e.can_run_job, e.is_active,
               m.code AS material_code,
               CASE WHEN e.can_run_job THEN EXISTS (
                        SELECT 1 FROM core.equipment_capability c
                        WHERE c.equipment_id = e.id AND c.material_id = m.id
                          AND c.effective_from <= @At AND (c.effective_to IS NULL OR c.effective_to > @At))
                    ELSE EXISTS (
                        SELECT 1
                        FROM core.equipment_closure cl
                        JOIN core.equipment l ON l.id = cl.descendant_id AND l.can_run_job AND l.is_active
                        JOIN core.equipment_capability c ON c.equipment_id = l.id
                        WHERE cl.ancestor_id = e.id AND cl.depth > 0 AND c.material_id = m.id
                          AND c.effective_from <= @At AND (c.effective_to IS NULL OR c.effective_to > @At))
               END AS can_run_material,
               oe.share_percent,
               -- A line's own batch size for the material (D-065), shown on the order form (#85); a department has none.
               CASE WHEN e.can_run_job THEN (
                        SELECT c.batch_size FROM core.equipment_capability c
                        WHERE c.equipment_id = e.id AND c.material_id = m.id AND c.batch_size IS NOT NULL
                          AND c.effective_from <= @At AND (c.effective_to IS NULL OR c.effective_to > @At)
                        LIMIT 1)
               END AS line_batch_size
        FROM core.process_operation o
        JOIN core.process_operation_equipment oe ON oe.operation_id = o.id
        JOIN core.equipment e ON e.id = oe.equipment_id
        JOIN core.material m ON m.id = @MaterialId
        WHERE o.process_version_id = @VersionId
        ORDER BY o.operation_number, e.code
        """;

    public async Task<IReadOnlyList<OrderEquipmentRow>> GetOrderEquipmentAsync(
        Guid versionId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OrderEquipmentRow>(new CommandDefinition(
            OrderEquipment, new { VersionId = versionId, MaterialId = materialId, At = at }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ProcessEquipmentRow>> GetEquipmentAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ProcessEquipmentRow>(
            new CommandDefinition(Equipment, new { Ids = ids.ToArray() }, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    private sealed class SummaryRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool IsActive { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public int? ReleasedVersion { get; init; }
        public int? DraftVersion { get; init; }
        public string DefaultFor { get; init; } = "";
        public bool MakesAllMaterials { get; init; }
        public string MakesClasses { get; init; } = "";
        public string MakesMaterials { get; init; } = "";
    }

    private static string[] Codes(string joined) => joined.Length == 0 ? [] : joined.Split(',');

    private sealed class VersionRow
    {
        public Guid Id { get; init; }
        public Guid ProcessId { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? Note { get; init; }
        public string DefinitionJson { get; init; } = "";
        public long Version { get; init; }
    }
}
