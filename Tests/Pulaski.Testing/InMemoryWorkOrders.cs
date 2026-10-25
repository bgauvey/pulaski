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

    public WorkOrder Add(string status, long version = 10, Guid? siteId = null)
    {
        var planned = WorkOrder.CreateLocal(
            Guid.CreateVersion7(), siteId ?? Guid.CreateVersion7(), "WO-" + Guid.NewGuid().ToString("N")[..6],
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

    public Func<Guid, ShopOrderJobsRow?> ShopOrderJobs { get; set; } = _ => null;

    public Task<ShopOrderJobsRow?> GetShopOrderJobsAsync(Guid workOrderId, CancellationToken cancellationToken) =>
        Task.FromResult(ShopOrderJobs(workOrderId));

    // ERP import lookups (D-066): codes a test registers.
    public Dictionary<string, Guid> Sites { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Guid> MaterialCodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, Guid> UomCodes { get; } = [];

    /// <summary>Processes by site and material that an ERP order may be made from.</summary>
    public Dictionary<(Guid SiteId, Guid MaterialId), List<(Guid Id, string Code)>> Processes { get; } = [];

    public Dictionary<Guid, string> SiteErpBatchRules { get; } = [];

    public Dictionary<Guid, string?> MaterialErpBatchRules { get; } = [];

    public Task<Guid?> FindByExternalKeyAsync(string externalSystem, string externalKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_rows.Values.FirstOrDefault(r => r.Value.ExternalSystem == externalSystem && r.Value.ExternalKey == externalKey)?.Value.Id);
        }
    }

    public Task<bool> NumberExistsAsync(Guid siteId, string orderNumber, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_rows.Values.Any(r => r.Value.SiteId == siteId && r.Value.OrderNumber == orderNumber));
        }
    }

    public Task<long> UpdateFromErpAsync(WorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var next = new Versioned<WorkOrder>(workOrder, _rows[workOrder.Id].Version + 1);
            _rows[workOrder.Id] = next;
            return Task.FromResult(next.Version);
        }
    }

    public Task<string> GetErpBatchRuleAsync(Guid siteId, Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialErpBatchRules.GetValueOrDefault(materialId) ?? SiteErpBatchRules.GetValueOrDefault(siteId) ?? ErpBatchRule.Reject);

    public Task SetSiteErpBatchRuleAsync(Guid siteId, string rule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        SiteErpBatchRules[siteId] = rule;
        return Task.CompletedTask;
    }

    public Task<long?> SetMaterialErpBatchRuleAsync(Guid materialId, string? rule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        MaterialErpBatchRules[materialId] = rule;
        return Task.FromResult<long?>(2);
    }

    public Task<Guid?> FindMaterialAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialCodes.TryGetValue(code, out var id) ? id : (Guid?)null);

    public Task<Guid?> FindUomAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(UomCodes.TryGetValue(code, out var id) ? id : (Guid?)null);

    public Task<string?> GetUomCodeAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<string?>(UomCodes.FirstOrDefault(u => u.Value == id).Key);

    public Task<Guid?> FindSiteAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(Sites.TryGetValue(code, out var id) ? id : (Guid?)null);

    public Task<IReadOnlyList<(Guid Id, string Code)>> FindProcessesAsync(Guid siteId, Guid materialId, string? processCode, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<(Guid Id, string Code)>>([.. (Processes.GetValueOrDefault((siteId, materialId)) ?? [])
            .Where(p => processCode is null || string.Equals(p.Code, processCode, StringComparison.OrdinalIgnoreCase))]);

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
            ProcessVersionId = w.ProcessVersionId,
            Notes = w.Notes,
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

    public Task<IReadOnlyList<MaterialClassDto>> ListMaterialClassesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialClassDto>>([]);
}
