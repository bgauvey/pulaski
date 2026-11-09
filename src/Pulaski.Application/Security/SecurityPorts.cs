using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Application.Security;

/// <summary>Identity providers a user can sign in through (D-037). Only local sign-in is built; others are configured later.</summary>
public static class Issuers
{
    /// <summary>Users whose passwords Pulaski keeps itself.</summary>
    public const string Local = "pulaski-local";

    /// <summary>
    /// Who history says did something: a local user by their user name, anyone else as issuer|subject, so
    /// two identity providers' subjects can't be confused.
    /// </summary>
    public static string ActorId(string issuer, string subject) => issuer == Local ? subject : $"{issuer}|{subject}";
}

/// <summary>A user as the security commands see them.</summary>
public sealed record SecurityUser(Guid Id, string Issuer, string Subject, string? Username, string DisplayName, string? Email, bool IsActive)
{
    /// <summary>A machine's local account, such as the edge's (D-091): it keeps its password when only administrators sign in with one.</summary>
    public bool IsServiceAccount { get; init; }
}

/// <summary>A password reset link as kept (D-093): only the token's hash.</summary>
public sealed record PasswordResetLink(Guid Id, Guid UserId, string TokenHash, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt);

/// <summary>
/// Password reset by e-mail (D-093), from configuration: off unless switched on with an SMTP server and the UI's public address, which the link
/// is made from (never from the request, whose host a caller chooses).
/// </summary>
public interface IPasswordResetSettings
{
    bool Enabled { get; }

    TimeSpan LinkLifetime { get; }

    /// <summary>The UI's public address, such as <c>https://mes.example.com</c>.</summary>
    string UiBaseUrl { get; }

    /// <summary>Only administrators and service accounts sign in with a password (D-091), so only administrators may reset one.</summary>
    bool AdministratorsOnly { get; }
}

/// <summary>Sends an e-mail later, off the request (D-093), so how long an answer takes says nothing about whether an account exists.</summary>
public interface IMailOutbox
{
    /// <summary>Whether a mail server is configured, so what is queued goes out (#84).</summary>
    bool CanSend { get; }

    void Enqueue(string recipient, string subject, string body);
}

/// <summary>A local user's password and lockout state.</summary>
public sealed record LocalCredential(Guid UserId, string PasswordHash, bool MustChange, int FailedAttempts, DateTimeOffset? LockedUntil);

/// <summary>A role assignment as the commands see it.</summary>
public sealed record RoleAssignment(Guid Id, Guid UserId, string RoleCode, Guid? SiteId, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo)
{
    public bool IsActiveAt(DateTimeOffset at) => ValidFrom <= at && (ValidTo is null || ValidTo > at);
}

/// <summary>A role as the commands see it (D-037): what it allows, held at a site unless it is the administrator.</summary>
public sealed record SecurityRole(Guid Id, string Code, string Name, string Description, bool IsActive, IReadOnlyList<string> Permissions)
{
    /// <summary>The administrator: every permission, held everywhere only.</summary>
    public bool IsGlobal => Roles.IsGlobal(Code);

    /// <summary>May also be given at every site (D-092), not only at one; the administrator always is.</summary>
    public bool AllowsEverySite { get; init; }
}

/// <summary>Hashes and checks passwords; the hash carries its own salt and work factor.</summary>
public interface IPasswordHasher
{
    string Hash(string password);

    bool Verify(string password, string hash);
}

