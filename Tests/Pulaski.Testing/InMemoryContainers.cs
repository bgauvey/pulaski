using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>In-memory containers (D-059); their lots live in <see cref="InMemoryInventory"/>.</summary>
public sealed class InMemoryContainers(InMemoryInventory inventory) : IContainerRepository, IContainerReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (Versioned<Container> Container, long LabelNo)> _containers = [];
    private long _next = 1000;

    public Container Get(Guid id)
    {
        lock (_lock)
        {
            return _containers[id].Container.Value;
        }
    }

    public long LabelOf(Guid id)
    {
        lock (_lock)
        {
            return _containers[id].LabelNo;
        }
    }

    public Task<Versioned<Container>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_containers.TryGetValue(id, out var c) ? c.Container : null);
        }
    }

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_containers.Values.Any(c => c.Container.Value.SiteId == siteId && c.Container.Value.Code == code));
        }
    }

    public Task<long> NextLabelNoAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(++_next);
        }
    }

    public Task<long> InsertAsync(Container container, long labelNo, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _containers[container.Id] = (new(container, version), labelNo);
            return Task.FromResult(version);
        }
    }

    public Task<long> UpdateAsync(Container container, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _containers[container.Id] = (new(container, version), _containers[container.Id].LabelNo);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<Guid>> ListLotIdsAsync(Guid containerId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. inventory.Lots
            .Where(l => l.ContainerId == containerId && l.Quantity > 0 && LotStatus.IsOnHand(l.Status))
            .Select(l => l.Id)
            .Order()]);

    public Task<IReadOnlyList<ContainerDto>> ListAsync(Guid siteId, string? search, bool includeRetired, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ContainerDto>>([]);

    public Task<ContainerDto?> GetAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<ContainerDto?>(null);

    /// <summary>Lots and containers by label number only; enough for the scan handler's tests.</summary>
    public Task<IReadOnlyList<ScanMatchDto>> FindAsync(Guid siteId, ScanCode code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<ScanMatchDto> matches = code.Kind switch
            {
                ScanCode.Container => [.. _containers.Values
                    .Where(c => c.LabelNo == code.LabelNo && c.Container.Value.SiteId == siteId)
                    .Select(c => new ScanMatchDto { Kind = ScanCode.Container, Id = c.Container.Value.Id, Label = c.Container.Value.Code })],
                ScanCode.FreeText => [.. _containers.Values
                    .Where(c => c.Container.Value.SiteId == siteId && string.Equals(c.Container.Value.Code, code.Text, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new ScanMatchDto { Kind = ScanCode.Container, Id = c.Container.Value.Id, Label = c.Container.Value.Code, MatchedBy = "container code" }),
                    .. inventory.Lots
                    .Where(l => l.SiteId == siteId && string.Equals(l.LotNumber, code.Text, StringComparison.OrdinalIgnoreCase))
                    .Select(l => new ScanMatchDto { Kind = ScanCode.Lot, Id = l.Id, Label = l.LotNumber!, MatchedBy = "lot number" })],
                _ => [],
            };
            return Task.FromResult(matches);
        }
    }
}
