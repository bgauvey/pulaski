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
    public required Guid Id { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public required string EquipmentName { get; init; }

    /// <summary>The time zone of the line's site, for showing and entering its dates.</summary>
    public string? SiteTimeZone { get; init; }
    public decimal IdealRatePerHour { get; init; }
    public required Guid RateUomId { get; init; }
    public required string RateUomCode { get; init; }
    public TimeSpan? StandardChangeover { get; init; }
    public decimal? StandardCrewSize { get; init; }
    public bool IsPreferred { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }

    /// <summary>True when it applies now: jobs for the material can be placed on the line.</summary>
    public bool IsInEffect { get; init; }

    /// <summary>Concurrency token; send it back when changing or ending the capability.</summary>
    public long Version { get; init; }

    /// <summary>The line's run standard for the material, shared by all its capability periods; null when there is none.</summary>
    public Guid? RunStandardId { get; init; }
    public long? RunStandardVersion { get; init; }
    public decimal? CalculatedRatePerHour { get; init; }
    public string? CalculationBasis { get; init; }
    public decimal? ModifiedRatePerHour { get; init; }
    public string? ModifiedBy { get; init; }
    public DateTimeOffset? ModifiedAt { get; init; }
    public string? ModifiedNote { get; init; }

    /// <summary>The run standard's rate in force, modified or calculated; null when there is none.</summary>
    public decimal? EffectiveRatePerHour { get; init; }
    public string? RateSource { get; init; }
    public string? RunStandardUomCode { get; init; }
}

/// <summary>A new capability: the material can run on the line over a period (D-020, D-034).</summary>
public sealed record AddCapabilityRequest
{
    public Guid EquipmentId { get; init; }
    public decimal IdealRatePerHour { get; init; }
    public Guid RateUomId { get; init; }
    public int? StandardChangeoverMinutes { get; init; }
    public decimal? StandardCrewSize { get; init; }
    public bool IsPreferred { get; init; }

    /// <summary>When it starts; now when left out.</summary>
    public DateTimeOffset? EffectiveFrom { get; init; }

    /// <summary>When it stops applying (not included); open-ended when null.</summary>
    public DateTimeOffset? EffectiveTo { get; init; }
}

/// <summary>A capability's new values; the line and material can't change.</summary>
public sealed record UpdateCapabilityRequest
{
    public long Version { get; init; }
    public decimal IdealRatePerHour { get; init; }
    public Guid RateUomId { get; init; }
    public int? StandardChangeoverMinutes { get; init; }
    public decimal? StandardCrewSize { get; init; }
    public bool IsPreferred { get; init; }
    public DateTimeOffset EffectiveFrom { get; init; }
    public DateTimeOffset? EffectiveTo { get; init; }
}

public sealed record EndCapabilityRequest
{
    public long Version { get; init; }
}

/// <summary>The capability as it is now, and notes on what the change did: a new or recalculated planning rate, open jobs left without one.</summary>
public sealed record CapabilityResponse(Guid Id, long Version, IReadOnlyList<string> Notes);

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
    /// <summary>Which system mastered it, such as <c>ERP1</c>.</summary>
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

/// <summary>What a bulk import did, or would do, with one row of the file (D-034).</summary>
public sealed record ImportRowResultDto
{
    /// <summary>The row's line in the file: in CSV counting the header as line 1, in XML where its element starts.</summary>
    public int Line { get; init; }
    public string Code { get; init; } = "";

    /// <summary><c>create</c>, <c>update</c>, <c>unchanged</c> or <c>error</c>.</summary>
    public string Action { get; init; } = "";

    /// <summary>The fields that change, for an update.</summary>
    public IReadOnlyList<string> Changes { get; init; } = [];

    /// <summary>Why the row can't be imported, for an error.</summary>
    public string? Message { get; init; }

    /// <summary>What else it did, such as setting a line's planning rate from a capability's ideal rate.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// A bulk import's outcome (D-034), of materials or of capabilities (D-055). It is all or nothing: <see cref="Applied"/> is true only when every
/// row was valid and it wasn't a check run; otherwise nothing was saved.
/// </summary>
public sealed record ImportMaterialsResponse
{
    public bool Applied { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public int Errors { get; init; }

    /// <summary>Problems with the file as a whole, such as a missing or unknown column.</summary>
    public IReadOnlyList<string> FileProblems { get; init; } = [];

    /// <summary>The codes of the classes the import created, or would create, because the file named them and they were missing (D-055).</summary>
    public IReadOnlyList<string> CreatedClasses { get; init; } = [];
    public IReadOnlyList<ImportRowResultDto> Rows { get; init; } = [];
}

/// <summary>A format materials can be imported from (D-053), at <c>POST /api/materials/import/{Name}</c>.</summary>
public sealed record MaterialSourceDto
{
    /// <summary>Lower-case name used in the URL, such as <c>csv</c> or <c>b2mml</c>.</summary>
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";

    /// <summary>File extensions and media types it reads, for a file picker's <c>accept</c>.</summary>
    public IReadOnlyList<string> FileTypes { get; init; } = [];

    /// <summary>Whether the import names the external system the file's items belong to (<c>?externalSystem=</c>).</summary>
    public bool TakesExternalSystem { get; init; }

    /// <summary>A file to start from, in the accepted format (D-055); also at <c>GET /api/materials/import/{Name}/template</c>.</summary>
    public string Template { get; init; } = "";
    public string TemplateFileName { get; init; } = "";
}

/// <summary>The materials CSV file (D-034): its header line with every column, for a template.</summary>
public static class MaterialCsvFormat
{
    public const string Header = "code,name,type,base_unit,class,class_name,description,lot_tracked,shelf_life_days,active,external_system,external_key,external_version";
}

/// <summary>A material's batch settings (D-036); a blank size makes it continuous again. The rule defaults from the type.</summary>
public sealed record SetBatchingRequest
{
    public long Version { get; init; }
    public decimal? BatchSize { get; init; }
    public decimal TolerancePct { get; init; } = 2m;

    /// <summary><c>exact</c> or <c>near</c>; left out, it keeps the current rule or defaults from the material's type.</summary>
    public string? Rule { get; init; }
}

/// <summary>The capabilities CSV file (D-055): its header line with every column, for a template.</summary>
public static class CapabilityCsvFormat
{
    public const string Header = "material,site,line,ideal_rate,rate_unit,changeover_minutes,crew_size,preferred,active";
}
