using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain.Units;

namespace Pulaski.Testing;

/// <summary>In-memory units and material conversions (D-026).</summary>
public sealed class InMemoryUnits : IUnitReadStore, IUnitRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<UnitOfMeasure>> _units = [];
    private readonly Dictionary<(Guid Material, Guid From, Guid To), (decimal Factor, bool Active)> _conversions = [];
    private readonly HashSet<Guid> _materials = [];
    private long _next = 1;

    public Unit AddUnit(string code, string dimension, decimal? toBaseFactor)
    {
        var unit = new UnitOfMeasure(Guid.CreateVersion7(), code, code, dimension, toBaseFactor, true);
        lock (_lock)
        {
            _units[unit.Id] = new(unit, ++_next);
        }

        return new Unit(unit.Id, code, dimension, toBaseFactor);
    }

    public void AddMaterial(Guid materialId)
    {
        lock (_lock)
        {
            _materials.Add(materialId);
        }
    }

    public Versioned<UnitOfMeasure>? Find(Guid id)
    {
        lock (_lock)
        {
            return _units.GetValueOrDefault(id);
        }
    }

    public Task<IReadOnlyList<MaterialConversionDto>> ListConversionsAsync(Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MaterialConversionDto>>([.. _conversions
                .Where(c => c.Key.Material == materialId && c.Value.Active)
                .Select(c => new MaterialConversionDto
                {
                    MaterialId = materialId,
                    FromUomId = c.Key.From,
                    FromUomCode = _units[c.Key.From].Value.Code,
                    ToUomId = c.Key.To,
                    ToUomCode = _units[c.Key.To].Value.Code,
                    Factor = c.Value.Factor,
                })]);
        }
    }

    public Task<IReadOnlyList<Unit>> GetUnitsAsync(CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Unit>>([.. _units.Values.Select(u => new Unit(u.Value.Id, u.Value.Code, u.Value.Dimension, u.Value.ToBaseFactor))]);
        }
    }

    public Task<IReadOnlyList<MaterialConversionRow>> GetConversionsAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<MaterialConversionRow>>([.. _conversions
                .Where(c => materialIds.Contains(c.Key.Material) && c.Value.Active)
                .Select(c => new MaterialConversionRow { MaterialId = c.Key.Material, FromUomId = c.Key.From, ToUomId = c.Key.To, Factor = c.Value.Factor })]);
        }
    }

    public Task<Versioned<UnitOfMeasure>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(id));

    public Task<long> InsertAsync(UnitOfMeasure unit, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_units.Values.Any(u => u.Value.Code == unit.Code))
            {
                throw new ConflictException($"Unit {unit.Code} already exists.");
            }

            var version = ++_next;
            _units[unit.Id] = new(unit, version);
            return Task.FromResult(version);
        }
    }

    public Task<long> UpdateAsync(UnitOfMeasure unit, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _units[unit.Id] = new(unit, version);
            return Task.FromResult(version);
        }
    }

    public Task<bool> LockMaterialAsync(Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.Contains(materialId));
        }
    }

    public Task SetConversionAsync(Guid materialId, MaterialConversion conversion, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _conversions[(materialId, conversion.FromUomId, conversion.ToUomId)] = (conversion.Factor, true);
        }

        return Task.CompletedTask;
    }

    public Task<bool> DeactivateConversionAsync(Guid materialId, Guid fromUomId, Guid toUomId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_conversions.TryGetValue((materialId, fromUomId, toUomId), out var row) && row.Active)
            {
                _conversions[(materialId, fromUomId, toUomId)] = (row.Factor, false);
                return Task.FromResult(true);
            }

            return Task.FromResult(false);
        }
    }
}
