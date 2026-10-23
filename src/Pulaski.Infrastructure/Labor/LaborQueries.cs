using Dapper;
using Npgsql;
using Pulaski.Application.Labor;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Shifts;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.Labor;

/// <summary>Read side for labor (D-040).</summary>
internal sealed class LaborQueries(NpgsqlDataSource dataSource) : ILaborReadStore
{
    private const string Crews = """
        SELECT c.id, c.site_id, c.code, c.name, c.is_active, c.xmin::text::bigint AS version,
               (SELECT count(*)::int FROM core.person p WHERE p.crew_id = c.id AND p.is_active) AS members
        FROM core.crew c
        WHERE c.site_id = @SiteId
        ORDER BY c.is_active DESC, c.code
        """;

    private const string PersonSelect = """
        SELECT p.id, p.site_id, p.employee_number, p.display_name, p.crew_id, c.code AS crew_code, u.username, p.is_active,
               p.xmin::text::bigint AS version
        FROM core.person p
        LEFT JOIN core.crew c ON c.id = p.crew_id
        LEFT JOIN sec.app_user u ON u.id = p.app_user_id
        """;

    private const string People = $"""
        {PersonSelect}
        WHERE p.site_id = @SiteId
        ORDER BY p.is_active DESC, p.display_name, p.employee_number
        """;

    private const string PersonOfUser = $"{PersonSelect} WHERE p.app_user_id = @UserId";

    // The lines labor is recorded on: what runs jobs, with the job running now and its standard crew.
    private const string Lines = """
        SELECT e.id, e.code, e.name, j.id AS running_job_id, j.job_number AS running_job_number, cap.standard_crew_size
        FROM core.equipment e
        LEFT JOIN core.job_run r ON r.equipment_id = e.id AND r.ended_at IS NULL
        LEFT JOIN core.job j ON j.id = r.job_id
        LEFT JOIN LATERAL (
            SELECT ec.standard_crew_size FROM core.equipment_capability ec
            WHERE ec.equipment_id = e.id AND ec.material_id = j.material_id
              AND ec.effective_from <= now() AND (ec.effective_to IS NULL OR ec.effective_to > now())
            LIMIT 1
        ) cap ON true
        WHERE e.site_id = @SiteId AND e.can_run_job AND e.is_active
        ORDER BY e.code
        """;

    private const string TimeZone = "SELECT time_zone FROM core.equipment WHERE id = @Id AND level = 'site'";

    private const string Open = $"""
        SELECT {LaborSql.RowColumns}
        {LaborSql.RowJoins}
        WHERE s.site_id = @SiteId AND s.ended_at IS NULL AND (@PersonId::uuid IS NULL OR s.person_id = @PersonId)
          AND NOT s.is_void AND NOT EXISTS (SELECT 1 FROM core.labor_session c WHERE c.corrects_session_id = s.id)
        ORDER BY e.code, p.display_name
        """;

    private const string InRange = $"""
        SELECT {LaborSql.RowColumns}
        {LaborSql.RowJoins}
        WHERE s.site_id = @SiteId AND s.production_date BETWEEN @First AND @Last
        ORDER BY s.started_at, s.id
        """;

    // Runs at the site that overlap the window, with the standard crew size in effect when each started.
    private const string Runs = """
        SELECT r.id AS run_id, r.job_id, j.job_number, m.code AS material_code, r.equipment_id, e.code AS line_code, r.started_at, r.ended_at,
               cap.standard_crew_size
        FROM core.job_run r
        JOIN core.job j ON j.id = r.job_id
        JOIN core.equipment e ON e.id = r.equipment_id
        LEFT JOIN core.material m ON m.id = j.material_id
        LEFT JOIN LATERAL (
            SELECT ec.standard_crew_size FROM core.equipment_capability ec
            WHERE ec.equipment_id = r.equipment_id AND ec.material_id = j.material_id
              AND ec.effective_from <= r.started_at AND (ec.effective_to IS NULL OR ec.effective_to > r.started_at)
            LIMIT 1
        ) cap ON true
        WHERE j.site_id = @SiteId AND r.started_at < @End AND (r.ended_at IS NULL OR r.ended_at > @Start)
        ORDER BY r.started_at
        """;

    private const string Approvals = """
        SELECT site_id, production_date, shift_schedule_id, shift_code, approved_by, approved_at
        FROM core.labor_approval
        WHERE site_id = @SiteId AND production_date BETWEEN @First AND @Last
        """;

    private const string Schedules = "SELECT id, code, name, cycle_days, cycle_start, shifts::text AS shifts_json, overrides::text AS overrides_json FROM core.shift_schedule WHERE site_id = @SiteId";

