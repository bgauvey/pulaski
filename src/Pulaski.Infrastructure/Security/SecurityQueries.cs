using Dapper;
using Npgsql;
using Pulaski.Application.Security;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Infrastructure.Security;

/// <summary>Read side for security (D-037): who a signed-in caller is, and the Users screen.</summary>
internal sealed class SecurityQueries(NpgsqlDataSource dataSource) : ISecurityReadStore, ISiteLookup
{
    private const string Access = """
        SELECT u.id, u.username, u.idp_subject AS subject, u.display_name, u.is_active, coalesce(c.must_change, false) AS must_change
        FROM sec.app_user u
        LEFT JOIN sec.local_credential c ON c.app_user_id = u.id
        WHERE u.idp_issuer = @Issuer AND u.idp_subject = @Subject
        """;

    private const string Grants = """
        SELECT a.id AS assignment_id, a.app_user_id AS user_id, r.code AS role_code, r.name AS role_name, a.scope_equipment_id AS site_id,
               a.valid_from AS granted_at, s.code AS site_code,
               array(SELECT rp.permission_code FROM sec.role_permission rp WHERE rp.role_id = r.id AND rp.is_granted ORDER BY rp.permission_code) AS permissions
        FROM sec.role_assignment a
        JOIN sec.role r ON r.id = a.role_id AND r.is_active
        LEFT JOIN core.equipment s ON s.id = a.scope_equipment_id
        WHERE a.app_user_id = ANY(@Ids) AND a.valid_from <= @At AND (a.valid_to IS NULL OR a.valid_to > @At)
        ORDER BY r.name, s.code
        """;

    private const string Users = """
        SELECT u.id, coalesce(u.username, u.idp_subject) AS username, u.display_name, u.email, u.is_active, u.idp_issuer AS issuer,
               u.last_login_at, coalesce(c.must_change, false) AS must_change_password, coalesce(c.locked_until > @At, false) AS is_locked,
               u.xmin::text::bigint AS version
        FROM sec.app_user u
        LEFT JOIN sec.local_credential c ON c.app_user_id = u.id
        """;

    // The administrator's permissions come from the code (D-037), so its row lists none.
    private const string RoleList = """
        SELECT r.id, r.code, r.name, r.description, r.is_active, r.xmin::text::bigint AS version,
               array(SELECT rp.permission_code FROM sec.role_permission rp WHERE rp.role_id = r.id AND rp.is_granted ORDER BY rp.permission_code) AS permissions,
               (SELECT count(*)::int FROM sec.role_assignment a
                WHERE a.role_id = r.id AND a.valid_from <= @At AND (a.valid_to IS NULL OR a.valid_to > @At)) AS holders
        FROM sec.role r
        ORDER BY r.code = 'administrator' DESC, r.is_active DESC, r.name
        """;

    private const string SiteCodes = "SELECT id, code FROM core.equipment WHERE id = ANY(@Ids)";

