using Pulaski.Application.Abstractions;
using Pulaski.Application.Andon;
using Pulaski.Contracts.Andon;
using Pulaski.Domain.Andon;

namespace Pulaski.Testing;

/// <summary>In-memory andon (#125): every site is known; the test adds the lines it calls from.</summary>
public sealed class InMemoryAndon : IAndonRepository, IAndonReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (AndonType Type, long Version)> _types = [];
    private readonly Dictionary<Guid, AndonCall> _calls = [];
    private long _version;

    public Dictionary<Guid, AndonLine> Lines { get; } = [];

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task<AndonLine?> LockLineAsync(Guid lineId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Lines.GetValueOrDefault(lineId));
        }
    }

    public Task<Versioned<AndonType>?> GetTypeForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_types.TryGetValue(id, out var t) ? new Versioned<AndonType>(t.Type, t.Version) : null);
        }
    }

    public Task<bool> TypeCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_types.Values.Any(t => t.Type.SiteId == siteId && t.Type.Code == code));
        }
    }

    public Task<long> InsertTypeAsync(AndonType type, DateTimeOffset at, CancellationToken cancellationToken) => UpdateTypeAsync(type, at, cancellationToken);

    public Task<long> UpdateTypeAsync(AndonType type, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _types[type.Id] = (type, ++_version);
            return Task.FromResult(_version);
        }
    }

    public Task<AndonCall?> FindOpenCallForUpdateAsync(Guid lineId, Guid typeId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_calls.Values.FirstOrDefault(c => c.LineId == lineId && c.TypeId == typeId && c.Status != AndonStatuses.Resolved));
        }
    }

    public Task<AndonCall?> GetCallForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_calls.GetValueOrDefault(id));
        }
    }

    public Task InsertCallAsync(AndonCall andonCall, DateTimeOffset at, CancellationToken cancellationToken) => UpdateCallAsync(andonCall, at, cancellationToken);

    public Task UpdateCallAsync(AndonCall andonCall, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _calls[andonCall.Id] = andonCall;
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<AndonTypeDto>> ListTypesAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AndonTypeDto>>([.. _types.Values
                .Where(t => t.Type.SiteId == siteId)
                .OrderByDescending(t => t.Type.IsActive).ThenBy(t => t.Type.SortOrder).ThenBy(t => t.Type.Code, StringComparer.Ordinal)
                .Select(t => new AndonTypeDto
                {
                    Id = t.Type.Id,
                    SiteId = t.Type.SiteId,
                    Code = t.Type.Code,
                    Name = t.Type.Name,
                    EscalateAfterMinutes = t.Type.EscalateAfterMinutes,
                    SortOrder = t.Type.SortOrder,
                    IsActive = t.Type.IsActive,
                    Version = t.Version,
                })]);
        }
    }

    public Task<IReadOnlyList<AndonCallDto>> ListCallsAsync(
        Guid siteId, bool openOnly, DateTimeOffset? from, DateTimeOffset? until, Guid? lineId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AndonCallDto>>([.. _calls.Values
                .Where(c => c.SiteId == siteId && (lineId is null || c.LineId == lineId))
                .Where(c => openOnly ? c.Status != AndonStatuses.Resolved : (from is null || c.RaisedAt >= from) && (until is null || c.RaisedAt < until))
                .OrderByDescending(c => c.RaisedAt)
                .Select(c =>
                {
                    var type = _types[c.TypeId].Type;
                    return new AndonCallDto
                    {
                        Id = c.Id,
                        SiteId = c.SiteId,
                        LineId = c.LineId,
                        LineCode = Lines.TryGetValue(c.LineId, out var l) ? l.Code : "",
                        TypeId = c.TypeId,
                        TypeCode = type.Code,
                        TypeName = type.Name,
                        EscalateAfterMinutes = type.EscalateAfterMinutes,
                        Status = c.Status,
                        Note = c.Note,
                        RaisedAt = c.RaisedAt,
                        RaisedBy = c.RaisedBy,
                        RaisedFrom = c.RaisedFrom,
                        AcknowledgedAt = c.AcknowledgedAt,
                        AcknowledgedBy = c.AcknowledgedBy,
                        ResolvedAt = c.ResolvedAt,
                        ResolvedBy = c.ResolvedBy,
                        Resolution = c.Resolution,
                    };
                })]);
        }
    }
}
