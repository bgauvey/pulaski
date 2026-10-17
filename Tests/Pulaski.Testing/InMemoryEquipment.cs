using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Domain.EquipmentModel;

namespace Pulaski.Testing;

public sealed class InMemoryEquipment : IEquipmentRepository, IEquipmentReadStore
{
    private readonly Dictionary<Guid, Versioned<Equipment>> _rows = [];
    private readonly Lock _lock = new();

    /// <summary>Adds an enterprise → site → line chain and returns it.</summary>
    public (Equipment Enterprise, Equipment Site, Equipment Line) AddHierarchy()
    {
        var enterprise = Equipment.Create(Guid.CreateVersion7(), null, "ENT", "Enterprise", null, "enterprise", "enterprise", false, false, false, null);
        var site = Equipment.Create(Guid.CreateVersion7(), enterprise, "PLT1", "Plant 1", null, "site", "site", false, false, false, "America/Chicago");
        var line = Equipment.Create(Guid.CreateVersion7(), site, "EXT-01", "Line 1", null, "work_center", "production_line", true, true, false, null);
        foreach (var e in new[] { enterprise, site, line })
        {
            Put(e, version: 1);
        }

        return (enterprise, site, line);
    }

    public void Put(Equipment equipment, long version)
    {
        lock (_lock)
        {
            _rows[equipment.Id] = new Versioned<Equipment>(equipment, version);
        }
    }

    public Versioned<Equipment>? Find(Guid id)
    {
        lock (_lock)
        {
            return _rows.GetValueOrDefault(id);
        }
    }

    public Task<Versioned<Equipment>?> GetAsync(Guid id, RowLock rowLock, CancellationToken cancellationToken) =>
        Task.FromResult(Find(id));

    public Task<long> InsertAsync(Equipment equipment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        Put(equipment, version: 1);
        return Task.FromResult(1L);
    }

    public Task<long> UpdateAsync(Equipment equipment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = (Find(equipment.Id)?.Version ?? 0) + 1;
        Put(equipment, version);
        return Task.FromResult(version);
    }

    /// <summary>Locations a test says hold separate lots (D-031).</summary>
    public HashSet<Guid> WithSeparateLots { get; } = [];

    public Task<bool> HoldsSeparateLotsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(WithSeparateLots.Contains(equipmentId));

    /// <summary>How many materials a test says each location holds (D-032).</summary>
    public Dictionary<Guid, int> MaterialsHeld { get; } = [];

    public Task<bool> HoldsStockAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialsHeld.GetValueOrDefault(equipmentId) > 0 || WithSeparateLots.Contains(equipmentId));

    public Task<int> CountMaterialsHeldAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(MaterialsHeld.GetValueOrDefault(equipmentId));

    public Task<bool> IsInSubtreeAsync(Guid ancestorId, Guid descendantId, CancellationToken cancellationToken)
    {
        for (var node = Find(descendantId)?.Value; node is not null; node = node.ParentId is { } p ? Find(p)?.Value : null)
        {
            if (node.Id == ancestorId)
            {
                return Task.FromResult(true);
            }
        }

        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<EquipmentDto>> ListAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<EquipmentDto>>(
                [.. _rows.Values.Where(r => siteId is null || r.Value.SiteId == siteId).Select(ToDto)]);
        }
    }

    public Task<EquipmentDto?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Find(id) is { } row ? ToDto(row) : null);

    private EquipmentDto ToDto(Versioned<Equipment> row)
    {
        var e = row.Value;
        var depth = 0;
        for (var parent = e.ParentId; parent is { } p; parent = Find(p)?.Value.ParentId)
        {
            depth++;
        }

        return new EquipmentDto
        {
            Id = e.Id,
            ParentId = e.ParentId,
            SiteId = e.SiteId,
            Code = e.Code,
            Name = e.Name,
            Description = e.Description,
            Level = e.Level,
            EquipmentRole = e.EquipmentRole,
            TracksState = e.TracksState,
            IsSchedulable = e.IsSchedulable,
            CanRunJob = e.CanRunJob,
            HoldsInventory = e.HoldsInventory,
            ComminglesLots = e.ComminglesLots,
            SingleMaterial = e.SingleMaterial,
            TimeZone = e.TimeZone,
            IsActive = e.IsActive,
            Depth = depth,
            Version = row.Version,
        };
    }
}
