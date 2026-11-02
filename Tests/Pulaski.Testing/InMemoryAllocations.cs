using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>
/// In-memory lot allocations (D-064). A test sets each job's <see cref="Sources"/> (job, needs, lots); allocations written here are
/// added to the source a load returns. The Infrastructure tests cover the real queries.
/// </summary>
public sealed class InMemoryAllocations : IAllocationRepository, IAllocationReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, (LotAllocation Allocation, string JobNumber)> _allocations = [];

    private readonly Dictionary<Guid, (AllocationSettings Settings, long Version)> _settings = [];

    public Dictionary<Guid, JobAllocationSource> Sources { get; } = [];

    /// <summary>What <see cref="ListSitesDueForRefreshAsync"/> returns: a test says which sites are due.</summary>
    public List<Guid> DueSites { get; } = [];

    /// <summary>The lines <see cref="GetPickingListAsync"/> returns.</summary>
    public List<PickingLineDto> Picking { get; } = [];

    /// <summary>Sets a site's settings, as if saved.</summary>
    public void SetSettings(AllocationSettings settings)
    {
        lock (_lock)
        {
            var version = _settings.TryGetValue(settings.SiteId, out var current) ? current.Version + 1 : 1;
            _settings[settings.SiteId] = (settings, version);
        }
    }

    public IReadOnlyList<LotAllocation> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _allocations.Values.Select(a => a.Allocation)];
            }
        }
    }

    public Task<JobAllocationSource?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!Sources.TryGetValue(jobId, out var source))
            {
                return Task.FromResult<JobAllocationSource?>(null);
            }

            var active = _allocations.Values.Where(a => a.Allocation.IsActive).ToList();
            var held = active.Where(a => a.Allocation.JobId == jobId).Select(a => new HeldAllocation(a.Allocation, a.JobNumber)).ToList();
            var lots = source.Lots.Select(l => l with
            {
                Candidate = l.Candidate with
                {
                    AllocatedToJob = active.Where(a => a.Allocation.LotId == l.Candidate.LotId && a.Allocation.JobId == jobId).Sum(a => a.Allocation.Quantity),
                    AllocatedToOthers = l.Candidate.AllocatedToOthers
                        + active.Where(a => a.Allocation.LotId == l.Candidate.LotId && a.Allocation.JobId != jobId).Sum(a => a.Allocation.Quantity),
                },
            }).ToList();
            var preferences = source.Preferences ?? (_settings.TryGetValue(source.Job.SiteId, out var s) ? s.Settings.Preferences : null);
            return Task.FromResult<JobAllocationSource?>(source with { Allocations = held, Lots = lots, Preferences = preferences });
        }
    }

    public Task<IReadOnlyList<HeldAllocation>> LockActiveOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<HeldAllocation>>([.. _allocations.Values
                .Where(a => a.Allocation.IsActive && lotIds.Contains(a.Allocation.LotId)).Select(a => new HeldAllocation(a.Allocation, a.JobNumber))]);
        }
    }

    public Task<LotAllocation?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_allocations.TryGetValue(id, out var a) ? a.Allocation : null);
        }
    }

    public Task<IReadOnlyList<Guid>> ListOpenJobsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([.. Sources.Values.Where(s => s.Job.SiteId == siteId).Select(s => s.Job.Id)]);
        }
    }

    public Task InsertAsync(LotAllocation allocation, string actor, DateTimeOffset at, CancellationToken cancellationToken) => UpdateAsync(allocation, actor, at, cancellationToken);

    public Task UpdateAsync(LotAllocation allocation, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var number = Sources.TryGetValue(allocation.JobId, out var s) ? s.Job.JobNumber : "";
            _allocations[allocation.Id] = (allocation, number);
        }

        return Task.CompletedTask;
    }

    public Task<AllocationSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_settings.TryGetValue(siteId, out var s) ? s.Settings : AllocationSettings.Default(siteId));
        }
    }

    public Task<Versioned<AllocationSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_settings.TryGetValue(siteId, out var s))
            {
                s = _settings[siteId] = (AllocationSettings.Default(siteId), 0);
            }

            return Task.FromResult(new Versioned<AllocationSettings>(s.Settings, s.Version));
        }
    }

    public Task<long> UpdateSettingsAsync(AllocationSettings settings, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = (_settings.TryGetValue(settings.SiteId, out var s) ? s.Version : 0) + 1;
            _settings[settings.SiteId] = (settings with { RefreshedAt = s.Settings?.RefreshedAt }, version);
            return Task.FromResult(version);
        }
    }

    public Task<IReadOnlyList<Guid>> ListSitesDueForRefreshAsync(TimeSpan quiet, TimeSpan atMost, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Guid>>([.. DueSites]);
        }
    }

    public Task MarkRefreshedAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            // The refresh time has a row of its own, so it leaves the settings' version alone.
            var s = _settings.TryGetValue(siteId, out var current) ? current : (AllocationSettings.Default(siteId), 0);
            _settings[siteId] = (s.Item1 with { RefreshedAt = at }, s.Item2);
            DueSites.Remove(siteId);
        }

        return Task.CompletedTask;
    }

    Task<(AllocationSettings Settings, long Version)> IAllocationReadStore.GetSettingsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_settings.TryGetValue(siteId, out var s) ? s : (AllocationSettings.Default(siteId), 0L));
        }
    }

    public Task<PickingListDto> GetPickingListAsync(Guid siteId, Guid? jobId, Guid? lineId, CancellationToken cancellationToken) =>
        Task.FromResult(new PickingListDto
        {
            SiteId = siteId,
            SiteCode = "SITE",
            SiteTimeZone = "UTC",
            Lines = [.. Picking.Where(p => jobId is null || p.JobId == jobId)],
        });

    public Task<IReadOnlyList<LotAllocationDto>> ListOfLotAsync(Guid lotId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LotAllocationDto>>([]);

    public Task<IReadOnlyList<AllocatedStock>> ListAllocatedAsync(Guid siteId, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AllocatedStock>>([.. _allocations.Values
                .Where(a => a.Allocation.IsActive && a.Allocation.SiteId == siteId
                    && (materialIds.Contains(a.Allocation.MaterialId) || materialIds.Contains(a.Allocation.ComponentId)))
                .GroupBy(a => (a.Allocation.JobId, a.Allocation.ComponentId, a.Allocation.MaterialId))
                .Select(g => new AllocatedStock(
                    g.Key.JobId, g.Key.ComponentId, g.Sum(a => a.Allocation.Covers), g.Key.MaterialId, g.Sum(a => a.Allocation.Quantity)))]);
        }
    }
}
