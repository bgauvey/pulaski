namespace Pulaski.Contracts.ReferenceData;

/// <summary>
/// A unit of measure (D-026). <see cref="ToBaseFactor"/> converts it to its dimension's base unit
/// for every material; null marks a packaging unit (box, bag, pallet) that converts only through a
/// material's own conversion.
/// </summary>
public sealed record UomDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }

    /// <summary><c>mass</c>, <c>length</c>, <c>count</c>, <c>volume</c>, <c>area</c>, <c>time</c> or <c>other</c> (packaging).</summary>
    public required string Dimension { get; init; }

    public decimal? ToBaseFactor { get; init; }

    /// <summary>The code of the dimension's base unit (factor 1), for showing "1 lb = 0.4536 kg".</summary>
    public string? BaseCode { get; init; }

    public bool IsActive { get; init; } = true;

    /// <summary>Concurrency token; send it back when changing the unit.</summary>
    public long Version { get; init; }
}

public sealed record MaterialDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string MaterialType { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }
    public Guid? MaterialClassId { get; init; }
    public string? MaterialClassCode { get; init; }

    /// <summary>False for a material kept as one balance per location rather than in numbered lots (D-027).</summary>
    public bool IsLotTracked { get; init; } = true;
    public bool IsActive { get; init; }
}

public sealed record MaterialClassDto(Guid Id, string Code, string Name);
