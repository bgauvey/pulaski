using Pulaski.Application.Abstractions;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Domain.Units;

namespace Pulaski.Application.Units;

/// <summary>Read side for units and material conversions (D-026). Unit lists come from <c>IReferenceDataReadStore</c>.</summary>
public interface IUnitReadStore
{
    /// <summary>The material's active conversions, by unit code.</summary>
    Task<IReadOnlyList<MaterialConversionDto>> ListConversionsAsync(Guid materialId, CancellationToken cancellationToken);

    /// <summary>Every unit, active or not, for converting.</summary>
    Task<IReadOnlyList<Unit>> GetUnitsAsync(CancellationToken cancellationToken);

    /// <summary>The active conversions of the materials.</summary>
    Task<IReadOnlyList<MaterialConversionRow>> GetConversionsAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IUnitRepository
{
    /// <summary>Every unit, active or not, for converting.</summary>
    Task<IReadOnlyList<Unit>> GetUnitsAsync(CancellationToken cancellationToken);

    /// <summary>The active conversions of the materials.</summary>
    Task<IReadOnlyList<MaterialConversionRow>> GetConversionsAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken);

    Task<Versioned<UnitOfMeasure>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertAsync(UnitOfMeasure unit, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the name and whether it is active.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateAsync(UnitOfMeasure unit, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Locks the material row, so its conversions change one command at a time. False when it doesn't exist.</summary>
    Task<bool> LockMaterialAsync(Guid materialId, CancellationToken cancellationToken);

    /// <summary>Adds the conversion, or switches it back on with the new factor.</summary>
    Task SetConversionAsync(Guid materialId, MaterialConversion conversion, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Switches the conversion off; false when it wasn't active.</summary>
    Task<bool> DeactivateConversionAsync(Guid materialId, Guid fromUomId, Guid toUomId, DateTimeOffset at, CancellationToken cancellationToken);
}

/// <summary>A unit as its commands see it (D-026). Only the name and <see cref="IsActive"/> change after creation.</summary>
public sealed record UnitOfMeasure(Guid Id, string Code, string Name, string Dimension, decimal? ToBaseFactor, bool IsActive);

/// <summary>One active conversion of a material: 1 from = factor to.</summary>
public sealed record MaterialConversionRow
{
    public required Guid MaterialId { get; init; }
    public required Guid FromUomId { get; init; }
    public required Guid ToUomId { get; init; }
    public decimal Factor { get; init; }

    public MaterialConversion ToDomain() => new(FromUomId, ToUomId, Factor);
}

/// <summary>Builds a material's converter from the units and conversions read for it.</summary>
public static class UnitConversions
{
    public static UnitConverter For(Guid materialId, IReadOnlyList<Unit> units, IEnumerable<MaterialConversionRow> conversions) =>
        new(units, conversions.Where(c => c.MaterialId == materialId).Select(c => c.ToDomain()));

    /// <summary>The standard factors only, for when no material is involved.</summary>
    public static UnitConverter Standard(IReadOnlyList<Unit> units) => new(units, []);
}
