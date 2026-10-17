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
public sealed record SecurityUser(Guid Id, string Issuer, string Subject, string? Username, string DisplayName, string? Email, bool IsActive);

/// <summary>A local user's password and lockout state.</summary>
public sealed record LocalCredential(Guid UserId, string PasswordHash, bool MustChange, int FailedAttempts, DateTimeOffset? LockedUntil);

/// <summary>A role assignment as the commands see it.</summary>
public sealed record RoleAssignment(Guid Id, Guid UserId, string RoleCode, Guid? SiteId, DateTimeOffset ValidFrom, DateTimeOffset? ValidTo)
{
    public bool IsActiveAt(DateTimeOffset at) => ValidFrom <= at && (ValidTo is null || ValidTo > at);
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

    /// <summary>The codes of the sites the user's roles are held at, for showing them.</summary>
    Task<IReadOnlyDictionary<Guid, string>> GetSiteCodesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ISecurityRepository
{
    Task<Versioned<SecurityUser>?> FindLocalUserForUpdateAsync(string username, CancellationToken cancellationToken);

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
}

/// <summary>Finds the site something belongs to, so a role held at that site can be checked (D-037).</summary>
public interface ISiteLookup
{
    /// <returns>The site, or null when the thing doesn't exist or belongs to no site (an enterprise-wide reason).</returns>
    Task<Guid?> SiteOfAsync(SiteOf kind, Guid id, CancellationToken cancellationToken);
}
