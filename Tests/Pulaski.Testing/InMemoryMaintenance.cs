using Pulaski.Application.Maintenance;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Maintenance;

namespace Pulaski.Testing;

/// <summary>In-memory maintenance requests (#127): the test adds the equipment and stops they are raised for.</summary>
public sealed class InMemoryMaintenance : IMaintenanceRepository, IMaintenanceReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, MaintenanceRequest> _requests = [];
    private long _number;

    public Dictionary<Guid, MaintenanceEquipment> Equipment { get; } = [];

    public Dictionary<Guid, MaintenanceStop> Stops { get; } = [];

    public Task<MaintenanceEquipment?> LockEquipmentAsync(Guid equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Equipment.GetValueOrDefault(equipmentId));
        }
    }

    public Task<MaintenanceStop?> GetStopAsync(Guid id, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Stops.TryGetValue(id, out var stop) && stop.StartedAt == startedAt ? stop : null);
        }
    }

    public Task<MaintenanceRequest?> FindOpenForStopForUpdateAsync(Guid downtimeId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_requests.Values.FirstOrDefault(r => r.DowntimeId == downtimeId && !r.IsClosed));
        }
    }

    public Task<MaintenanceRequest?> FindByNumberForUpdateAsync(string number, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_requests.Values.FirstOrDefault(r => r.Number == number));
        }
    }

    public Task<long> NextNumberAsync(CancellationToken cancellationToken) => Task.FromResult(Interlocked.Increment(ref _number));

    public Task<MaintenanceRequest?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_requests.GetValueOrDefault(id));
        }
    }

    public Task InsertAsync(MaintenanceRequest request, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _requests[request.Id] = request;
        }

        return Task.CompletedTask;
    }

    public Task UpdateAsync(MaintenanceRequest request, DateTimeOffset at, CancellationToken cancellationToken) => InsertAsync(request, at, cancellationToken);

    public Task<IReadOnlyList<MaintenanceRequestDto>> ListAsync(
        Guid siteId, bool openOnly, DateTimeOffset? from, DateTimeOffset? until, Guid? equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<MaintenanceRequestDto> list = [.. _requests.Values
                .Where(r => r.SiteId == siteId && (equipmentId is null || r.EquipmentId == equipmentId))
                .Where(r => openOnly ? !r.IsClosed : (from is null || r.RaisedAt >= from) && (until is null || r.RaisedAt < until))
                .OrderByDescending(r => r.RaisedAt)
                .Select(r => new MaintenanceRequestDto
                {
                    Id = r.Id,
                    SiteId = r.SiteId,
                    Number = r.Number,
                    EquipmentId = r.EquipmentId,
                    EquipmentCode = Equipment.TryGetValue(r.EquipmentId, out var e) ? e.Code : "",
                    Status = r.Status,
                    Priority = r.Priority,
                    Description = r.Description,
                    RaisedAt = r.RaisedAt,
                    RaisedBy = r.RaisedBy,
                    RaisedFrom = r.RaisedFrom,
                    DowntimeId = r.DowntimeId,
                    DowntimeStartedAt = r.DowntimeStartedAt,
                    StartedAt = r.StartedAt,
                    StartedBy = r.StartedBy,
                    ClosedAt = r.ClosedAt,
                    ClosedBy = r.ClosedBy,
                    Resolution = r.Resolution,
                    ExternalSystem = r.ExternalSystem,
                    ExternalReference = r.ExternalReference,
                })];
            return Task.FromResult(list);
        }
    }
}
