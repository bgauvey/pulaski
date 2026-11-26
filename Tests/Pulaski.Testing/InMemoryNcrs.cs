using Pulaski.Application.Abstractions;
using Pulaski.Application.Quality;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;

namespace Pulaski.Testing;

/// <summary>Nonconformance reports in memory (#133): enough for handlers to resolve and run; every user is a site user.</summary>
public sealed class InMemoryNcrs : INcrRepository, INcrReadStore
{
    private readonly Dictionary<Guid, (Nonconformance Ncr, long Version)> _ncrs = [];
    private readonly Dictionary<Guid, NcrAction> _actions = [];
    private long _number;

    public Task<long> NextNumberAsync(CancellationToken cancellationToken) => Task.FromResult(++_number);

    public Task<NcrSource?> SourceAsync(string kind, Guid id, DateTimeOffset? at, CancellationToken cancellationToken) => Task.FromResult<NcrSource?>(null);

    public Task<IReadOnlyList<Guid>> LotsAtAsync(Guid siteId, IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. lotIds]);

    public Task<bool> IsSiteUserAsync(Guid userId, Guid siteId, CancellationToken cancellationToken) => Task.FromResult(true);

    public Task InsertAsync(Nonconformance ncr, CancellationToken cancellationToken)
    {
        _ncrs[ncr.Id] = (ncr, 1);
        return Task.CompletedTask;
    }

    public Task<Versioned<Nonconformance>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_ncrs.TryGetValue(id, out var n) ? new Versioned<Nonconformance>(n.Ncr, n.Version) : null);

    public Task<long> UpdateAsync(Nonconformance ncr, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = _ncrs[ncr.Id].Version + 1;
        _ncrs[ncr.Id] = (ncr, version);
        return Task.FromResult(version);
    }

    public Task LinkAsync(Guid ncrId, IReadOnlyCollection<Guid> lotIds, IReadOnlyCollection<Guid> runIds, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<NcrAction>> ListActionsAsync(Guid ncrId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NcrAction>>([.. _actions.Values.Where(a => a.NcrId == ncrId)]);

    public Task InsertActionAsync(NcrAction action, string by, CancellationToken cancellationToken)
    {
        _actions[action.Id] = action;
        return Task.CompletedTask;
    }

    public Task UpdateActionAsync(NcrAction action, CancellationToken cancellationToken)
    {
        _actions[action.Id] = action;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<NcrSummaryDto>> ListAsync(Guid? siteId, string? status, Guid? lotId, DateOnly today, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<NcrSummaryDto>>([.. _ncrs.Values.Select(n => n.Ncr).Where(n => siteId is null || n.SiteId == siteId).Select(n => new NcrSummaryDto
        {
            Id = n.Id, SiteId = n.SiteId, Number = n.Number, Title = n.Title, Severity = n.Severity, Status = n.Status, SourceKind = n.SourceKind, RaisedAt = n.RaisedAt,
        })]);

    public Task<NcrDto?> GetAsync(Guid id, DateOnly today, CancellationToken cancellationToken) =>
        Task.FromResult(_ncrs.TryGetValue(id, out var n) ? new NcrDto
        {
            Id = n.Ncr.Id,
            SiteId = n.Ncr.SiteId,
            Number = n.Ncr.Number,
            Title = n.Ncr.Title,
            Severity = n.Ncr.Severity,
            Status = n.Ncr.Status,
            SourceKind = n.Ncr.SourceKind,
            Version = n.Version,
        } : null);

    public Task<IReadOnlyList<NcrAssigneeDto>> AssigneesAsync(Guid siteId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<NcrAssigneeDto>>([]);
}