    public async Task<UserAccess?> GetAccessAsync(string issuer, string subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var user = await connection.QuerySingleOrDefaultAsync<AccessRow>(new CommandDefinition(Access, new { Issuer = issuer, Subject = subject }, cancellationToken: cancellationToken));
        if (user is null)
        {
            return null;
        }

        var grants = await connection.QueryAsync<GrantRow>(new CommandDefinition(Grants, new { Ids = new[] { user.Id }, At = now }, cancellationToken: cancellationToken));
        return new UserAccess(
            user.Id, user.Username ?? user.Subject, user.DisplayName, Issuers.ActorId(issuer, subject), user.IsActive,
            [.. grants.Select(g => new RoleGrant(g.AssignmentId, g.RoleCode, g.RoleName, g.SiteId, g.Permissions))], user.MustChange);
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await UsersAsync($"{Users} ORDER BY u.display_name", new { At = now }, now, cancellationToken);

    public async Task<UserDto?> GetUserAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await UsersAsync($"{Users} WHERE u.id = @Id", new { At = now, Id = id }, now, cancellationToken) is [var user, ..] ? user : null;

    public async Task<IReadOnlyList<RoleDto>> ListRolesAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<RoleRow>(new CommandDefinition(RoleList, new { At = now }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new RoleDto
        {
            Id = r.Id,
            Code = r.Code,
            Name = r.Name,
            Description = r.Description,
            IsGlobal = Roles.IsGlobal(r.Code),
            IsLocked = Roles.IsGlobal(r.Code),
            IsActive = r.IsActive,
            Permissions = Roles.IsGlobal(r.Code) ? [.. Permissions.All.Keys] : r.Permissions,
            Holders = r.Holders,
            Version = r.Version,
        })];
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetSiteCodesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QueryAsync<(Guid Id, string Code)>(new CommandDefinition(SiteCodes, new { Ids = siteIds.ToArray() }, cancellationToken: cancellationToken)))
            .ToDictionary(r => r.Id, r => r.Code);
    }

    public async Task<Guid?> SiteOfAsync(SiteOf kind, Guid id, CancellationToken cancellationToken)
    {
        var sql = kind switch
        {
            SiteOf.Equipment => "SELECT CASE WHEN level = 'site' THEN id ELSE site_id END FROM core.equipment WHERE id = @Id",
            SiteOf.Job => "SELECT site_id FROM core.job WHERE id = @Id",
            SiteOf.WorkOrder => "SELECT site_id FROM core.work_order WHERE id = @Id",
            SiteOf.Lot => "SELECT site_id FROM core.lot WHERE id = @Id",
            SiteOf.StockCount => "SELECT site_id FROM core.stock_count WHERE id = @Id",
            SiteOf.Process => "SELECT site_id FROM core.process WHERE id = @Id",
            SiteOf.Capability => "SELECT e.site_id FROM core.equipment_capability c JOIN core.equipment e ON e.id = c.equipment_id WHERE c.id = @Id",
            SiteOf.PartialBatch => "SELECT j.site_id FROM core.partial_batch p JOIN core.job j ON j.id = p.job_id WHERE p.id = @Id",
            SiteOf.RunStandard => "SELECT e.site_id FROM core.run_standard r JOIN core.equipment e ON e.id = r.equipment_id WHERE r.id = @Id",
            SiteOf.ReasonCode => "SELECT site_id FROM core.reason_code WHERE id = @Id",
            SiteOf.ShiftSchedule => "SELECT site_id FROM core.shift_schedule WHERE id = @Id",
            SiteOf.Crew => "SELECT site_id FROM core.crew WHERE id = @Id",
            SiteOf.Person => "SELECT site_id FROM core.person WHERE id = @Id",
            SiteOf.LaborSession => "SELECT site_id FROM core.labor_session WHERE id = @Id",
            SiteOf.MachineProfile => "SELECT site_id FROM core.machine_profile WHERE id = @Id",
            SiteOf.Downtime => "SELECT e.site_id FROM ts.downtime d JOIN core.equipment e ON e.id = d.equipment_id WHERE d.id = @Id",
            SiteOf.EdgeNode => "SELECT site_id FROM core.edge_node WHERE id = @Id",
            SiteOf.EdgeConnection => "SELECT n.site_id FROM core.edge_connection c JOIN core.edge_node n ON n.id = c.edge_node_id WHERE c.id = @Id",
            SiteOf.EquipmentTag => "SELECT n.site_id FROM core.equipment_tag t JOIN core.edge_connection c ON c.id = t.connection_id JOIN core.edge_node n ON n.id = c.edge_node_id WHERE t.id = @Id",
            SiteOf.Terminal => "SELECT site_id FROM sec.terminal WHERE id = @Id",
            SiteOf.Badge => "SELECT p.site_id FROM sec.badge b JOIN core.person p ON p.id = b.person_id WHERE b.id = @Id",
            SiteOf.LotAttribute => "SELECT site_id FROM core.lot_attribute WHERE id = @Id",
            SiteOf.LotDocument => "SELECT site_id FROM core.lot_document WHERE id = @Id",
            SiteOf.ExpectedReceipt => "SELECT site_id FROM core.expected_receipt WHERE id = @Id",
            SiteOf.Container => "SELECT site_id FROM core.container WHERE id = @Id",
            SiteOf.LaborBreak => "SELECT site_id FROM core.labor_break WHERE id = @Id",
            SiteOf.Qualification => "SELECT site_id FROM core.qualification WHERE id = @Id",
            SiteOf.QualificationGrant => "SELECT p.site_id FROM core.person_qualification g JOIN core.person p ON p.id = g.person_id WHERE g.id = @Id",
            SiteOf.CalendarException => "SELECT e.site_id FROM core.calendar_exception x JOIN core.equipment e ON e.id = x.scope_equipment_id WHERE x.id = @Id",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(sql, new { Id = id }, cancellationToken: cancellationToken));
    }

    private async Task<IReadOnlyList<UserDto>> UsersAsync(string sql, object args, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var users = (await connection.QueryAsync<UserDto>(new CommandDefinition(sql, args, cancellationToken: cancellationToken))).AsList();
        if (users.Count == 0)
        {
            return users;
        }

        var grants = (await connection.QueryAsync<GrantRow>(new CommandDefinition(Grants, new { Ids = users.Select(u => u.Id).ToArray(), At = now }, cancellationToken: cancellationToken)))
            .ToLookup(g => g.UserId);
        return [.. users.Select(u => u with
        {
            Roles = [.. grants[u.Id].Select(g => new RoleGrantDto
            {
                AssignmentId = g.AssignmentId,
                RoleCode = g.RoleCode,
                RoleName = g.RoleName,
                SiteId = g.SiteId,
                SiteCode = g.SiteCode,
                GrantedAt = g.GrantedAt,
            })],
        })];
    }

    private sealed class AccessRow
    {
        public Guid Id { get; init; }
        public string? Username { get; init; }
        public string Subject { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsActive { get; init; }
        public bool MustChange { get; init; }
    }

    private sealed class GrantRow
    {
        public Guid AssignmentId { get; init; }
        public Guid UserId { get; init; }
        public string RoleCode { get; init; } = "";
        public string RoleName { get; init; } = "";
        public Guid? SiteId { get; init; }
        public string? SiteCode { get; init; }
        public string[] Permissions { get; init; } = [];
        public DateTimeOffset GrantedAt { get; init; }
    }

    private sealed class RoleRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsActive { get; init; }
        public long Version { get; init; }
        public string[] Permissions { get; init; } = [];
        public int Holders { get; init; }
    }
}
