using Pulaski.Application.Integrations;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Integrations;

namespace Pulaski.Testing;

/// <summary>In-memory outbound deliveries (D-061); queuing and sending are covered by the Infrastructure tests.</summary>
public sealed class InMemoryDeliveries : IDeliveryStore, IDeliveryFeedStore
{
    private readonly Dictionary<string, StoredFeedSettings> _feeds = [];

    private readonly Lock _lock = new();
    private readonly List<DeliveryDto> _rows = [];

    public DeliveryDto Add(string sink, string status, Guid? subject = null, int attempts = 0, string? error = null)
    {
        var row = new DeliveryDto
        {
            Id = Guid.CreateVersion7(),
            Sink = sink,
            EventId = Guid.CreateVersion7(),
            EventType = "pulaski.lot.received.v1",
            Subject = subject ?? Guid.CreateVersion7(),
            OccurredAt = DateTimeOffset.UtcNow,
            Status = status,
            Attempts = attempts,
            LastError = error,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        lock (_lock)
        {
            _rows.Add(row);
        }

        return row;
    }

    public DeliveryDto Get(Guid id)
    {
        lock (_lock)
        {
            return _rows.Single(r => r.Id == id);
        }
    }

    public Task<IReadOnlyList<DeliveryDto>> ListAsync(string sink, DeliveryFilter filter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DeliveryDto>>([.. _rows
                .Where(r => r.Sink == sink && (filter.Status is null || r.Status == filter.Status))
                .Where(r => filter.Search is null || r.Subject.ToString() == filter.Search || string.Equals(r.LotNumber, filter.Search, StringComparison.OrdinalIgnoreCase))
                .Where(r => filter.Type is null || r.EventType.Contains(filter.Type, StringComparison.OrdinalIgnoreCase))
                .Where(r => (filter.From is null || r.OccurredAt >= filter.From) && (filter.To is null || r.OccurredAt < filter.To))
                .Take(filter.Limit)]);
        }
    }

    public Task<DeliveryCounts> CountAsync(string sink, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var mine = _rows.Where(r => r.Sink == sink).ToList();
            return Task.FromResult(new DeliveryCounts(
                mine.Count(r => r.Status == DeliveryStatuses.Pending),
                mine.Count(r => r.Status == DeliveryStatuses.Failed),
                mine.Count(r => r.IsHeld),
                mine.Count(r => r.Status == DeliveryStatuses.Delivered && r.DeliveredAt > now.AddDays(-1)),
                mine.Where(r => r.Status == DeliveryStatuses.Pending).Min(r => (DateTimeOffset?)r.OccurredAt),
                mine.Max(r => r.DeliveredAt)));
        }
    }

    public Task<DeliveryRecord?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var r = _rows.FirstOrDefault(r => r.Id == id);
            return Task.FromResult(r is null ? null : new DeliveryRecord(r.Id, r.Sink, r.EventId, r.EventType, r.Subject, r.Status, r.Attempts));
        }
    }

    public Task<DeliveryRecord?> GetByEventForUpdateAsync(string sink, Guid eventId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var r = _rows.FirstOrDefault(r => r.Sink == sink && r.EventId == eventId);
            return Task.FromResult(r is null ? null : new DeliveryRecord(r.Id, r.Sink, r.EventId, r.EventType, r.Subject, r.Status, r.Attempts));
        }
    }

    public Task AcknowledgeAsync(
        Guid id, Acknowledgement acknowledgement, string source, string? status, string? failure, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var i = _rows.FindIndex(r => r.Id == id);
            _rows[i] = _rows[i] with
            {
                AckStatus = acknowledgement.Status,
                AckReference = acknowledgement.Reference,
                AckMessage = acknowledgement.Message,
                AckSource = source,
                AckAt = at,
                Status = status ?? _rows[i].Status,
                LastError = status == DeliveryStatuses.Failed ? failure : status == DeliveryStatuses.Delivered ? null : _rows[i].LastError,
            };
        }

        return Task.CompletedTask;
    }

    public Task ResolveAsync(Guid id, string status, string actor, string? note, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var i = _rows.FindIndex(r => r.Id == id);
            _rows[i] = _rows[i] with { Status = status, NextAttemptAt = at, ResolvedBy = actor, ResolveNote = note };
        }

        return Task.CompletedTask;
    }

    public Task<int> RetryFailedAsync(string sink, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var failed = _rows.Select((r, i) => (r, i)).Where(x => x.r.Sink == sink && x.r.Status == DeliveryStatuses.Failed).ToList();
            foreach (var (r, i) in failed)
            {
                _rows[i] = r with { Status = DeliveryStatuses.Pending, NextAttemptAt = at, ResolvedBy = actor };
            }

            return Task.FromResult(failed.Count);
        }
    }

    public Task<StoredFeedSettings?> GetAsync(string feed, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_feeds.GetValueOrDefault(feed));
        }
    }

    public Task<StoredFeedSettings> GetForUpdateAsync(string feed, DeliveryFeedSettings defaults, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_feeds.TryGetValue(feed, out var saved) ? saved : new StoredFeedSettings(defaults, 0, DateTimeOffset.UtcNow, null));
        }
    }

    public Task<long> SaveAsync(DeliveryFeedSettings settings, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = (_feeds.TryGetValue(settings.Feed, out var saved) ? saved.Version : 0) + 1;
            _feeds[settings.Feed] = new StoredFeedSettings(settings, version, at, actor);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<StoredFeedSettings>> ListAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<StoredFeedSettings>>([.. _feeds.Values.OrderBy(f => f.Settings.Feed, StringComparer.Ordinal)]);
        }
    }

    public Task<bool> InsertAsync(DeliveryFeedSettings settings, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_feeds.TryAdd(settings.Feed, new StoredFeedSettings(settings, 1, at, actor)));
        }
    }
}
