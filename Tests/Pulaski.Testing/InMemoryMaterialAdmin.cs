using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;

namespace Pulaski.Testing;

/// <summary>In-memory materials, classes and units for the materials-management handlers (D-033).</summary>
public sealed class InMemoryMaterialAdmin : IMaterialAdminRepository, IMaterialReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<Material>> _materials = [];
    private readonly Dictionary<Guid, Versioned<MaterialClass>> _classes = [];
    private readonly Dictionary<string, (Guid Id, bool IsActive)> _uoms = new(StringComparer.Ordinal);
    private long _next = 1;

    /// <summary>What a test says uses each material.</summary>
    public Dictionary<Guid, MaterialUse> Use { get; } = [];

    public Guid AddUom(string code, bool isActive = true, Guid? id = null)
    {
        var uomId = id ?? Guid.CreateVersion7();
        lock (_lock)
        {
            _uoms[code] = (uomId, isActive);
        }

        return uomId;
    }

    public MaterialClass AddClass(string code, bool isActive = true)
    {
        var c = new MaterialClass(Guid.CreateVersion7(), code, code, isActive);
        lock (_lock)
        {
            _classes[c.Id] = new(c, ++_next);
        }

        return c;
    }

    public Versioned<Material>? Find(Guid id)
    {
        lock (_lock)
        {
            return _materials.GetValueOrDefault(id);
        }
    }

    public Versioned<MaterialClass>? FindClass(Guid id)
    {
        lock (_lock)
        {
            return _classes.GetValueOrDefault(id);
        }
    }

    public Task<Versioned<Material>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(id));

    public Task<Versioned<Material>?> FindByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.Values.FirstOrDefault(m => m.Value.ExternalSystem == externalSystem && m.Value.ExternalKey == externalKey));
        }
    }

    public Task<Versioned<Material>?> FindByCodeForUpdateAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.Values.FirstOrDefault(m => m.Value.Code == code));
        }
    }

    public Task<MaterialClass?> FindClassByCodeAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_classes.Values.Select(c => c.Value).FirstOrDefault(c => c.Code == code));
        }
    }

    public Task<long> InsertAsync(Material material, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_materials.Values.Any(m => m.Value.Code == material.Code))
            {
                throw new ConflictException("That material code is already used.");
            }
        }

        return UpdateAsync(material, at, cancellationToken);
    }

    public Task<long> UpdateAsync(Material material, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _materials[material.Id] = new(material, version);
            return Task.FromResult(version);
        }
    }

    public Task<MaterialUse> GetUseAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Use.GetValueOrDefault(id, MaterialUse.None));
        }
    }

    public Task<IReadOnlyList<string>> DescribeUseAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<string>>(Use.GetValueOrDefault(id, MaterialUse.None).InUse ? ["2 open jobs"] : []);

    public Task<(Guid Id, bool IsActive)?> FindUomAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<(Guid Id, bool IsActive)?>(_uoms.TryGetValue(code, out var u) ? u : null);
        }
    }

    public Task<bool> IsUomActiveAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_uoms.Values.Any(u => u.Id == id && u.IsActive));
        }
    }

    public Task<MaterialClass?> GetClassAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(FindClass(id)?.Value);

    public Task<Versioned<MaterialClass>?> GetClassForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(FindClass(id));

    public Task<long> InsertClassAsync(MaterialClass materialClass, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpdateClassAsync(materialClass, at, cancellationToken);

    public Task<long> UpdateClassAsync(MaterialClass materialClass, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _classes[materialClass.Id] = new(materialClass, version);
            return Task.FromResult(version);
        }
    }

    public Task<MaterialDetailDto?> GetDetailAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<MaterialDetailDto?>(null);
}
