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
        SELECT a.id AS assignment_id, a.app_user_id AS user_id, r.code AS role_code, a.scope_equipment_id AS site_id, a.valid_from AS granted_at,
               s.code AS site_code
        FROM sec.role_assignment a
        JOIN sec.role r ON r.id = a.role_id
        LEFT JOIN core.equipment s ON s.id = a.scope_equipment_id
        WHERE a.app_user_id = ANY(@Ids) AND a.valid_from <= @At AND (a.valid_to IS NULL OR a.valid_to > @At)
        ORDER BY r.code, s.code
        """;

    private const string Users = """
        SELECT u.id, coalesce(u.username, u.idp_subject) AS username, u.display_name, u.email, u.is_active, u.idp_issuer AS issuer,
               u.last_login_at, coalesce(c.must_change, false) AS must_change_password, coalesce(c.locked_until > @At, false) AS is_locked,
               u.xmin::text::bigint AS version
        FROM sec.app_user u
        LEFT JOIN sec.local_credential c ON c.app_user_id = u.id
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
            [.. grants.Select(g => new RoleGrant(g.AssignmentId, g.RoleCode, g.SiteId))], user.MustChange);
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await UsersAsync($"{Users} ORDER BY u.display_name", new { At = now }, now, cancellationToken);

    public async Task<UserDto?> GetUserAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken) =>
        await UsersAsync($"{Users} WHERE u.id = @Id", new { At = now, Id = id }, now, cancellationToken) is [var user, ..] ? user : null;

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
                RoleName = Roles.Find(g.RoleCode)?.Name ?? g.RoleCode,
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
        public Guid? SiteId { get; init; }
        public string? SiteCode { get; init; }
        public DateTimeOffset GrantedAt { get; init; }
    }
}
