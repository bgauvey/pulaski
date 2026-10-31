using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Scheduling;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Scheduling;

/// <summary>Write side for changeover rules (D-074): the site's row is locked first, then its rules are read.</summary>
internal sealed class ChangeoverRuleRepository(DbSession session) : IChangeoverRuleRepository
{
    private const string LockSite = "SELECT id FROM core.equipment WHERE id = @Id AND level = 'site' FOR NO KEY UPDATE";

    private const string SiteOfRule = "SELECT site_id FROM core.changeover_rule WHERE id = @Id";

    private const string SiteRules = """
        SELECT id, site_id, equipment_id, from_material_id, from_material_class_id, to_material_id, to_material_class_id, duration, note, is_active,
               xmin::text::bigint AS version
        FROM core.changeover_rule WHERE site_id = @SiteId
        ORDER BY id
        """;

    private const string Lines = """
        SELECT lower(code) AS code, id FROM core.equipment WHERE site_id = @SiteId AND can_run_job AND is_active AND level <> 'site'
        """;

    private const string Materials = "SELECT lower(code) AS code, id FROM core.material WHERE lower(code) = ANY(@Codes)";

    private const string Classes = "SELECT lower(code) AS code, id FROM core.material_class WHERE lower(code) = ANY(@Codes)";

    private const string ExistingMaterials = "SELECT id FROM core.material WHERE id = ANY(@Ids)";

    private const string ExistingClasses = "SELECT id FROM core.material_class WHERE id = ANY(@Ids)";

    private const string Insert = """
        INSERT INTO core.changeover_rule (id, site_id, equipment_id, from_material_id, from_material_class_id, to_material_id, to_material_class_id,
                                          duration, note, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @EquipmentId, @FromMaterialId, @FromMaterialClassId, @ToMaterialId, @ToMaterialClassId, @Duration, @Note, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.changeover_rule SET duration = @Duration, note = @Note, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<bool> LockSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Guid?>(LockSite, new { Id = siteId }, cancellationToken) is not null;

    public Task<Guid?> SiteOfRuleAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(SiteOfRule, new { Id = id }, cancellationToken);

    public async Task<IReadOnlyList<Versioned<ChangeoverRuleRecord>>> ListSiteRulesAsync(Guid siteId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<RuleRow>(SiteRules, new { SiteId = siteId }, cancellationToken))
            .Select(r => new Versioned<ChangeoverRuleRecord>(
                new(r.Id, r.SiteId, new(r.EquipmentId, r.FromMaterialId, r.FromMaterialClassId, r.ToMaterialId, r.ToMaterialClassId), r.Duration, r.Note, r.IsActive),
                r.Version))];

    public async Task<IReadOnlyDictionary<string, Guid>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken) =>
        (await session.QueryAsync<CodeRow>(Lines, new { SiteId = siteId }, cancellationToken)).ToDictionary(r => r.Code, r => r.Id);

    public async Task<IReadOnlyDictionary<string, Guid>> FindMaterialsAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        codes.Count == 0 ? new Dictionary<string, Guid>()
            : (await session.QueryAsync<CodeRow>(Materials, new { Codes = codes.ToArray() }, cancellationToken)).ToDictionary(r => r.Code, r => r.Id);

    public async Task<IReadOnlyDictionary<string, Guid>> FindClassesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken) =>
        codes.Count == 0 ? new Dictionary<string, Guid>()
            : (await session.QueryAsync<CodeRow>(Classes, new { Codes = codes.ToArray() }, cancellationToken)).ToDictionary(r => r.Code, r => r.Id);

    public async Task<(IReadOnlySet<Guid> Materials, IReadOnlySet<Guid> Classes)> ExistingAsync(
        IReadOnlyCollection<Guid> materialIds, IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken)
    {
        var materials = materialIds.Count == 0 ? [] : (await session.QueryAsync<Guid>(ExistingMaterials, new { Ids = materialIds.ToArray() }, cancellationToken)).ToHashSet();
        var classes = classIds.Count == 0 ? [] : (await session.QueryAsync<Guid>(ExistingClasses, new { Ids = classIds.ToArray() }, cancellationToken)).ToHashSet();
        return (materials, classes);
    }

    public Task<long> InsertAsync(ChangeoverRuleRecord rule, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(rule, at), cancellationToken);

    public Task<long> UpdateAsync(ChangeoverRuleRecord rule, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(rule, at), cancellationToken);

    private static object Args(ChangeoverRuleRecord r, DateTimeOffset at) => new
    {
        r.Id,
        r.SiteId,
        r.Pattern.EquipmentId,
        r.Pattern.FromMaterialId,
        r.Pattern.FromMaterialClassId,
        r.Pattern.ToMaterialId,
        r.Pattern.ToMaterialClassId,
        r.Duration,
        r.Note,
        r.IsActive,
        At = at,
    };

    private sealed class RuleRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid? EquipmentId { get; init; }
        public Guid? FromMaterialId { get; init; }
        public Guid? FromMaterialClassId { get; init; }
        public Guid? ToMaterialId { get; init; }
        public Guid? ToMaterialClassId { get; init; }
        public TimeSpan Duration { get; init; }
        public string? Note { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class CodeRow
    {
        public string Code { get; init; } = "";
        public Guid Id { get; init; }
    }
}

/// <summary>Read side for changeover rules: the site's, with the codes and names they show.</summary>
internal sealed class ChangeoverRuleQueries(NpgsqlDataSource dataSource) : IChangeoverRuleReadStore
{
    private const string List = """
        SELECT r.id, r.site_id, r.equipment_id, e.code AS equipment_code,
               r.from_material_id, fm.code AS from_material_code, fm.name AS from_material_name, r.from_material_class_id, fc.code AS from_material_class_code,
               r.to_material_id, tm.code AS to_material_code, tm.name AS to_material_name, r.to_material_class_id, tc.code AS to_material_class_code,
               (extract(epoch FROM r.duration) / 60)::int AS duration_minutes, r.note, r.is_active, r.xmin::text::bigint AS version
        FROM core.changeover_rule r
        LEFT JOIN core.equipment e ON e.id = r.equipment_id
        LEFT JOIN core.material fm ON fm.id = r.from_material_id
        LEFT JOIN core.material_class fc ON fc.id = r.from_material_class_id
        LEFT JOIN core.material tm ON tm.id = r.to_material_id
        LEFT JOIN core.material_class tc ON tc.id = r.to_material_class_id
        WHERE r.site_id = @SiteId
        ORDER BY NOT r.is_active, e.code NULLS FIRST, coalesce(fm.code, fc.code) NULLS FIRST, coalesce(tm.code, tc.code) NULLS FIRST, r.id
        """;

    public async Task<IReadOnlyList<ChangeoverRuleDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<ChangeoverRuleDto>(new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }
}
