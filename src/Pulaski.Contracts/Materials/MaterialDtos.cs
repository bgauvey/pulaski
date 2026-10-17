using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Contracts.Materials;

/// <summary>What refers to a material (D-033): the reasons its base unit or code are fixed, and what a deactivation leaves running.</summary>
public sealed record MaterialUsageDto
{
    /// <summary>True when anything refers to it: then its base unit and (if local) its code are fixed.</summary>
    public bool InUse { get; init; }
    public int LotsOnHand { get; init; }
    public decimal QuantityOnHand { get; init; }
    public int OpenJobs { get; init; }
    public int OpenWorkOrders { get; init; }

    /// <summary>BOMs that make it.</summary>
    public int Boms { get; init; }

    /// <summary>Released BOMs that list it as a component.</summary>
    public int UsedInReleasedBoms { get; init; }
    public int Capabilities { get; init; }
    public int Processes { get; init; }
}

/// <summary>A line or unit that can run the material (D-020), with its planning rate (D-016).</summary>
public sealed record MaterialCapabilityDto
{
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public required string EquipmentName { get; init; }
    public decimal IdealRatePerHour { get; init; }
    public required string RateUomCode { get; init; }
    public TimeSpan? StandardChangeover { get; init; }
    public decimal? StandardCrewSize { get; init; }
    public bool IsPreferred { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }

    /// <summary>The run standard's rate in force, modified or calculated; null when there is none.</summary>
    public decimal? EffectiveRatePerHour { get; init; }
    public string? RateSource { get; init; }
    public string? RunStandardUomCode { get; init; }
}

/// <summary>A material with what uses it and where it can run.</summary>
public sealed record MaterialDetailDto
{
    public required MaterialDto Material { get; init; }
    public required string BaseUomName { get; init; }
    public required MaterialUsageDto Usage { get; init; }
    public IReadOnlyList<MaterialCapabilityDto> Capabilities { get; init; } = [];
}

/// <summary>A new local material (D-033).</summary>
public sealed record CreateMaterialRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string MaterialType { get; init; } = "";
    public Guid? MaterialClassId { get; init; }
    public Guid BaseUomId { get; init; }
    public bool IsLotTracked { get; init; } = true;
    public int? ShelfLifeDays { get; init; }
}

/// <summary>Every field of a material. For one from the ERP, code, name, base unit and type must be sent unchanged.</summary>
public sealed record UpdateMaterialRequest
{
    public long Version { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public string MaterialType { get; init; } = "";
    public Guid? MaterialClassId { get; init; }
    public Guid BaseUomId { get; init; }
    public bool IsLotTracked { get; init; } = true;
    public int? ShelfLifeDays { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>The material as it is now, and, when it was switched off, what still uses it.</summary>
public sealed record UpdateMaterialResponse(long Version, IReadOnlyList<string> StillUsedBy);

/// <summary>
/// One item from an external item master, such as the ERP (D-033). Creates the material, or updates
/// the fields the ERP owns on the one with this system and key; Pulaski's own fields are left alone.
/// </summary>
public sealed record ImportMaterialRequest
{
    /// <summary>Which system mastered it, such as <c>SAP-PRD</c>.</summary>
    public string ExternalSystem { get; init; } = "";

    /// <summary>Its key there; with the system, it identifies the material for every later import.</summary>
    public string ExternalKey { get; init; } = "";

    /// <summary>The system's version or change stamp, kept for reference.</summary>
    public string? ExternalVersion { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>The base unit's code in Pulaski, such as <c>kg</c>.</summary>
    public string BaseUomCode { get; init; } = "";
    public string MaterialType { get; init; } = "";
}

/// <summary>What an import did: created the material, changed it, or found it already up to date.</summary>
public sealed record ImportMaterialResponse(Guid Id, long Version, bool Created, bool Changed);

public sealed record CreateMaterialClassRequest
{
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record UpdateMaterialClassRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public bool IsActive { get; init; } = true;
}
