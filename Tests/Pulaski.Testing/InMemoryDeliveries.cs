using Pulaski.Application.Integrations;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Integrations;

namespace Pulaski.Testing;

/// <summary>In-memory outbound deliveries (D-061); queuing and sending are covered by the Infrastructure tests.</summary>
public sealed class InMemoryDeliveries : IDeliveryStore
{
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

    public Task<IReadOnlyList<DeliveryDto>> ListAsync(string sink, string? status, int limit, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<DeliveryDto>>([.. _rows.Where(r => r.Sink == sink && (status is null || r.Status == status)).Take(limit)]);
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
}
