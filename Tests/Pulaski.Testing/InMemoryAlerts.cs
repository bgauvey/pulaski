using Pulaski.Application.Abstractions;
using Pulaski.Application.Alerts;
using Pulaski.Contracts.Alerts;
using Pulaski.Domain.Alerts;

namespace Pulaski.Testing;

/// <summary>In-memory alert subscriptions (#121): every equipment id is its own site; nothing is ever due.</summary>
public sealed class InMemoryAlerts : IAlertRepository, IAlertReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (AlertSubscription Subscription, long Version)> _subscriptions = [];
    private readonly HashSet<(Guid, Guid)> _sent = [];
    private long _version;

    public Task<Guid?> SiteOfAsync(Guid equipmentId, CancellationToken cancellationToken) => Task.FromResult<Guid?>(equipmentId);

    public Task<bool> ExistsAsync(Guid userId, Guid siteId, Guid? equipmentId, string kind, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_subscriptions.Values.Any(s =>
                s.Subscription.UserId == userId && s.Subscription.SiteId == siteId && s.Subscription.EquipmentId == equipmentId && s.Subscription.Kind == kind));
        }
    }

    public Task<Versioned<AlertSubscription>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_subscriptions.TryGetValue(id, out var s) ? new Versioned<AlertSubscription>(s.Subscription, s.Version) : null);
        }
    }

    public Task<long> InsertAsync(AlertSubscription subscription, DateTimeOffset at, CancellationToken cancellationToken) => UpdateAsync(subscription, at, cancellationToken);

    public Task<long> UpdateAsync(AlertSubscription subscription, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _subscriptions[subscription.Id] = (subscription, ++_version);
            return Task.FromResult(_version);
        }
    }

    public Task<bool> MarkSentAsync(Guid subscriptionId, Guid subjectId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sent.Add((subscriptionId, subjectId)));
        }
    }

    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<AlertSubscriptionDto>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AlertSubscriptionDto>>([.. _subscriptions.Values.Where(s => s.Subscription.UserId == userId).Select(s => new AlertSubscriptionDto
            {
                Id = s.Subscription.Id,
                SiteId = s.Subscription.SiteId,
                SiteCode = "",
                EquipmentId = s.Subscription.EquipmentId,
                Kind = s.Subscription.Kind,
                Minutes = s.Subscription.Minutes,
                IsActive = s.Subscription.IsActive,
                Version = s.Version,
            })]);
        }
    }

    public Task<IReadOnlyList<AlertSubscriber>> ListSubscribersAsync(string kind, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AlertSubscriber>>([]);

    public Task<IReadOnlyList<AlertDue>> ListDueAsync(DateTimeOffset since, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AlertDue>>([]);
}