/// <summary>Read side for security (D-037).</summary>
public interface ISecurityReadStore
{
    /// <summary>What the user an identity provider vouched for may do now; null when Pulaski doesn't know them.</summary>
    Task<UserAccess?> GetAccessAsync(string issuer, string subject, DateTimeOffset now, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserDto>> ListUsersAsync(DateTimeOffset now, CancellationToken cancellationToken);

    Task<UserDto?> GetUserAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>Every role, with how many users hold it at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<RoleDto>> ListRolesAsync(DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The codes of the sites the user's roles are held at, for showing them.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetSiteCodesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ISecurityRepository
{
    Task<Versioned<SecurityUser>?> FindLocalUserForUpdateAsync(string username, CancellationToken cancellationToken);

    /// <summary>The active local users with this e-mail address, ignoring case, locked (D-093).</summary>
    Task<IReadOnlyList<Versioned<SecurityUser>>> FindLocalUsersByEmailForUpdateAsync(string email, CancellationToken cancellationToken);

    Task InsertPasswordResetAsync(PasswordResetLink link, CancellationToken cancellationToken);

    /// <summary>The open link (not used, not revoked) whose token hashes to <paramref name="tokenHash"/>, locked; null when none.</summary>
    Task<PasswordResetLink?> FindOpenPasswordResetForUpdateAsync(string tokenHash, CancellationToken cancellationToken);

    /// <summary>How many links were asked for the user since <paramref name="since"/>, for the rate limit.</summary>
    Task<int> CountPasswordResetsSinceAsync(Guid userId, DateTimeOffset since, CancellationToken cancellationToken);

    Task MarkPasswordResetUsedAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Ends every open link of the user, but <paramref name="exceptId"/>.</summary>
    Task RevokePasswordResetsAsync(Guid userId, DateTimeOffset at, Guid? exceptId, CancellationToken cancellationToken);

    /// <summary>The user an identity provider knows by <paramref name="issuer"/> and <paramref name="subject"/>, locked (D-091); null when Pulaski doesn't.</summary>
    Task<Versioned<SecurityUser>?> FindUserByIdentityForUpdateAsync(string issuer, string subject, CancellationToken cancellationToken);

    Task<Versioned<SecurityUser>?> GetUserForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> InsertUserAsync(SecurityUser user, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateUserAsync(SecurityUser user, DateTimeOffset at, CancellationToken cancellationToken);

    Task SetLastLoginAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<LocalCredential?> GetCredentialForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    Task UpsertCredentialAsync(LocalCredential credential, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The user's assignments that are in effect at <paramref name="at"/>, locked.</summary>
    Task<IReadOnlyList<RoleAssignment>> ListActiveAssignmentsForUpdateAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<RoleAssignment?> GetAssignmentForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task InsertAssignmentAsync(RoleAssignment assignment, Guid? grantedBy, CancellationToken cancellationToken);

    Task EndAssignmentAsync(Guid id, DateTimeOffset at, Guid? endedBy, CancellationToken cancellationToken);

    /// <summary>Active users holding the administrator role now, other than <paramref name="exceptUserId"/>.</summary>
    Task<int> CountOtherAdministratorsAsync(Guid exceptUserId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<bool> IsSiteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The role and its granted permissions, locked.</summary>
    Task<Versioned<SecurityRole>?> FindRoleForUpdateAsync(string code, CancellationToken cancellationToken);

    Task<long> InsertRoleAsync(SecurityRole role, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Saves the name, description and active flag; every change to a role, its permissions too, gives it a new version.</summary>
    Task<long> UpdateRoleAsync(SecurityRole role, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Grants or takes away one permission. Rows are kept, flagged; the app role has no DELETE on security tables.</summary>
    Task SetRolePermissionAsync(Guid roleId, string permission, bool granted, DateTimeOffset at, Guid? by, CancellationToken cancellationToken);

    /// <summary>How many active assignments of the role there are now.</summary>
    Task<int> CountHoldersAsync(Guid roleId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>How many active assignments of the role are held at every site now (D-092).</summary>
    Task<int> CountEverywhereHoldersAsync(Guid roleId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Copies the code's permission catalog into <c>sec.permission</c>, so roles can name every permission.</summary>
    Task SyncPermissionsAsync(IReadOnlyList<PermissionInfo> catalog, CancellationToken cancellationToken);

    Task<int> CountUsersAsync(CancellationToken cancellationToken);
}

/// <summary>What a permission check needs to find the site of (D-037).</summary>
public enum SiteOf
{
    /// <summary>The equipment itself: a site, or the site it belongs to.</summary>
    Equipment,
    Job,
    WorkOrder,
    Lot,
    StockCount,
    Process,
    Capability,
    PartialBatch,
    RunStandard,
    ReasonCode,
    ShiftSchedule,
    Crew,
    Person,
    LaborSession,
    MachineProfile,
    EdgeNode,
    EdgeConnection,
    EquipmentTag,
    Downtime,
    CalendarException,
    LaborBreak,
    Qualification,
    QualificationGrant,
    Terminal,
    Badge,
    LotAttribute,
    LotDocument,
    ExpectedReceipt,
    Container,
    ChangeoverRule,
    FeedShortfall,
    LabelPrinter,
    SupplierBarcodeFormat,

    /// <summary>A people connection (#71): the site its people are imported into.</summary>
    Connection,

    /// <summary>A role assignment (D-092): its site, none when it is held at every site.</summary>
    RoleAssignment,
}

/// <summary>Finds the site something belongs to, so a role held at that site can be checked (D-037).</summary>
public interface ISiteLookup
{
    /// <returns>The site, or null when the thing doesn't exist or belongs to no site (an enterprise-wide reason).</returns>
    Task<Guid?> SiteOfAsync(SiteOf kind, Guid id, CancellationToken cancellationToken);
}
