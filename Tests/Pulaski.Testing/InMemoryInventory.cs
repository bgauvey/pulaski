using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>
/// In-memory lots, movements and genealogy (D-027), with the locations, materials, jobs and
/// reasons the commands look up. The read side answers only what the handler tests need; the
/// Infrastructure tests cover the real queries.
/// </summary>
public sealed class InMemoryInventory : IInventoryRepository, IInventoryReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<Lot>> _lots = [];
    private readonly List<LotEventRow> _events = [];
    private readonly List<GenealogyEdge> _edges = [];
    private readonly Dictionary<Guid, InventoryLocation> _locations = [];
    private readonly Dictionary<Guid, InventoryMaterial> _materials = [];
    private readonly Dictionary<Guid, InventoryJob> _jobs = [];
    private readonly Dictionary<Guid, InventoryReason> _reasons = [];
    private readonly HashSet<(Guid Job, Guid Material)> _planned = [];
    private long _next = 1;

    public IReadOnlyList<LotEventRow> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<GenealogyEdge> Edges
    {
        get
        {
            lock (_lock)
            {
                return [.. _edges];
            }
        }
    }

    public InventoryLocation AddLocation(string code, Guid siteId, bool holdsInventory = true, bool isActive = true)
    {
        var location = new InventoryLocation { Id = Guid.CreateVersion7(), Code = code, SiteId = siteId, HoldsInventory = holdsInventory, IsActive = isActive };
        lock (_lock)
        {
            _locations[location.Id] = location;
        }

        return location;
    }

    public InventoryMaterial AddMaterial(string code, Guid baseUomId, string baseUomCode, bool isLotTracked = true, bool isActive = true)
    {
        var material = new InventoryMaterial
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            BaseUomId = baseUomId,
            BaseUomCode = baseUomCode,
            IsLotTracked = isLotTracked,
            IsActive = isActive,
        };
        lock (_lock)
        {
            _materials[material.Id] = material;
        }

        return material;
    }

    public InventoryJob AddJob(string number, Guid siteId, Guid? materialId, string status = "planned", string jobType = "production")
    {
        var job = new InventoryJob { Id = Guid.CreateVersion7(), SiteId = siteId, JobNumber = number, JobType = jobType, Status = status, MaterialId = materialId };
        lock (_lock)
        {
            _jobs[job.Id] = job;
        }

        return job;
    }

    public InventoryReason AddReason(string kind, string code, bool requiresComment = false, Guid? siteId = null, bool isActive = true)
    {
        var reason = new InventoryReason
        {
            Id = Guid.CreateVersion7(),
            SiteId = siteId,
            Kind = kind,
            Code = code,
            Name = code,
            IsSelectable = true,
            RequiresComment = requiresComment,
            IsActive = isActive,
        };
        lock (_lock)
        {
            _reasons[reason.Id] = reason;
        }

        return reason;
    }

    public void PlanComponent(Guid jobId, Guid materialId)
    {
        lock (_lock)
        {
            _planned.Add((jobId, materialId));
        }
    }

    public Versioned<Lot>? Find(Guid id)
    {
        lock (_lock)
        {
            return _lots.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<Lot> Lots
    {
        get
        {
            lock (_lock)
            {
                return [.. _lots.Values.Select(l => l.Value)];
            }
        }
    }

    // ---- Repository ----

    public Task<Versioned<Lot>?> GetLotForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(id));

    public Task<Versioned<Lot>?> FindLotForUpdateAsync(Guid siteId, Guid materialId, string lotNumber, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_lots.Values.FirstOrDefault(l =>
                l.Value.SiteId == siteId && l.Value.MaterialId == materialId && l.Value.LotNumber == lotNumber));
        }
    }

    public Task<Versioned<Lot>?> FindBulkLotForUpdateAsync(Guid materialId, Guid locationId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_lots.Values.FirstOrDefault(l =>
                l.Value.IsBulk && l.Value.MaterialId == materialId && l.Value.LocationId == locationId));
        }
    }

    public Task<long> InsertLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Store(lot));

    public Task<long> UpdateLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Store(lot));

    public Task InsertEventAsync(LotEventRow row, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _events.Add(row);
        }

        return Task.CompletedTask;
    }

    public Task<InventoryLocation?> GetLocationAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_locations.GetValueOrDefault(id));
        }
    }

    public Task<InventoryMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.GetValueOrDefault(id));
        }
    }

    public Task<InventoryJob?> GetJobForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<Guid>> ListLotsProducedByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(LotsOfJob(jobId, LotEventKind.Produced));

    public Task<IReadOnlyList<Guid>> ListLotsConsumedByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(LotsOfJob(jobId, LotEventKind.Consumed));

    public Task InsertGenealogyAsync(IReadOnlyList<GenealogyEdge> edges, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _edges.AddRange(edges.Where(e => !_edges.Any(x => x.ParentLotId == e.ParentLotId && x.ChildLotId == e.ChildLotId)));
        }

        return Task.CompletedTask;
    }

    public Task<InventoryReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_reasons.GetValueOrDefault(id));
        }
    }

    public Task<bool> PlansComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_planned.Contains((jobId, materialId)));
        }
    }

    // ---- Read store: only what the handler tests use ----

    public Task<IReadOnlyList<MaterialStockDto>> ListStockAsync(StockFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialStockDto>>([]);

    public Task<MaterialStockDetailDto?> GetMaterialStockAsync(Guid materialId, Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<MaterialStockDetailDto?>(null);

    public Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(LotFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LotSummaryDto>>([]);

    public Task<LotDto?> GetLotAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<LotDto?>(null);

    public Task<IReadOnlyList<LotTraceNodeDto>> TraceAsync(Guid lotId, bool forward, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LotTraceNodeDto>>([]);

    public Task<WhereUsedDto?> WhereUsedAsync(Guid materialId, CancellationToken cancellationToken) => Task.FromResult<WhereUsedDto?>(null);

    public Task<IReadOnlyList<InventoryLocationDto>> ListLocationsAsync(Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventoryLocationDto>>([]);

    public Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(Guid? siteId, string? search, Guid? materialId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeclarableJobDto>>([]);

    public Task<JobMaterialsDto?> GetJobMaterialsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<JobMaterialsDto?>(null);

    public Task<IReadOnlyList<ReasonCodeDto>> ListReasonCodesAsync(string kind, Guid? siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ReasonCodeDto>>([.. _reasons.Values
                .Where(r => r.Kind == kind && r.IsActive && (r.SiteId is null || r.SiteId == siteId))
                .Select(r => new ReasonCodeDto { Id = r.Id, Kind = r.Kind, Code = r.Code, Name = r.Name, RequiresComment = r.RequiresComment })]);
        }
    }

    private long Store(Lot lot)
    {
        lock (_lock)
        {
            var version = ++_next;
            _lots[lot.Id] = new(lot, version);
            return version;
        }
    }

    private IReadOnlyList<Guid> LotsOfJob(Guid jobId, string kind)
    {
        lock (_lock)
        {
            return [.. _events.Where(e => e.JobId == jobId && e.Movement.Kind == kind).Select(e => e.LotId).Distinct()];
        }
    }
}
