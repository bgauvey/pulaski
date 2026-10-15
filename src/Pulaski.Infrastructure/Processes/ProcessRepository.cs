using Pulaski.Application.Abstractions;
using Pulaski.Application.Processes;
using Pulaski.Domain.Processes;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Processes;

/// <summary>Write side for process routes. Every statement runs in the command's transaction.</summary>
internal sealed class ProcessRepository(DbSession session) : IProcessRepository
{
    private const string GetForUpdate = """
        SELECT id, site_id, code, name, description, is_active, xmin::text::bigint AS version
        FROM core.process
        WHERE id = @Id
        FOR UPDATE
        """;

    private const string Insert = """
        INSERT INTO core.process (id, site_id, code, name, description, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Description, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.process
        SET name = @Name, description = @Description, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string VersionColumns = """
        id, process_id, version_number AS number, status, definition::text AS definition_json, released_at, note,
        xmin::text::bigint AS version
        """;

    private const string GetVersionForUpdate = $"""
        SELECT {VersionColumns} FROM core.process_version
        WHERE process_id = @ProcessId AND id = @VersionId
        FOR UPDATE
        """;

    private const string GetReleasedForUpdate = $"""
        SELECT {VersionColumns} FROM core.process_version
        WHERE process_id = @ProcessId AND status = 'released'
        FOR UPDATE
        """;

    private const string HasDraft = "SELECT EXISTS (SELECT 1 FROM core.process_version WHERE process_id = @ProcessId AND status = 'draft')";

    private const string InsertVersion = """
        INSERT INTO core.process_version (id, process_id, version_number, status, definition, released_at, note, created_at, updated_at)
        VALUES (@Id, @ProcessId, @Number, @Status, @Definition::jsonb, @ReleasedAt, @Note, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateVersion = """
        UPDATE core.process_version
        SET status = @Status, definition = @Definition::jsonb, released_at = @ReleasedAt, note = @Note, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertOperation = """
        INSERT INTO core.process_operation (id, process_version_id, operation_number, name, description)
        VALUES (@Id, @VersionId, @Number, @Name, @Description)
        """;

    private const string InsertOperationEquipment = """
        INSERT INTO core.process_operation_equipment (operation_id, equipment_id, share_percent)
        VALUES (@OperationId, @EquipmentId, @SharePercent)
        """;

    private const string InsertDependency = """
        INSERT INTO core.process_dependency (id, from_operation_id, to_operation_id, dependency_type, lag)
        VALUES (@Id, @FromOperationId, @ToOperationId, @Type, @Lag)
        """;

    private const string SetAllMaterials = """
        UPDATE core.process SET makes_all_materials = @AllMaterials, updated_at = @At
        WHERE id = @ProcessId
        RETURNING xmin::text::bigint
        """;

    // Rows leaving the scope are switched off; rows joining it are added or switched back on.
    private const string SyncClasses = """
        UPDATE core.process_material_class SET is_active = false, updated_at = @At
        WHERE process_id = @ProcessId AND is_active AND NOT (material_class_id = ANY(@Ids));
        INSERT INTO core.process_material_class (process_id, material_class_id, is_active, updated_at)
        SELECT @ProcessId, id, true, @At FROM unnest(@Ids) AS id
        ON CONFLICT (process_id, material_class_id) DO UPDATE SET is_active = true, updated_at = excluded.updated_at
        WHERE NOT core.process_material_class.is_active;
        """;

    private const string SyncMaterials = """
        UPDATE core.process_material SET is_active = false, updated_at = @At
        WHERE process_id = @ProcessId AND is_active AND NOT (material_id = ANY(@Ids));
        INSERT INTO core.process_material (process_id, material_id, is_active, updated_at)
        SELECT @ProcessId, id, true, @At FROM unnest(@Ids) AS id
        ON CONFLICT (process_id, material_id) DO UPDATE SET is_active = true, updated_at = excluded.updated_at
        WHERE NOT core.process_material.is_active;
        """;

    private const string CountExisting = """
        SELECT (SELECT count(*)::int FROM core.material_class WHERE id = ANY(@ClassIds)) AS classes,
               (SELECT count(*)::int FROM core.material WHERE id = ANY(@MaterialIds)) AS materials
        """;

    private const string CoversMaterial = $"""
        SELECT {ProcessQueries.Covers} FROM core.process p WHERE p.id = @ProcessId
        """;

    private const string SetMaterialDefault = """
        UPDATE core.material
        SET default_process_id = @ProcessId, updated_at = @At
        WHERE id = @MaterialId
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<Process>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<ProcessRow>(GetForUpdate, new { Id = id }, cancellationToken);
        return row is null ? null : new(new Process(row.Id, row.SiteId, row.Code, row.Name, row.Description, row.IsActive), row.Version);
    }

