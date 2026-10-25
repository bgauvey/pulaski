using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Materials;

/// <summary>Material connections (D-068), in the command's transaction.</summary>
internal sealed class MaterialConnectionStore(DbSession session) : IMaterialConnectionStore
{
    private const string Columns = """
        id, code, name, kind, base_url, entity_set, filter, external_system, field_map::text AS field_map, is_active, xmin::text::bigint AS version
        """;

    private const string Insert = """
        INSERT INTO core.material_connection (id, code, name, kind, base_url, entity_set, filter, external_system, field_map, is_active, created_at, updated_at)
        VALUES (@Id, @Code, @Name, @Kind, @BaseUrl, @EntitySet, @Filter, @ExternalSystem, @FieldMap::jsonb, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.material_connection
        SET name = @Name, base_url = @BaseUrl, entity_set = @EntitySet, filter = @Filter, external_system = @ExternalSystem,
            field_map = @FieldMap::jsonb, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<IReadOnlyList<Versioned<MaterialConnection>>> ListAsync(CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<Row>($"SELECT {Columns} FROM core.material_connection ORDER BY code", null, cancellationToken)).Select(r => r.ToVersioned())];

    public async Task<Versioned<MaterialConnection>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<Row>($"SELECT {Columns} FROM core.material_connection WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken))?.ToVersioned();

    public Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM core.material_connection WHERE code = @Code)", new { Code = code }, cancellationToken);

    public Task<long> InsertAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(connection, at), cancellationToken);

    public Task<long> UpdateAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(connection, at), cancellationToken);

    public Task RecordRunAsync(Guid id, string actor, string summary, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            "UPDATE core.material_connection SET last_run_at = @At, last_run_by = @Actor, last_run_summary = @Summary WHERE id = @Id",
            new { Id = id, Actor = actor, Summary = summary.Length <= 1000 ? summary : summary[..1000], At = at }, cancellationToken);

    public async Task<IReadOnlyDictionary<Guid, (DateTimeOffset At, string? By, string? Summary)>> LastRunsAsync(CancellationToken cancellationToken) =>
        (await session.QueryAsync<RunRow>(
            "SELECT id, last_run_at, last_run_by, last_run_summary FROM core.material_connection WHERE last_run_at IS NOT NULL", null, cancellationToken))
        .ToDictionary(r => r.Id, r => (r.LastRunAt, r.LastRunBy, r.LastRunSummary));

    private static object Args(MaterialConnection c, DateTimeOffset at) => new
    {
        c.Id,
        c.Code,
        c.Name,
        c.Kind,
        c.BaseUrl,
        c.EntitySet,
        c.Filter,
        c.ExternalSystem,
        FieldMap = JsonSerializer.Serialize(c.FieldMap),
        c.IsActive,
        At = at,
    };

    private sealed class Row
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Kind { get; init; } = "";
        public string BaseUrl { get; init; } = "";
        public string EntitySet { get; init; } = "";
        public string? Filter { get; init; }
        public string ExternalSystem { get; init; } = "";
        public string FieldMap { get; init; } = "{}";
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public Versioned<MaterialConnection> ToVersioned() => new(
            new MaterialConnection(Id, Code, Name, Kind, BaseUrl, EntitySet, Filter, ExternalSystem,
                JsonSerializer.Deserialize<Dictionary<string, string>>(FieldMap) ?? [], IsActive),
            Version);
    }

    private sealed class RunRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset LastRunAt { get; init; }
        public string? LastRunBy { get; init; }
        public string? LastRunSummary { get; init; }
    }
}
