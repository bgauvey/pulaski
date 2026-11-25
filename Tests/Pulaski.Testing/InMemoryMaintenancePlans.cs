using Pulaski.Application.Abstractions;
using Pulaski.Application.Maintenance;
using Pulaski.Contracts.Maintenance;
using Pulaski.Domain.Maintenance;

namespace Pulaski.Testing;

/// <summary>In-memory preventive plans (#127d): the test sets each equipment's meter readings.</summary>
public sealed class InMemoryMaintenancePlans : IMaintenancePlanRepository, IMaintenancePlanReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (MaintenancePlan Plan, long Version)> _plans = [];
    private long _version;

    /// <summary>What a meter reads for an equipment, whatever the period.</summary>
    public Dictionary<(Guid EquipmentId, string Meter), decimal> Readings { get; } = [];

    public Dictionary<Guid, PlanLastRequest> LastRequests { get; } = [];

    public Task<Versioned<MaintenancePlan>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_plans.TryGetValue(id, out var p) ? new Versioned<MaintenancePlan>(p.Plan, p.Version) : null);
        }
    }

    public Task<long> InsertAsync(MaintenancePlan plan, DateTimeOffset at, string by, CancellationToken cancellationToken) => UpdateAsync(plan, at, cancellationToken);

    public Task<long> UpdateAsync(MaintenancePlan plan, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _plans[plan.Id] = (plan, ++_version);
            return Task.FromResult(_version);
        }
    }

    public Task<IReadOnlyList<Guid>> ListActiveIdsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<Guid> ids = [.. _plans.Values.Where(p => p.Plan.IsActive).Select(p => p.Plan.Id)];
            return Task.FromResult(ids);
        }
    }

    public Task<PlanLastRequest?> GetLastRequestAsync(Guid planId, CancellationToken cancellationToken) => Task.FromResult(LastRequests.GetValueOrDefault(planId));

    public Task<decimal> ReadMeterAsync(Guid equipmentId, string meter, DateTimeOffset from, DateTimeOffset until, CancellationToken cancellationToken) =>
        Task.FromResult(Readings.GetValueOrDefault((equipmentId, meter)));

    public Task<IReadOnlyList<MaintenancePlanDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            IReadOnlyList<MaintenancePlanDto> list = [.. _plans.Values.Where(p => p.Plan.SiteId == siteId).Select(p => new MaintenancePlanDto
            {
                Id = p.Plan.Id,
                SiteId = p.Plan.SiteId,
                EquipmentId = p.Plan.EquipmentId,
                EquipmentCode = "",
                Name = p.Plan.Name,
                Meter = p.Plan.Meter,
                Interval = p.Plan.Interval,
                Priority = p.Plan.Priority,
                IsActive = p.Plan.IsActive,
                StartedAt = p.Plan.StartedAt,
                Version = p.Version,
            })];
            return Task.FromResult(list);
        }
    }
}
