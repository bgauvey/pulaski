using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Maintenance;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Maintenance;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Maintenance;

/// <summary>Preventive plans' writes and meters (#127d, D-115), in the command's transaction.</summary>
internal sealed class MaintenancePlanRepository(DbSession session) : IMaintenancePlanRepository
{
    private const string Columns = "id, site_id, equipment_id, name, meter, interval_value AS interval, priority, is_active, started_at, xmin::text::bigint AS version";

    public async Task<Versioned<MaintenancePlan>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<PlanRow>($"SELECT {Columns} FROM core.maintenance_plan WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken) is { } r
            ? new Versioned<MaintenancePlan>(r.ToDomain(), r.Version)
            : null;

    public Task<long> InsertAsync(MaintenancePlan plan, DateTimeOffset at, string by, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            INSERT INTO core.maintenance_plan (id, site_id, equipment_id, name, meter, interval_value, priority, is_active, started_at, created_at, created_by, updated_at)
            VALUES (@Id, @SiteId, @EquipmentId, @Name, @Meter, @Interval, @Priority, @IsActive, @StartedAt, @At, @By, @At)
            RETURNING xmin::text::bigint
            """,
            new { plan.Id, plan.SiteId, plan.EquipmentId, plan.Name, plan.Meter, plan.Interval, plan.Priority, plan.IsActive, plan.StartedAt, At = at, By = by },
            cancellationToken);

    public Task<long> UpdateAsync(MaintenancePlan plan, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            """
            UPDATE core.maintenance_plan
            SET name = @Name, interval_value = @Interval, priority = @Priority, is_active = @IsActive, started_at = @StartedAt, updated_at = @At
            WHERE id = @Id
            RETURNING xmin::text::bigint
            """,
            new { plan.Id, plan.Name, plan.Interval, plan.Priority, plan.IsActive, plan.StartedAt, At = at },
            cancellationToken);

    public Task<PlanLastRequest?> GetLastRequestAsync(Guid planId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<PlanLastRequest>(
            "SELECT id, status, closed_at FROM core.maintenance_request WHERE maintenance_plan_id = @Id ORDER BY raised_at DESC LIMIT 1",
            new { Id = planId }, cancellationToken);

    private sealed class PlanRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid EquipmentId { get; init; }
        public string Name { get; init; } = "";
        public string Meter { get; init; } = "";
        public decimal Interval { get; init; }
        public string Priority { get; init; } = "";
        public bool IsActive { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public long Version { get; init; }

        public MaintenancePlan ToDomain() => new(Id, SiteId, EquipmentId, Name, Meter, Interval, Priority, IsActive, StartedAt);
    }
}

/// <summary>Preventive plans' reads (#127d, D-115).</summary>
internal sealed class MaintenancePlanQueries(NpgsqlDataSource dataSource) : IMaintenancePlanReadStore
{
    private const string Plans = """
        SELECT p.id, p.site_id, p.equipment_id, e.code AS equipment_code, p.name, p.meter, p.interval_value AS interval, p.priority, p.is_active,
               p.started_at, p.xmin::text::bigint AS version,
               last.id AS last_request_id, last.number AS last_request_number, last.status AS last_request_status, last.closed_at AS last_request_closed_at
        FROM core.maintenance_plan p
        JOIN core.equipment e ON e.id = p.equipment_id
        LEFT JOIN LATERAL (
            SELECT r.id, r.number, r.status, r.closed_at FROM core.maintenance_request r
            WHERE r.maintenance_plan_id = p.id ORDER BY r.raised_at DESC LIMIT 1) last ON true
        WHERE p.site_id = @SiteId
        ORDER BY p.is_active DESC, e.code, p.name
        """;

    // Hours executing (PackML 6) in [from, until): the state in effect at from, then each change, each lasting to the next (or until).
    private const string RunHours = """
        WITH states AS (
            (SELECT occurred_at, packml_state FROM ts.equipment_state_change
             WHERE equipment_id = @EquipmentId AND occurred_at <= @From ORDER BY occurred_at DESC LIMIT 1)
            UNION ALL
            SELECT occurred_at, packml_state FROM ts.equipment_state_change
            WHERE equipment_id = @EquipmentId AND occurred_at > @From AND occurred_at < @Until),
        spans AS (
            SELECT greatest(occurred_at, @From) AS since, lead(occurred_at, 1, @Until) OVER (ORDER BY occurred_at) AS until, packml_state FROM states)
        SELECT coalesce(sum(extract(epoch FROM (until - since))), 0) / 3600.0
        FROM spans WHERE packml_state = 6 AND until > since
        """;

    // What the machine counted (processed) in (from, until].
    private const string Counts = """
        SELECT coalesce(sum(delta_qty), 0) FROM ts.production_count
        WHERE equipment_id = @EquipmentId AND source = 'machine' AND count_kind = 'processed' AND occurred_at > @From AND occurred_at <= @Until
        """;

    public async Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<Guid>(new CommandDefinition("SELECT id FROM core.maintenance_plan WHERE is_active ORDER BY id", cancellationToken: cancellationToken))];
    }

    public async Task<decimal> ReadMeterAsync(Guid equipmentId, string meter, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
            meter == MaintenanceMeters.RunHours ? RunHours : Counts, new { EquipmentId = equipmentId, From = from, Until = until }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<MaintenancePlanDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<MaintenancePlanDto>(new CommandDefinition(Plans, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }
}
