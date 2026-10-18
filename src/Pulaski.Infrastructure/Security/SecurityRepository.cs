using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Domain.Security;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Security;

/// <summary>Write side for users, their roles and local passwords (D-037). Every statement runs in the command's transaction.</summary>
internal sealed class SecurityRepository(DbSession session) : ISecurityRepository
{
    private const string UserColumns = """
        id, idp_issuer AS issuer, idp_subject AS subject, username, display_name, email, is_active, xmin::text::bigint AS version
        """;

    private const string FindLocal = $"SELECT {UserColumns} FROM sec.app_user WHERE idp_issuer = 'pulaski-local' AND lower(username) = @Username FOR UPDATE";

    private const string GetUser = $"SELECT {UserColumns} FROM sec.app_user WHERE id = @Id FOR UPDATE";

    private const string InsertUser = """
        INSERT INTO sec.app_user (id, idp_issuer, idp_subject, username, display_name, email, is_active, created_at, updated_at)
        VALUES (@Id, @Issuer, @Subject, @Username, @DisplayName, @Email, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateUser = """
        UPDATE sec.app_user SET display_name = @DisplayName, email = @Email, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string LastLogin = "UPDATE sec.app_user SET last_login_at = @At WHERE id = @Id";

    private const string GetCredential = """
        SELECT app_user_id AS user_id, password_hash, must_change, failed_attempts, locked_until
        FROM sec.local_credential WHERE app_user_id = @Id FOR UPDATE
        """;

    private const string UpsertCredential = """
        INSERT INTO sec.local_credential (app_user_id, password_hash, must_change, failed_attempts, locked_until, changed_at)
        VALUES (@UserId, @PasswordHash, @MustChange, @FailedAttempts, @LockedUntil, @At)
        ON CONFLICT (app_user_id) DO UPDATE
        SET changed_at = CASE WHEN sec.local_credential.password_hash <> EXCLUDED.password_hash THEN EXCLUDED.changed_at ELSE sec.local_credential.changed_at END,
            password_hash = EXCLUDED.password_hash, must_change = EXCLUDED.must_change,
            failed_attempts = EXCLUDED.failed_attempts, locked_until = EXCLUDED.locked_until
        """;

    private const string AssignmentColumns = """
        a.id, a.app_user_id AS user_id, r.code AS role_code, a.scope_equipment_id AS site_id, a.valid_from, a.valid_to
        """;

    private const string ActiveAssignments = $"""
        SELECT {AssignmentColumns}
        FROM sec.role_assignment a JOIN sec.role r ON r.id = a.role_id
        WHERE a.app_user_id = @UserId AND a.valid_from <= @At AND (a.valid_to IS NULL OR a.valid_to > @At)
        ORDER BY a.valid_from
        FOR UPDATE OF a
        """;

    private const string GetAssignment = $"""
        SELECT {AssignmentColumns}
        FROM sec.role_assignment a JOIN sec.role r ON r.id = a.role_id
        WHERE a.id = @Id
        FOR UPDATE OF a
        """;

    private const string InsertAssignment = """
        INSERT INTO sec.role_assignment (id, role_id, app_user_id, scope_equipment_id, valid_from, granted_by_user_id)
        SELECT @Id, r.id, @UserId, @SiteId, @ValidFrom, @GrantedBy FROM sec.role r WHERE r.code = @RoleCode
        """;

    private const string EndAssignment = "UPDATE sec.role_assignment SET valid_to = @At, ended_by_user_id = @EndedBy WHERE id = @Id";

    // Locks the other administrators' assignments, so two can't remove each other at the same moment.
    private const string OtherAdministrators = """
        SELECT a.id
        FROM sec.role_assignment a
        JOIN sec.role r ON r.id = a.role_id AND r.code = 'administrator'
        JOIN sec.app_user u ON u.id = a.app_user_id AND u.is_active
        WHERE a.app_user_id <> @UserId AND a.valid_from <= @At AND (a.valid_to IS NULL OR a.valid_to > @At)
        FOR UPDATE OF a
        """;

    private const string FindRole = """
        SELECT r.id, r.code, r.name, r.description, r.is_active, r.xmin::text::bigint AS version,
               array(SELECT rp.permission_code FROM sec.role_permission rp WHERE rp.role_id = r.id AND rp.is_granted ORDER BY rp.permission_code) AS permissions
        FROM sec.role r WHERE r.code = @Code
        FOR UPDATE OF r
        """;

    private const string InsertRole = """
        INSERT INTO sec.role (id, code, name, description, is_active, is_system, created_at, updated_at)
        VALUES (@Id, @Code, @Name, @Description, @IsActive, false, @At, @At)
        RETURNING xmin::text::bigint
        """;

