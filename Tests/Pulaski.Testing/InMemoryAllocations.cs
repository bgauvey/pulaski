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

    public Dictionary<Guid, JobAllocationSource> Sources { get; } = [];

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
            return Task.FromResult<JobAllocationSource?>(source with { Allocations = held, Lots = lots });
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

    public Task<IReadOnlyList<LotAllocationDto>> ListOfLotAsync(Guid lotId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<LotAllocationDto>>([]);

    public Task<IReadOnlyList<AllocatedStock>> ListAllocatedAsync(Guid siteId, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<AllocatedStock>>([.. _allocations.Values
                .Where(a => a.Allocation.IsActive && a.Allocation.SiteId == siteId && materialIds.Contains(a.Allocation.MaterialId))
                .GroupBy(a => (a.Allocation.JobId, a.Allocation.MaterialId))
                .Select(g => new AllocatedStock(g.Key.JobId, g.Key.MaterialId, g.Sum(a => a.Allocation.Quantity)))]);
        }
    }
}