    public Task<long> InsertAsync(Process process, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            Insert, new { process.Id, process.SiteId, process.Code, process.Name, process.Description, process.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateAsync(Process process, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, new { process.Id, process.Name, process.Description, process.IsActive, At = at }, cancellationToken);

    public async Task<Versioned<ProcessVersion>?> GetVersionForUpdateAsync(Guid processId, Guid versionId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<VersionRow>(
            GetVersionForUpdate, new { ProcessId = processId, VersionId = versionId }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<ProcessVersion>?> GetReleasedForUpdateAsync(Guid processId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<VersionRow>(GetReleasedForUpdate, new { ProcessId = processId }, cancellationToken))?.ToVersioned();

    public Task<bool> HasDraftAsync(Guid processId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(HasDraft, new { ProcessId = processId }, cancellationToken);

    public Task<long> InsertVersionAsync(ProcessVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertVersion, VersionArgs(version, at), cancellationToken);

    public Task<long> UpdateVersionAsync(ProcessVersion version, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateVersion, VersionArgs(version, at), cancellationToken);

    public async Task InsertReleasedRowsAsync(ProcessVersion version, CancellationToken cancellationToken)
    {
        foreach (var op in version.Definition.Operations)
        {
            await session.ExecuteAsync(
                InsertOperation, new { op.Id, VersionId = version.Id, op.Number, op.Name, op.Description }, cancellationToken);
            foreach (var use in op.Equipment)
            {
                await session.ExecuteAsync(
                    InsertOperationEquipment, new { OperationId = op.Id, use.EquipmentId, use.SharePercent }, cancellationToken);
            }
        }

        foreach (var link in version.Definition.Dependencies)
        {
            await session.ExecuteAsync(InsertDependency, link, cancellationToken);
        }
    }

    public Task<IReadOnlyList<ProcessEquipmentRow>> GetEquipmentAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        session.QueryAsync<ProcessEquipmentRow>(ProcessQueries.Equipment, new { Ids = ids.ToArray() }, cancellationToken);

    public async Task<long> SetScopeAsync(Guid processId, ProcessScope scope, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync(SyncClasses, new { ProcessId = processId, Ids = scope.MaterialClassIds.ToArray(), At = at }, cancellationToken);
        await session.ExecuteAsync(SyncMaterials, new { ProcessId = processId, Ids = scope.MaterialIds.ToArray(), At = at }, cancellationToken);
        return await session.ExecuteScalarAsync<long>(
            SetAllMaterials, new { ProcessId = processId, scope.AllMaterials, At = at }, cancellationToken);
    }

    public async Task<(int Classes, int Materials)> CountExistingAsync(
        IReadOnlyCollection<Guid> classIds, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<CountRow>(
            CountExisting, new { ClassIds = classIds.ToArray(), MaterialIds = materialIds.ToArray() }, cancellationToken);
        return (row!.Classes, row.Materials);
    }

    public Task<bool> CoversAsync(Guid processId, Guid materialId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CoversMaterial, new { ProcessId = processId, MaterialId = materialId }, cancellationToken);

    public Task<long?> SetMaterialDefaultAsync(Guid materialId, Guid? processId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<long?>(SetMaterialDefault, new { MaterialId = materialId, ProcessId = processId, At = at }, cancellationToken);

    private static object VersionArgs(ProcessVersion version, DateTimeOffset at) => new
    {
        version.Id,
        version.ProcessId,
        version.Number,
        version.Status,
        Definition = ProcessJson.Write(version.Definition),
        version.ReleasedAt,
        version.Note,
        At = at,
    };

    private sealed class CountRow
    {
        public int Classes { get; init; }
        public int Materials { get; init; }
    }

    private sealed class ProcessRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class VersionRow
    {
        public Guid Id { get; init; }
        public Guid ProcessId { get; init; }
        public int Number { get; init; }
        public string Status { get; init; } = "";
        public string DefinitionJson { get; init; } = "";
        public DateTimeOffset? ReleasedAt { get; init; }
        public string? Note { get; init; }
        public long Version { get; init; }

        public Versioned<ProcessVersion> ToVersioned() => new(
            new ProcessVersion(Id, ProcessId, Number, Status, ProcessJson.Read<ProcessDefinition>(DefinitionJson), ReleasedAt, Note),
            Version);
    }
}