    private const string CrewNames = "SELECT code, name FROM core.crew WHERE site_id = @SiteId";

    public async Task<IReadOnlyList<CrewDto>> ListCrewsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<CrewDto>(new CommandDefinition(Crews, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }

    public async Task<IReadOnlyList<PersonDto>> ListPeopleAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<PersonDto>(new CommandDefinition(People, new { SiteId = siteId }, cancellationToken: cancellationToken))];
    }

    public async Task<PersonDto?> GetPersonOfUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<PersonDto>(new CommandDefinition(PersonOfUser, new { UserId = userId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<LaborLineRow>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<LineRow>(new CommandDefinition(Lines, new { SiteId = siteId }, cancellationToken: cancellationToken)))
            .Select(r => new LaborLineRow(r.Id, r.Code, r.Name, r.RunningJobId, r.RunningJobNumber, r.StandardCrewSize))];
    }

    public async Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0)
        {
            return [];
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<ShiftSql.CalendarRow>(new CommandDefinition(ShiftSql.EquipmentCalendars, new { Ids = equipmentIds.ToArray() }, cancellationToken: cancellationToken)))
            .Select(LaborSql.ToShifts)];
    }

    public async Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(TimeZone, new { Id = siteId }, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<SessionRow>> ListOpenSessionsAsync(Guid siteId, Guid? personId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<LaborSql.SessionViewRow>(new CommandDefinition(Open, new { SiteId = siteId, PersonId = personId }, cancellationToken: cancellationToken)))
            .Select(r => r.ToRow())];
    }

    public async Task<IReadOnlyList<SessionRow>> ListSessionsAsync(Guid siteId, DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<LaborSql.SessionViewRow>(new CommandDefinition(InRange, new { SiteId = siteId, First = first, Last = last }, cancellationToken: cancellationToken)))
            .Select(r => r.ToRow())];
    }

    public async Task<IReadOnlyList<RunRow>> ListRunsAsync(Guid siteId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<RunDbRow>(new CommandDefinition(Runs, new { SiteId = siteId, Start = windowStart, End = windowEnd }, cancellationToken: cancellationToken)))
            .Select(r => new RunRow(r.RunId, r.JobId, r.JobNumber, r.MaterialCode, r.EquipmentId, r.LineCode, r.StartedAt, r.EndedAt, r.StandardCrewSize))];
    }

    public async Task<IReadOnlyList<ApprovalRow>> ListApprovalsAsync(Guid siteId, DateOnly first, DateOnly last, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<ApprovalDbRow>(new CommandDefinition(Approvals, new { SiteId = siteId, First = first, Last = last }, cancellationToken: cancellationToken)))
            .Select(r => new ApprovalRow(new ShiftKey(r.SiteId, r.ProductionDate, r.ShiftScheduleId, r.ShiftCode), r.ApprovedBy, r.ApprovedAt))];
    }

    public async Task<IReadOnlyList<(Guid Id, string Code, ShiftSchedule Schedule)>> ListSchedulesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<ScheduleRow>(new CommandDefinition(Schedules, new { SiteId = siteId }, cancellationToken: cancellationToken)))
            .Select(r => (r.Id, r.Code, ShiftMapping.ToDomain(r.Code, r.Name, r.CycleDays, r.CycleStart, ShiftSql.Read(r.ShiftsJson), ShiftSql.ReadOverrides(r.OverridesJson))))];
    }

    public async Task<IReadOnlyDictionary<string, string>> CrewNamesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<(string Code, string Name)>(new CommandDefinition(CrewNames, new { SiteId = siteId }, cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Code, r => r.Name, StringComparer.Ordinal);
    }

    private sealed class LineRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public Guid? RunningJobId { get; init; }
        public string? RunningJobNumber { get; init; }
        public decimal? StandardCrewSize { get; init; }
    }

    private sealed class RunDbRow
    {
        public Guid RunId { get; init; }
        public Guid JobId { get; init; }
        public string JobNumber { get; init; } = "";
        public string? MaterialCode { get; init; }
        public Guid EquipmentId { get; init; }
        public string LineCode { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public decimal? StandardCrewSize { get; init; }
    }

    private sealed class ApprovalDbRow
    {
        public Guid SiteId { get; init; }
        public DateOnly ProductionDate { get; init; }
        public Guid? ShiftScheduleId { get; init; }
        public string? ShiftCode { get; init; }
        public string ApprovedBy { get; init; } = "";
        public DateTimeOffset ApprovedAt { get; init; }
    }

    private sealed class ScheduleRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public int CycleDays { get; init; }
        public DateOnly CycleStart { get; init; }
        public string ShiftsJson { get; init; } = "[]";
        public string OverridesJson { get; init; } = "[]";
    }
}
