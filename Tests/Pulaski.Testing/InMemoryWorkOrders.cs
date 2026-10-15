using Pulaski.Application.Abstractions;
using Pulaski.Application.ReferenceData;
using Pulaski.Application.WorkOrders;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Testing;

public sealed class InMemoryWorkOrders : IWorkOrderRepository, IWorkOrderReadStore
{
    private readonly Dictionary<Guid, Versioned<WorkOrder>> _rows = [];
    private readonly Lock _lock = new();

    public WorkOrder Add(string status, long version = 10)
    {
        var planned = WorkOrder.CreateLocal(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "WO-" + Guid.NewGuid().ToString("N")[..6],
            Guid.CreateVersion7(), 100, Guid.CreateVersion7(), null, null, null, null, 0);
        var workOrder = planned with { Status = status };
        lock (_lock)
        {
            _rows[workOrder.Id] = new Versioned<WorkOrder>(workOrder, version);
        }

        return workOrder;
    }

    public Versioned<WorkOrder>? Find(Guid id)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault(id);
        }
    }

    public Task<Versioned<WorkOrder>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Find(id));

    public Task<Versioned<WorkOrder>?> GetForShareAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Find(id));

    /// <summary>Answers <see cref="HasOpenJobsAsync"/>; <see cref="InMemoryStores"/> points it at its scheduling store.</summary>
    public Func<Guid, bool> OpenJobs { get; set; } = _ => false;

    public Task<bool> HasOpenJobsAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(OpenJobs(id));

    public Task<long> InsertAsync(WorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _rows[workOrder.Id] = new Versioned<WorkOrder>(workOrder, 1);
        }

        return Task.FromResult(1L);
    }

    public Task<long> UpdateStatusAsync(Guid id, string status, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var current = _rows[id];
            var next = new Versioned<WorkOrder>(current.Value with { Status = status }, current.Version + 1);
            _rows[id] = next;
            return Task.FromResult(next.Version);
        }
    }

    public Task<IReadOnlyList<WorkOrderDto>> ListAsync(Guid? siteId, string? status, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<WorkOrderDto>>(
                [.. _rows.Values.Where(r => status is null || r.Value.Status == status).Select(ToDto)]);
        }
    }

    /// <summary>The equipment a progress query sees for a selection; tests set it up.</summary>
    public Func<Guid?, IReadOnlyList<ScopeEquipmentRow>> Scope { get; set; } = _ => [];

    /// <summary>Good quantity and open job count per order; orders not listed have neither.</summary>
    public Dictionary<Guid, WorkOrderFactsRow> Facts { get; } = [];

    public Task<IReadOnlyList<ScopeEquipmentRow>> GetScopeAsync(Guid? equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Scope(equipmentId));

    public Task<IReadOnlyList<WorkOrderFactsRow>> GetFactsAsync(Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkOrderFactsRow>>([.. Facts.Values]);

    public Task<WorkOrderDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Find(id) is { } row ? ToDto(row) : null);

    private static WorkOrderDto ToDto(Versioned<WorkOrder> row)
    {
        var w = row.Value;
        return new WorkOrderDto
        {
            Id = w.Id,
            SiteId = w.SiteId,
            SiteCode = "PLT1",
            OrderNumber = w.OrderNumber,
            MaterialId = w.MaterialId,
            MaterialCode = "MAT",
            MaterialName = "Material",
            PlannedQty = w.PlannedQty,
            UomId = w.UomId,
            UomCode = "m",
            TargetEquipmentId = w.TargetEquipmentId,
            PlannedStart = w.PlannedStart,
            PlannedEnd = w.PlannedEnd,
            DueAt = w.DueAt,
            Priority = w.Priority,
            Status = w.Status,
            Origin = w.Origin,
            Version = row.Version,
        };
    }
}

public sealed class InMemoryMaterials : IMaterialRepository, IReferenceDataReadStore
{
    private readonly HashSet<Guid> _active = [];

    public Guid AddActive()
    {
        var id = Guid.CreateVersion7();
        lock (_active)
        {
            _active.Add(id);
        }

        return id;
    }

    public Task<bool> IsActiveAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_active)
        {
            return Task.FromResult(_active.Contains(id));
        }
    }

    public Task<IReadOnlyList<UomDto>> ListUomsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<UomDto>>([]);

    public Task<IReadOnlyList<MaterialDto>> ListMaterialsAsync(bool includeInactive, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialDto>>([]);
}
