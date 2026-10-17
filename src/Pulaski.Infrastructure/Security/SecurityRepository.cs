using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
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
