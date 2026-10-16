namespace Pulaski.Contracts.ReferenceData;

/// <summary>A new unit (D-026). A packaging unit has dimension <c>other</c> and no factor.</summary>
public sealed record CreateUomRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string Dimension { get; init; } = "";

    /// <summary>How many of the dimension's base unit one of it is (1 lb = 0.45359237 kg). Null for packaging.</summary>
    public decimal? ToBaseFactor { get; init; }
}

/// <summary>Only the name and whether it is active can change; the code, dimension and factor are fixed.</summary>
public sealed record UpdateUomRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public bool IsActive { get; init; } = true;
}

/// <summary>For one material: 1 <see cref="FromUomCode"/> = <see cref="Factor"/> <see cref="ToUomCode"/>, and the reverse.</summary>
public sealed record MaterialConversionDto
{
    public required Guid MaterialId { get; init; }
    public required Guid FromUomId { get; init; }
    public required string FromUomCode { get; init; }
    public required Guid ToUomId { get; init; }
    public required string ToUomCode { get; init; }
    public decimal Factor { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record SetMaterialConversionRequest
{
    public Guid FromUomId { get; init; }
    public Guid ToUomId { get; init; }
    public decimal Factor { get; init; }
}

/// <summary>
/// A quantity converted from one unit to another, for a material when one is given (its own
/// conversions apply too). <see cref="Converts"/> is false when there is no way between them.
/// </summary>
public sealed record UnitConversionResultDto
{
    public bool Converts { get; init; }
    public decimal? Quantity { get; init; }

    /// <summary>What one of the from-unit is in the to-unit.</summary>
    public decimal? Factor { get; init; }

    public string? Message { get; init; }
}
