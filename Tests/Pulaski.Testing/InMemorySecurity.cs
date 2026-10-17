using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;

namespace Pulaski.Testing;

/// <summary>In-memory users, passwords and roles (D-037), and the sites things belong to.</summary>
public sealed class InMemorySecurity : ISecurityReadStore, ISecurityRepository, ISiteLookup
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<SecurityUser>> _users = [];
    private readonly Dictionary<Guid, LocalCredential> _credentials = [];
    private readonly Dictionary<Guid, RoleAssignment> _assignments = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lastLogins = [];
    private readonly HashSet<Guid> _sites = [];
    private readonly Dictionary<(SiteOf Kind, Guid Id), Guid> _siteOf = [];
    private long _next = 1;

    /// <summary>Adds an active local user holding <paramref name="roleCode"/> (at <paramref name="siteId"/> unless global).</summary>
    public SecurityUser AddUser(string username, string password, string roleCode, Guid? siteId = null, bool mustChange = false, IPasswordHasher? hasher = null)
    {
        var user = new SecurityUser(Guid.CreateVersion7(), Issuers.Local, username, username, username, null, IsActive: true);
        lock (_lock)
        {
            _users[user.Id] = new(user, ++_next);
            _credentials[user.Id] = new LocalCredential(user.Id, (hasher ?? new PlainPasswordHasher()).Hash(password), mustChange, 0, null);
            var assignment = new RoleAssignment(Guid.CreateVersion7(), user.Id, roleCode, siteId, DateTimeOffset.UnixEpoch, null);
            _assignments[assignment.Id] = assignment;
            if (siteId is { } site)
            {
                _sites.Add(site);
            }
        }

        return user;
    }

    public void AddSite(Guid siteId)
    {
        lock (_lock)
        {
            _sites.Add(siteId);
        }
    }

    /// <summary>Says which site something belongs to, for permission checks.</summary>
    public void SetSite(SiteOf kind, Guid id, Guid siteId)
    {
        lock (_lock)
        {
            _siteOf[(kind, id)] = siteId;
            _sites.Add(siteId);
        }
    }

    public LocalCredential? Credential(Guid userId)
    {
        lock (_lock)
        {
            return _credentials.GetValueOrDefault(userId);
        }
    }

    public IReadOnlyList<RoleAssignment> Assignments(Guid userId)
    {
        lock (_lock)
        {
            return [.. _assignments.Values.Where(a => a.UserId == userId)];
        }
    }

    public Task<UserAccess?> GetAccessAsync(string issuer, string subject, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_users.Values.FirstOrDefault(u => u.Value.Issuer == issuer && u.Value.Subject == subject) is not { } found)
            {
                return Task.FromResult<UserAccess?>(null);
            }

            var user = found.Value;
            var grants = _assignments.Values.Where(a => a.UserId == user.Id && a.IsActiveAt(now)).Select(a => new RoleGrant(a.Id, a.RoleCode, a.SiteId)).ToList();
            return Task.FromResult<UserAccess?>(new UserAccess(
                user.Id, user.Username ?? user.Subject, user.DisplayName, Issuers.ActorId(user.Issuer, user.Subject), user.IsActive, grants,
                _credentials.GetValueOrDefault(user.Id)?.MustChange == true));
        }
    }

    public Task<IReadOnlyList<UserDto>> ListUsersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<UserDto>>([.. _users.Values.Select(u => ToDto(u, now)).OrderBy(u => u.Username, StringComparer.Ordinal)]);
        }
    }

    public Task<UserDto?> GetUserAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_users.TryGetValue(id, out var user) ? ToDto(user, now) : null);
        }
    }

    public Task<IReadOnlyDictionary<Guid, string>> GetSiteCodesAsync(IReadOnlyCollection<Guid> siteIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, string>>(siteIds.Distinct().ToDictionary(id => id, id => $"SITE-{id.ToString()[..4]}"));

    public Task<Versioned<SecurityUser>?> FindLocalUserForUpdateAsync(string username, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_users.Values.FirstOrDefault(u => u.Value.Issuer == Issuers.Local && u.Value.Username == username));
        }
    }

    public Task<Versioned<SecurityUser>?> GetUserForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_users.GetValueOrDefault(id));
        }
    }

    public Task<long> InsertUserAsync(SecurityUser user, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _users[user.Id] = new(user, version);
            return Task.FromResult(version);
        }
    }

    public Task<long> UpdateUserAsync(SecurityUser user, DateTimeOffset at, CancellationToken cancellationToken) => InsertUserAsync(user, at, cancellationToken);

    public Task SetLastLoginAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _lastLogins[userId] = at;
        }

        return Task.CompletedTask;
    }

    public Task<LocalCredential?> GetCredentialForUpdateAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult(Credential(userId));

    public Task UpsertCredentialAsync(LocalCredential credential, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _credentials[credential.UserId] = credential;
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<RoleAssignment>> ListActiveAssignmentsForUpdateAsync(Guid userId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<RoleAssignment>>([.. _assignments.Values.Where(a => a.UserId == userId && a.IsActiveAt(at))]);
        }
    }

    public Task<RoleAssignment?> GetAssignmentForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_assignments.GetValueOrDefault(id));
        }
    }

    public Task InsertAssignmentAsync(RoleAssignment assignment, Guid? grantedBy, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _assignments[assignment.Id] = assignment;
        }

        return Task.CompletedTask;
    }

    public Task EndAssignmentAsync(Guid id, DateTimeOffset at, Guid? endedBy, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _assignments[id] = _assignments[id] with { ValidTo = at };
        }

        return Task.CompletedTask;
    }

    public Task<int> CountOtherAdministratorsAsync(Guid exceptUserId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_assignments.Values
                .Where(a => a.UserId != exceptUserId && a.RoleCode == Roles.Administrator && a.IsActiveAt(at) && _users[a.UserId].Value.IsActive)
                .Select(a => a.UserId).Distinct().Count());
        }
    }

    public Task<bool> IsSiteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sites.Contains(id));
        }
    }

    public Task<int> CountUsersAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_users.Count);
        }
    }

    public Task<Guid?> SiteOfAsync(SiteOf kind, Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_siteOf.TryGetValue((kind, id), out var site) ? site : (Guid?)null);
        }
    }

    private UserDto ToDto(Versioned<SecurityUser> user, DateTimeOffset now)
    {
        var credential = _credentials.GetValueOrDefault(user.Value.Id);
        return new UserDto
        {
            Id = user.Value.Id,
            Username = user.Value.Username ?? user.Value.Subject,
            DisplayName = user.Value.DisplayName,
            Email = user.Value.Email,
            IsActive = user.Value.IsActive,
            Issuer = user.Value.Issuer,
            LastLoginAt = _lastLogins.TryGetValue(user.Value.Id, out var at) ? at : null,
            MustChangePassword = credential?.MustChange == true,
            IsLocked = credential?.LockedUntil > now,
            Roles = [.. _assignments.Values.Where(a => a.UserId == user.Value.Id && a.IsActiveAt(now)).Select(a => new RoleGrantDto
            {
                AssignmentId = a.Id,
                RoleCode = a.RoleCode,
                RoleName = Roles.Find(a.RoleCode)?.Name ?? a.RoleCode,
                SiteId = a.SiteId,
                GrantedAt = a.ValidFrom,
            })],
            Version = user.Version,
        };
    }
}

/// <summary>A fast stand-in for the real hasher: tests don't need 210,000 PBKDF2 rounds.</summary>
public sealed class PlainPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => "plain$" + password;

    public bool Verify(string password, string hash) => hash == "plain$" + password;
}
