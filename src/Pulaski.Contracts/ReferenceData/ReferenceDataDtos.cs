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

/// <summary>A material (D-033). For one from the ERP, code, name, base unit and type are the ERP's and read-only here.</summary>
public sealed record MaterialDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required string MaterialType { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }
    public Guid? MaterialClassId { get; init; }
    public string? MaterialClassCode { get; init; }
    public string? MaterialClassName { get; init; }

    /// <summary>False for a material kept as one balance per location rather than in numbered lots (D-027).</summary>
    public bool IsLotTracked { get; init; } = true;

    /// <summary>A new lot expires this many days after it arrives (D-032).</summary>
    public int? ShelfLifeDays { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Made in batches of this many base units (D-036); null when made continuously.</summary>
    public decimal? BatchSize { get; init; }

    /// <summary>How far a batch may be from the size under the near rule, in percent.</summary>
    public decimal BatchTolerancePct { get; init; } = 2m;

    /// <summary><c>exact</c> or <c>near</c>; null when made continuously.</summary>
    public string? BatchRule { get; init; }

    /// <summary>For ERP orders that aren't whole batches (D-066): <c>reject</c>, <c>round_up</c>, <c>round_down</c> or <c>hold</c>; null uses the site's.</summary>
    public string? ErpBatchRule { get; init; }

    /// <summary><c>local</c>, or <c>erp</c> for one imported from an external item master.</summary>
    public string Origin { get; init; } = "local";
    public string? ExternalSystem { get; init; }
    public string? ExternalKey { get; init; }
    public string? ExternalVersion { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Concurrency token; send it back when changing the material.</summary>
    public long Version { get; init; }
}

/// <summary>A flat group of materials (D-033).</summary>
public sealed record MaterialClassDto
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; } = true;

    /// <summary>How many materials (active or not) are in it.</summary>
    public int MaterialCount { get; init; }
    public long Version { get; init; }
}
