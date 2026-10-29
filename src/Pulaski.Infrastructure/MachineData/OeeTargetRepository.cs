using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.MachineData;

/// <summary>Columns of an OEE target (D-073), shared by its reads and writes.</summary>
internal static class OeeTargetSql
{
    public const string Columns = """
        t.id, t.site_id, t.equipment_id, t.effective_from, t.effective_to, t.availability_pct, t.performance_pct, t.quality_pct, t.oee_pct,
        t.xmin::text::bigint AS version
        """;

    public const string OfSite = $"SELECT {Columns} FROM core.oee_target t WHERE t.site_id = @SiteId";

    internal sealed class Row
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid? EquipmentId { get; init; }
        public DateOnly EffectiveFrom { get; init; }
        public DateOnly? EffectiveTo { get; init; }
        public decimal? AvailabilityPct { get; init; }
        public decimal? PerformancePct { get; init; }
        public decimal? QualityPct { get; init; }
        public decimal? OeePct { get; init; }
        public long Version { get; init; }
        public string? EquipmentCode { get; init; }
        public string? EquipmentName { get; init; }

        public OeeTarget ToDomain() =>
            new(Id, SiteId, EquipmentId, EffectiveFrom, EffectiveTo, new OeeTargetValues(AvailabilityPct, PerformancePct, QualityPct, OeePct));
    }
}

/// <summary>Write side for OEE targets (D-073): a scope is locked through its site's or equipment's row, then its periods are read.</summary>
internal sealed class OeeTargetRepository(DbSession session) : IOeeTargetRepository
{
    private const string LockSite = "SELECT id FROM core.equipment WHERE id = @SiteId AND level = 'site' FOR NO KEY UPDATE";

    private const string LockEquipment = """
        SELECT id FROM core.equipment WHERE id = @EquipmentId AND site_id = @SiteId AND tracks_state AND level <> 'site' FOR NO KEY UPDATE
        """;

    private const string Scope = $"""
        {OeeTargetSql.OfSite} AND t.equipment_id IS NOT DISTINCT FROM @EquipmentId::uuid
        ORDER BY t.effective_from
        """;

    private const string Insert = """
        INSERT INTO core.oee_target (id, site_id, equipment_id, effective_from, effective_to, availability_pct, performance_pct, quality_pct, oee_pct,
                                     created_at, updated_at)
        VALUES (@Id, @SiteId, @EquipmentId, @EffectiveFrom, @EffectiveTo, @AvailabilityPct, @PerformancePct, @QualityPct, @OeePct, @At, @At)
        """;

    private const string Update = """
        UPDATE core.oee_target
        SET effective_to = @EffectiveTo, availability_pct = @AvailabilityPct, performance_pct = @PerformancePct, quality_pct = @QualityPct,
            oee_pct = @OeePct, updated_at = @At
        WHERE id = @Id
        """;

    public async Task<bool> LockScopeAsync(Guid siteId, Guid? equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<Guid?>(equipmentId is null ? LockSite : LockEquipment, new { SiteId = siteId, EquipmentId = equipmentId }, cancellationToken)
            is not null;

    public async Task<IReadOnlyList<Versioned<OeeTarget>>> ListScopeAsync(Guid siteId, Guid? equipmentId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<OeeTargetSql.Row>(Scope, new { SiteId = siteId, EquipmentId = equipmentId }, cancellationToken))
            .Select(r => new Versioned<OeeTarget>(r.ToDomain(), r.Version))];

    public Task InsertAsync(OeeTarget target, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Insert, Args(target, at), cancellationToken);

    public Task UpdateAsync(OeeTarget target, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Update, Args(target, at), cancellationToken);

    private static object Args(OeeTarget t, DateTimeOffset at) => new
    {
        t.Id,
        t.SiteId,
        t.EquipmentId,
        t.EffectiveFrom,
        t.EffectiveTo,
        t.Values.AvailabilityPct,
        t.Values.PerformancePct,
        t.Values.QualityPct,
        t.Values.OeePct,
        At = at,
    };
}

/// <summary>Read side for OEE targets (D-073).</summary>
internal sealed class OeeTargetQueries(NpgsqlDataSource dataSource) : IOeeTargetReadStore
{
    // The site's first, then equipment by code; each scope newest first.
    private const string List = $"""
        SELECT {OeeTargetSql.Columns}, e.code AS equipment_code, e.name AS equipment_name
        FROM core.oee_target t LEFT JOIN core.equipment e ON e.id = t.equipment_id
        WHERE t.site_id = @SiteId
        ORDER BY t.equipment_id IS NOT NULL, e.code, t.effective_from DESC
        """;

    public async Task<IReadOnlyList<OeeTargetDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<OeeTargetSql.Row>(new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new OeeTargetDto
        {
            Id = r.Id,
            SiteId = r.SiteId,
            EquipmentId = r.EquipmentId,
            EquipmentCode = r.EquipmentCode,
            EquipmentName = r.EquipmentName,
            EffectiveFrom = r.EffectiveFrom,
            EffectiveTo = r.EffectiveTo,
            Targets = new OeeTargetFiguresDto(r.AvailabilityPct, r.PerformancePct, r.QualityPct, r.OeePct),
            Version = r.Version,
        })];
    }
}
