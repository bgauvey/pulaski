using System.Text;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;

namespace Pulaski.Testing;

/// <summary>In-memory terminals and badges (D-049). Lines a terminal offers come from <see cref="Lines"/>.</summary>
public sealed class InMemoryTerminals : ITerminalRepository, ITerminalReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<TerminalRecord>> _terminals = [];
    private readonly Dictionary<Guid, (byte[]? Secret, byte[]? Code, DateTimeOffset? Expires, DateTimeOffset? LastSeen)> _credentials = [];
    private readonly Dictionary<Guid, BadgeRecord> _badges = [];
    private long _next = 1;

    public Dictionary<Guid, List<TerminalLineDto>> Lines { get; } = [];

    public Versioned<TerminalRecord> Terminal(Guid id)
    {
        lock (_lock)
        {
            return _terminals[id];
        }
    }

    public BadgeRecord Badge(Guid id)
    {
        lock (_lock)
        {
            return _badges[id];
        }
    }

    public IReadOnlyList<BadgeRecord> Badges()
    {
        lock (_lock)
        {
            return [.. _badges.Values];
        }
    }

    public DateTimeOffset? LastSeen(Guid id)
    {
        lock (_lock)
        {
            return _credentials.GetValueOrDefault(id).LastSeen;
        }
    }

    public Task<Versioned<TerminalRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _terminals.GetValueOrDefault(id));

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        Locked(() => _terminals.Values.Any(t => t.Value.SiteId == siteId && t.Value.Code == code));

    public Task<long> InsertAsync(TerminalRecord terminal, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(terminal));

    public Task<long> UpdateAsync(TerminalRecord terminal, DateTimeOffset at, CancellationToken cancellationToken) => Locked(() => Save(terminal));

    public Task SetEnrolmentCodeAsync(Guid id, byte[] codeHash, DateTimeOffset expiresAt, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            _credentials[id] = _credentials.GetValueOrDefault(id) with { Code = codeHash, Expires = expiresAt };
            return 0;
        });

    public Task<TerminalRecord?> EnrolAsync(byte[] codeHash, byte[] secretHash, DateTimeOffset now, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            var match = _credentials.FirstOrDefault(c => c.Value.Code is { } code && code.SequenceEqual(codeHash) && c.Value.Expires > now
                && _terminals[c.Key].Value.IsActive);
            if (match.Value.Code is null)
            {
                return null;
            }

            _credentials[match.Key] = match.Value with { Secret = secretHash, Code = null, Expires = null };
            var enrolled = _terminals[match.Key].Value with { EnrolledAt = now };
            Save(enrolled);
            return enrolled;
        });

    public Task TouchAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken) =>
        Locked(() =>
        {
            _credentials[id] = _credentials.GetValueOrDefault(id) with { LastSeen = at };
            return 0;
        });

    public Task<BadgeRecord?> GetBadgeForUpdateAsync(Guid id, CancellationToken cancellationToken) => Locked(() => _badges.GetValueOrDefault(id));

    public Task<BadgeRecord?> GetActiveBadgeOfPersonForUpdateAsync(Guid personId, CancellationToken cancellationToken) =>
        Locked(() => _badges.Values.FirstOrDefault(b => b.PersonId == personId && b.RevokedAt is null));

    public Task<BadgeRecord?> FindActiveBadgeForUpdateAsync(byte[] badgeHash, CancellationToken cancellationToken) =>
        Locked(() => _badges.Values.FirstOrDefault(b => b.BadgeHash.SequenceEqual(badgeHash) && b.RevokedAt is null));

    public Task InsertBadgeAsync(BadgeRecord badge, CancellationToken cancellationToken) => Locked(() => _badges[badge.Id] = badge);

    public Task UpdateBadgeAsync(BadgeRecord badge, CancellationToken cancellationToken) => Locked(() => _badges[badge.Id] = badge);

    public Task<IReadOnlyList<TerminalDto>> ListAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<TerminalDto>>(() => [.. _terminals.Values.Where(t => t.Value.SiteId == siteId).Select(t => new TerminalDto
        {
            Id = t.Value.Id, SiteId = t.Value.SiteId, Code = t.Value.Code, Name = t.Value.Name, ScopeEquipmentId = t.Value.ScopeEquipmentId,
            DefaultEquipmentId = t.Value.DefaultEquipmentId, AuthMode = t.Value.AuthMode, IsActive = t.Value.IsActive, EnrolledAt = t.Value.EnrolledAt, Version = t.Version,
        })]);

    public Task<TerminalRecord?> FindBySecretAsync(byte[] secretHash, CancellationToken cancellationToken) =>
        Locked(() => _credentials.FirstOrDefault(c => c.Value.Secret is { } s && s.SequenceEqual(secretHash)) is { Value.Secret: not null } found
            && _terminals[found.Key].Value is { IsActive: true } t ? t : null);

    public Task<IReadOnlyList<TerminalLineDto>> ListLinesAsync(Guid terminalId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<TerminalLineDto>>(() => Lines.TryGetValue(terminalId, out var lines) ? [.. lines] : []);

    public Task<IReadOnlyList<BadgeDto>> ListBadgesAsync(Guid siteId, CancellationToken cancellationToken) =>
        Locked<IReadOnlyList<BadgeDto>>(() => [.. _badges.Values.Select(b => new BadgeDto
        {
            Id = b.Id, PersonId = b.PersonId, HasPin = b.PinHash is not null, IssuedAt = b.IssuedAt, IssuedBy = b.IssuedBy, RevokedAt = b.RevokedAt, LockedUntil = b.LockedUntil,
        })]);

    private long Save(TerminalRecord terminal)
    {
        var version = ++_next;
        _terminals[terminal.Id] = new(terminal, version);
        return version;
    }

    private Task<T> Locked<T>(Func<T> act)
    {
        lock (_lock)
        {
            return Task.FromResult(act());
        }
    }
}

/// <summary>A badge "hash" that is just the value's bytes reversed: stable and readable in tests.</summary>
public sealed class ReversingBadgeHasher : IBadgeHasher
{
    public byte[] Hash(string normalizedBadgeValue) => [.. Encoding.UTF8.GetBytes(normalizedBadgeValue).Reverse()];
}
