using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;

namespace Pulaski.Testing;

/// <summary>In-memory OEE targets (D-073). Every site and the equipment the test names are known; versions count changes.</summary>
public sealed class InMemoryOeeTargets : IOeeTargetRepository, IOeeTargetReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (OeeTarget Target, long Version)> _targets = [];

    /// <summary>Equipment of each site that may have targets; any site is known.</summary>
    public Dictionary<Guid, Guid> EquipmentSite { get; } = [];

    public Task<bool> LockScopeAsync(Guid siteId, Guid? equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(equipmentId is not { } id || (EquipmentSite.TryGetValue(id, out var site) && site == siteId));
        }
    }

    public Task<IReadOnlyList<Versioned<OeeTarget>>> ListScopeAsync(Guid siteId, Guid? equipmentId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Versioned<OeeTarget>>>([.. _targets.Values
                .Where(t => t.Target.SiteId == siteId && t.Target.EquipmentId == equipmentId)
                .OrderBy(t => t.Target.EffectiveFrom)
                .Select(t => new Versioned<OeeTarget>(t.Target, t.Version))]);
        }
    }

    public Task InsertAsync(OeeTarget target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _targets[target.Id] = (target, 1);
            return Task.CompletedTask;
        }
    }

    public Task UpdateAsync(OeeTarget target, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _targets[target.Id] = (target, _targets[target.Id].Version + 1);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<OeeTargetDto>> ListAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<OeeTargetDto>>([.. _targets.Values
                .Where(t => t.Target.SiteId == siteId)
                .OrderBy(t => t.Target.EquipmentId is not null).ThenByDescending(t => t.Target.EffectiveFrom)
                .Select(t => new OeeTargetDto
                {
                    Id = t.Target.Id,
                    SiteId = t.Target.SiteId,
                    EquipmentId = t.Target.EquipmentId,
                    EffectiveFrom = t.Target.EffectiveFrom,
                    EffectiveTo = t.Target.EffectiveTo,
                    Targets = new(t.Target.Values.AvailabilityPct, t.Target.Values.PerformancePct, t.Target.Values.QualityPct, t.Target.Values.OeePct),
                    Version = t.Version,
                })]);
        }
    }
}
