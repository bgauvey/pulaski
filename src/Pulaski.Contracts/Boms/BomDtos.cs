namespace Pulaski.Contracts.Boms;

/// <summary>A BOM in a list (D-025): its material, the process it is for (none: any), and its versions.</summary>
public sealed record BomSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public Guid? ProcessId { get; init; }
    public string? ProcessCode { get; init; }
    public bool IsActive { get; init; }
    public int? ReleasedVersion { get; init; }
    public int? DraftVersion { get; init; }

    /// <summary>Components in the released version.</summary>
    public int ReleasedItems { get; init; }

    /// <summary>
    /// Operation numbers the released version names that its process's released version no
    /// longer has. Orders for the material made with that process are refused until a new BOM
    /// version is released (D-025).
    /// </summary>
    public int[] MissingOperations { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record BomDto
{
    public required Guid Id { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required Guid MaterialBaseUomId { get; init; }
    public Guid? ProcessId { get; init; }
    public string? ProcessCode { get; init; }
    public string? ProcessName { get; init; }
    public bool IsActive { get; init; }

    /// <summary>As <see cref="BomSummaryDto.MissingOperations"/>.</summary>
    public int[] MissingOperations { get; init; } = [];

    /// <summary>Concurrency token of the BOM row.</summary>
    public long Version { get; init; }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<BomVersionSummaryDto> Versions { get; init; } = [];
}

public sealed record BomVersionSummaryDto(Guid Id, int Number, string Status, DateTimeOffset? ReleasedAt, string? Note);

/// <summary>One version with its items, and what still stops it being released.</summary>
public sealed record BomVersionDto
{
    public required Guid Id { get; init; }
    public required Guid BomId { get; init; }
    public int Number { get; init; }

    /// <summary><c>draft</c>, <c>released</c> or <c>obsolete</c>.</summary>
    public required string Status { get; init; }

    public DateTimeOffset? ReleasedAt { get; init; }
    public string? Note { get; init; }
    public required BomDefinitionDto Definition { get; init; }

    /// <summary>Concurrency token; send it back when saving or releasing.</summary>
    public long Version { get; init; }

    public IReadOnlyList<BomProblemDto> Problems { get; init; } = [];
}

/// <summary>Items are per <see cref="BaseQty"/> of the BOM's material, in <see cref="BaseUomId"/>.</summary>
public sealed record BomDefinitionDto
{
    public decimal BaseQty { get; init; } = 1m;
    public Guid BaseUomId { get; init; }
    public IReadOnlyList<BomItemDto> Items { get; init; } = [];
}

/// <summary>
/// A component line. <see cref="ConsumptionMethod"/> is <c>time_window</c>, <c>declared</c>,
/// <c>backflush</c> or <c>scan</c>; <see cref="OperationNumber"/> (a BOM for a process only) is the
/// operation that consumes it, or null for the first.
/// </summary>
public sealed record BomItemDto
{
    public Guid Id { get; init; }
    public int LineNo { get; init; }
    public Guid ComponentMaterialId { get; init; }
    public decimal QtyPer { get; init; }
    public Guid UomId { get; init; }
    public decimal ScrapFactor { get; init; }
    public string ConsumptionMethod { get; init; } = "backflush";
    public int? OperationNumber { get; init; }
}

public sealed record BomProblemDto(string Code, string Message, Guid? ItemId);

public sealed record SaveBomDraftResponse(long Version, IReadOnlyList<BomProblemDto> Problems);

public sealed record CreateBomRequest
{
    public Guid MaterialId { get; init; }

    /// <summary>The process the BOM is for; null for any process.</summary>
    public Guid? ProcessId { get; init; }
}

public sealed record SaveBomDraftRequest
{
    public long Version { get; init; }
    public BomDefinitionDto Definition { get; init; } = new();
}

public sealed record ReleaseBomVersionRequest
{
    public long Version { get; init; }
    public string? Note { get; init; }
}

/// <summary>A component planned for one of a work order's jobs (D-025).</summary>
public sealed record PlannedComponentDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string EquipmentCode { get; init; }
    public int? OperationNumber { get; init; }
    public int? LineNo { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public decimal PlannedQty { get; init; }
    public required string UomCode { get; init; }
    public string? ConsumptionMethod { get; init; }
}