    // Also run after a permission changes, so the role's version moves on with it.
    private const string UpdateRole = """
        UPDATE sec.role SET name = @Name, description = @Description, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string SetPermission = """
        INSERT INTO sec.role_permission (role_id, permission_code, is_granted, changed_at, changed_by_user_id)
        VALUES (@RoleId, @Permission, @Granted, @At, @By)
        ON CONFLICT (role_id, permission_code) DO UPDATE
        SET is_granted = EXCLUDED.is_granted, changed_at = EXCLUDED.changed_at, changed_by_user_id = EXCLUDED.changed_by_user_id
        """;

    private const string CountHolders = """
        SELECT count(*)::int FROM sec.role_assignment
        WHERE role_id = @RoleId AND valid_from <= @At AND (valid_to IS NULL OR valid_to > @At)
        """;

    private const string SyncPermission = """
        INSERT INTO sec.permission (code, description) VALUES (@Code, @Description)
        ON CONFLICT (code) DO UPDATE SET description = EXCLUDED.description WHERE sec.permission.description <> EXCLUDED.description
        """;

    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')";

    private const string CountUsers = "SELECT count(*)::int FROM sec.app_user";

    public async Task<Versioned<SecurityUser>?> FindLocalUserForUpdateAsync(string username, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<UserRow>(FindLocal, new { Username = username }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<SecurityUser>?> GetUserForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<UserRow>(GetUser, new { Id = id }, cancellationToken))?.ToVersioned();

    public Task<long> InsertUserAsync(SecurityUser user, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertUser, UserArgs(user, at), cancellationToken);

    public Task<long> UpdateUserAsync(SecurityUser user, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateUser, UserArgs(user, at), cancellationToken);

    public Task SetLastLoginAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(LastLogin, new { Id = userId, At = at }, cancellationToken);

    public async Task<LocalCredential?> GetCredentialForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CredentialRow>(GetCredential, new { Id = userId }, cancellationToken) is { } r
            ? new LocalCredential(r.UserId, r.PasswordHash, r.MustChange, r.FailedAttempts, r.LockedUntil)
            : null;

    public Task UpsertCredentialAsync(LocalCredential credential, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            UpsertCredential,
            new { credential.UserId, credential.PasswordHash, credential.MustChange, credential.FailedAttempts, credential.LockedUntil, At = at },
            cancellationToken);

    public async Task<IReadOnlyList<RoleAssignment>> ListActiveAssignmentsForUpdateAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<AssignmentRow>(ActiveAssignments, new { UserId = userId, At = at }, cancellationToken)).Select(r => r.ToDomain())];

    public async Task<RoleAssignment?> GetAssignmentForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<AssignmentRow>(GetAssignment, new { Id = id }, cancellationToken))?.ToDomain();

    public Task InsertAssignmentAsync(RoleAssignment assignment, Guid? grantedBy, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            InsertAssignment,
            new { assignment.Id, assignment.UserId, assignment.SiteId, assignment.ValidFrom, GrantedBy = grantedBy, assignment.RoleCode },
            cancellationToken);

    public Task EndAssignmentAsync(Guid id, DateTimeOffset at, Guid? endedBy, CancellationToken cancellationToken) =>
        session.ExecuteAsync(EndAssignment, new { Id = id, At = at, EndedBy = endedBy }, cancellationToken);

    public async Task<int> CountOtherAdministratorsAsync(Guid exceptUserId, DateTimeOffset at, CancellationToken cancellationToken) =>
        (await session.QueryAsync<Guid>(OtherAdministrators, new { UserId = exceptUserId, At = at }, cancellationToken)).Count;

    public Task<bool> IsSiteAsync(Guid id, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsSite, new { Id = id }, cancellationToken);

    public Task<int> CountUsersAsync(CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<int>(CountUsers, null, cancellationToken);

    public async Task<Versioned<SecurityRole>?> FindRoleForUpdateAsync(string code, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<RoleRow>(FindRole, new { Code = code }, cancellationToken) is { } r
            ? new Versioned<SecurityRole>(new SecurityRole(r.Id, r.Code, r.Name, r.Description, r.IsActive, r.Permissions), r.Version)
            : null;

    public Task<long> InsertRoleAsync(SecurityRole role, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertRole, RoleArgs(role, at), cancellationToken);

    public Task<long> UpdateRoleAsync(SecurityRole role, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateRole, RoleArgs(role, at), cancellationToken);

    public Task SetRolePermissionAsync(Guid roleId, string permission, bool granted, DateTimeOffset at, Guid? by, CancellationToken cancellationToken) =>
        session.ExecuteAsync(SetPermission, new { RoleId = roleId, Permission = permission, Granted = granted, At = at, By = by }, cancellationToken);

    public Task<int> CountHoldersAsync(Guid roleId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<int>(CountHolders, new { RoleId = roleId, At = at }, cancellationToken);

    public async Task SyncPermissionsAsync(IReadOnlyList<PermissionInfo> catalog, CancellationToken cancellationToken)
    {
        foreach (var permission in catalog)
        {
            await session.ExecuteAsync(SyncPermission, new { permission.Code, permission.Description }, cancellationToken);
        }
    }

    private static object RoleArgs(SecurityRole r, DateTimeOffset at) => new { r.Id, r.Code, r.Name, r.Description, r.IsActive, At = at };

    private static object UserArgs(SecurityUser u, DateTimeOffset at) =>
        new { u.Id, u.Issuer, u.Subject, u.Username, u.DisplayName, u.Email, u.IsActive, At = at };

    private sealed class UserRow
    {
        public Guid Id { get; init; }
        public string Issuer { get; init; } = "";
        public string Subject { get; init; } = "";
        public string? Username { get; init; }
        public string DisplayName { get; init; } = "";
        public string? Email { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public Versioned<SecurityUser> ToVersioned() => new(new SecurityUser(Id, Issuer, Subject, Username, DisplayName, Email, IsActive), Version);
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
    }

    private sealed class CredentialRow
    {
        public Guid UserId { get; init; }
        public string PasswordHash { get; init; } = "";
        public bool MustChange { get; init; }
        public int FailedAttempts { get; init; }
        public DateTimeOffset? LockedUntil { get; init; }
    }

    internal sealed class AssignmentRow
    {
        public Guid Id { get; init; }
        public Guid UserId { get; init; }
        public string RoleCode { get; init; } = "";
        public Guid? SiteId { get; init; }
        public DateTimeOffset ValidFrom { get; init; }
        public DateTimeOffset? ValidTo { get; init; }

        public RoleAssignment ToDomain() => new(Id, UserId, RoleCode, SiteId, ValidFrom, ValidTo);
    }
}
