using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>Feed points and shortfalls (#33) in memory: what a test sets, and the shortfalls handlers write.</summary>
public sealed class InMemoryFeeds : IFeedRepository, IFeedReadStore
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, (FeedShortfall Shortfall, long Version)> _shortfalls = [];

    public Dictionary<Guid, List<FeedPointRow>> FeedPointsOfLine { get; } = [];

    public List<FeedEvent> Events { get; } = [];

    public Dictionary<Guid, List<AutoComponent>> Components { get; } = [];

    public IReadOnlyList<FeedShortfall> Shortfalls
    {
        get
        {
            lock (_lock)
            {
                return [.. _shortfalls.Values.Select(s => s.Shortfall)];
            }
        }
    }

    public Task<IReadOnlyList<FeedPointRow>> ListFeedPointsOfLineAsync(Guid lineId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeedPointRow>>(FeedPointsOfLine.TryGetValue(lineId, out var points) ? points : []);

    public Task<IReadOnlyList<FeedEvent>> ListFeedEventsAsync(IReadOnlyCollection<Guid> feedPointIds, DateTimeOffset until, CancellationToken cancellationToken) =>
        Task.FromResult(Named(feedPointIds, until));

    public Task<IReadOnlyList<FeedEvent>> ListFeedEventsAsync(IReadOnlyCollection<Guid> feedPointIds, CancellationToken cancellationToken) =>
        Task.FromResult(Named(feedPointIds, DateTimeOffset.MaxValue));

    public Task<IReadOnlyList<AutoComponent>> ListAutoComponentsAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AutoComponent>>(Components.TryGetValue(jobId, out var components) ? components : []);

    public Task<DateTimeOffset?> GetPreviousReportAtAsync(Guid runId, Guid reportId, DateTimeOffset before, CancellationToken cancellationToken) =>
        Task.FromResult<DateTimeOffset?>(null);

    public Task<IReadOnlyList<Guid>> ListLotsAtAsync(IReadOnlyCollection<Guid> locationIds, Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([]);

    public Task<bool> IsPostedAsync(Guid sourceRef, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_shortfalls.Values.Any(s => s.Shortfall.SourceRef == sourceRef));
        }
    }

    public Task InsertShortfallAsync(FeedShortfall shortfall, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_shortfalls.Values.Any(s => s.Shortfall.SourceRef == shortfall.SourceRef && s.Shortfall.EquipmentId == shortfall.EquipmentId
                && s.Shortfall.MaterialId == shortfall.MaterialId))
            {
                _shortfalls[shortfall.Id] = (shortfall, 1);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CounterRow>> ListUnpostedCountsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset since, int take, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CounterRow>>([]);

    public Task<CounterContext?> GetCounterContextAsync(Guid feedPointId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult<CounterContext?>(null);

    public Task<Versioned<FeedShortfall>?> GetShortfallForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_shortfalls.TryGetValue(id, out var s) ? new Versioned<FeedShortfall>(s.Shortfall, s.Version) : null);
        }
    }

    public Task<long> UpdateShortfallAsync(FeedShortfall shortfall, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _shortfalls[shortfall.Id].Version + 1;
            _shortfalls[shortfall.Id] = (shortfall, version);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<Versioned<FeedShortfall>>> LockOpenShortfallsOfAsync(Guid sourceRef, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Versioned<FeedShortfall>>>(
                [.. _shortfalls.Values.Where(s => s.Shortfall.SourceRef == sourceRef && s.Shortfall.Status == FeedShortfallStatus.Open)
                    .Select(s => new Versioned<FeedShortfall>(s.Shortfall, s.Version))]);
        }
    }

    public Task<IReadOnlyList<(Guid LotId, Guid EventId)>> ListAutoConsumptionsAsync(Guid sourceRef, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Guid LotId, Guid EventId)>>([]);

    public Task<IReadOnlyList<FeedPointDto>> ListFeedPointsAsync(Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeedPointDto>>([]);

    public Task<IReadOnlyList<LotSummaryDto>> GetLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LotSummaryDto>>([]);

    public Task<IReadOnlyList<FeedShortfallDto>> ListShortfallsAsync(Guid? siteId, string status, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeedShortfallDto>>([]);

    public Task<FeedShortfallDto?> GetShortfallAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_shortfalls.TryGetValue(id, out var s)
                ? new FeedShortfallDto
                {
                    Id = id,
                    SiteId = s.Shortfall.SiteId,
                    EquipmentId = s.Shortfall.EquipmentId,
                    EquipmentCode = "",
                    JobId = s.Shortfall.JobId,
                    Source = s.Shortfall.Source,
                    Reason = s.Shortfall.Reason,
                    Status = s.Shortfall.Status,
                    Version = s.Version,
                }
                : null);
        }
    }

    private IReadOnlyList<FeedEvent> Named(IReadOnlyCollection<Guid> ids, DateTimeOffset until) =>
        [.. Events.Where(e => e.At <= until && (e.FromLocationId is { } f && ids.Contains(f) || e.ToLocationId is { } t && ids.Contains(t)))];
}
