using Dapper;
using Npgsql;
using Pulaski.Application.Maintenance;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Maintenance;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Maintenance;

/// <summary>Maintenance requests' writes (#127, D-115), in the command's transaction.</summary>
internal sealed class MaintenanceRepository(DbSession session) : IMaintenanceRepository
{
    private const string Columns = """
        id, site_id, equipment_id, number, status, priority, description, raised_at, raised_by, raised_from, downtime_id, downtime_started_at,
        started_at, started_by, closed_at, closed_by, resolution, external_system, external_reference, maintenance_plan_id AS plan_id
        """;

    public Task<MaintenanceEquipment?> LockEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<MaintenanceEquipment>(
            "SELECT id, site_id, code, level, is_active FROM core.equipment WHERE id = @Id FOR NO KEY UPDATE", new { Id = equipmentId }, cancellationToken);

    public Task<MaintenanceStop?> GetStopAsync(Guid id, DateTimeOffset startedAt, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<MaintenanceStop>(
            "SELECT id, started_at, equipment_id FROM ts.downtime WHERE id = @Id AND started_at = @StartedAt AND superseded_at IS NULL",
            new { Id = id, StartedAt = startedAt }, cancellationToken);

    public Task<MaintenanceRequest?> FindOpenForStopForUpdateAsync(Guid downtimeId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<MaintenanceRequest>(
            $"SELECT {Columns} FROM core.maintenance_request WHERE downtime_id = @Id AND status <> 'closed' FOR UPDATE", new { Id = downtimeId }, cancellationToken);

    public Task<long> NextNumberAsync(CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>("SELECT nextval('core.maintenance_request_no')", new { }, cancellationToken);

    public Task<MaintenanceRequest?> FindByNumberForUpdateAsync(string number, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<MaintenanceRequest>(
            $"SELECT {Columns} FROM core.maintenance_request WHERE number = @Number FOR UPDATE", new { Number = number }, cancellationToken);

    public Task<MaintenanceRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<MaintenanceRequest>($"SELECT {Columns} FROM core.maintenance_request WHERE id = @Id FOR UPDATE", new { Id = id }, cancellationToken);

    public Task InsertAsync(MaintenanceRequest request, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.maintenance_request (id, site_id, equipment_id, number, status, priority, description, raised_at, raised_by, raised_from,
                                                  downtime_id, downtime_started_at, maintenance_plan_id, created_at, updated_at)
            VALUES (@Id, @SiteId, @EquipmentId, @Number, @Status, @Priority, @Description, @RaisedAt, @RaisedBy, @RaisedFrom,
                    @DowntimeId, @DowntimeStartedAt, @PlanId, @At, @At)
            """,
            new
            {
                request.Id,
                request.SiteId,
                request.EquipmentId,
                request.Number,
                request.Status,
                request.Priority,
                request.Description,
                request.RaisedAt,
                request.RaisedBy,
                request.RaisedFrom,
                request.DowntimeId,
                request.DowntimeStartedAt,
                request.PlanId,
                At = at,
            },
            cancellationToken);

    public Task UpdateAsync(MaintenanceRequest request, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            UPDATE core.maintenance_request
            SET status = @Status, started_at = @StartedAt, started_by = @StartedBy, closed_at = @ClosedAt, closed_by = @ClosedBy, resolution = @Resolution,
                external_system = @ExternalSystem, external_reference = @ExternalReference, updated_at = @At
            WHERE id = @Id
            """,
            new
            {
                request.Id,
                request.Status,
                request.StartedAt,
                request.StartedBy,
                request.ClosedAt,
                request.ClosedBy,
                request.Resolution,
                request.ExternalSystem,
                request.ExternalReference,
                At = at,
            },
            cancellationToken);
}

/// <summary>Maintenance requests' reads (#127, D-115).</summary>
internal sealed class MaintenanceQueries(NpgsqlDataSource dataSource) : IMaintenanceReadStore
{
    // A request with its equipment, the stop it came from (and that stop's reason) and the names of the users who raised, started and closed it.
    private const string Requests = """
        SELECT r.id, r.site_id, r.number, r.equipment_id, e.code AS equipment_code, e.name AS equipment_name, line.id AS line_id, line.code AS line_code,
               r.status, r.priority, r.description,
               r.raised_at, r.raised_by, r.raised_from, r.downtime_id, r.downtime_started_at, d.ended_at AS downtime_ended_at,
               rc.code || ' · ' || rc.name AS downtime_reason,
               r.started_at, r.started_by, r.closed_at, r.closed_by, r.resolution, r.external_system, r.external_reference, r.maintenance_plan_id AS plan_id,
               (SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = r.raised_by) AS raised_by_name,
               (SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = r.started_by) AS started_by_name,
               (SELECT u.display_name FROM sec.app_user u WHERE (CASE WHEN u.idp_issuer = 'pulaski-local' THEN u.idp_subject ELSE u.idp_issuer || '|' || u.idp_subject END) = r.closed_by) AS closed_by_name
        FROM core.maintenance_request r
        JOIN core.equipment e ON e.id = r.equipment_id
        LEFT JOIN ts.downtime d ON d.id = r.downtime_id AND d.started_at = r.downtime_started_at
        LEFT JOIN core.reason_code rc ON rc.id = d.effective_reason_code_id
        LEFT JOIN LATERAL (
            SELECT a.id, a.code
            FROM core.equipment_closure c
            JOIN core.equipment a ON a.id = c.ancestor_id
            WHERE c.descendant_id = r.equipment_id AND a.can_run_job
            ORDER BY c.depth
            LIMIT 1) line ON true
        WHERE r.site_id = @SiteId
          AND (@EquipmentId::uuid IS NULL
               OR EXISTS (SELECT 1 FROM core.equipment_closure c WHERE c.ancestor_id = @EquipmentId AND c.descendant_id = r.equipment_id))
          AND (CASE WHEN @OpenOnly THEN r.status <> 'closed'
                    ELSE (@From::timestamptz IS NULL OR r.raised_at >= @From) AND (@To::timestamptz IS NULL OR r.raised_at < @To) END)
        ORDER BY r.raised_at DESC
        LIMIT 1000
        """;

    public async Task<IReadOnlyList<MaintenanceRequestDto>> ListAsync(
        Guid siteId, bool openOnly, DateTimeOffset? from, DateTimeOffset? until, Guid? equipmentId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<MaintenanceRequestDto>(new CommandDefinition(
            Requests, new { SiteId = siteId, OpenOnly = openOnly, From = from, To = until, EquipmentId = equipmentId }, cancellationToken: cancellationToken))];
    }
}
