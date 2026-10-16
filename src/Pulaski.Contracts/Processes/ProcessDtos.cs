namespace Pulaski.Contracts.Processes;

/// <summary>A process in a list: which version is released and whether a draft is open.</summary>
public sealed record ProcessSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; }
    public int? ReleasedVersion { get; init; }
    public int? DraftVersion { get; init; }

    /// <summary>What it makes: all materials, or these class and material codes.</summary>
    public bool MakesAllMaterials { get; init; }
    public IReadOnlyList<string> MakesClasses { get; init; } = [];
    public IReadOnlyList<string> MakesMaterials { get; init; } = [];

    /// <summary>Codes of the materials that pre-select this process when an order is created.</summary>
    public IReadOnlyList<string> DefaultFor { get; init; } = [];

    public DateTimeOffset UpdatedAt { get; init; }
}

/// <summary>A process route (D-023) with its versions, newest first.</summary>
public sealed record ProcessDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string SiteCode { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Concurrency token for the process header (name, description, active).</summary>
    public long Version { get; init; }

    public IReadOnlyList<ProcessVersionSummaryDto> Versions { get; init; } = [];

    /// <summary>
    /// The scope (D-023): it makes every material, or the classes and materials listed. Order
    /// creation offers it for any material the scope covers.
    /// </summary>
    public bool MakesAllMaterials { get; init; }
    public IReadOnlyList<ProcessMaterialClassDto> MakesClasses { get; init; } = [];
    public IReadOnlyList<ProcessMaterialDto> MakesMaterials { get; init; } = [];

    /// <summary>Materials that pre-select this process when an order is created; always inside the scope when set.</summary>
    public IReadOnlyList<ProcessMaterialDto> DefaultFor { get; init; } = [];
}

public sealed record ProcessMaterialClassDto(Guid Id, string Code, string Name);

public sealed record ProcessVersionSummaryDto(Guid Id, int Number, string Status, DateTimeOffset? ReleasedAt, string? Note);

public sealed record ProcessMaterialDto(Guid Id, string Code, string Name);

/// <summary>
/// One version of a process, with its whole definition. <see cref="Problems"/> lists what would stop
/// it being released (always empty for a released version).
/// </summary>
public sealed record ProcessVersionDto
{
    public required Guid Id { get; init; }
    public required Guid ProcessId { get; init; }
    public int Number { get; init; }

    /// <summary><c>draft</c>, <c>released</c> or <c>obsolete</c>.</summary>
    public required string Status { get; init; }

    public DateTimeOffset? ReleasedAt { get; init; }
    public string? Note { get; init; }
    public required ProcessDefinitionDto Definition { get; init; }

    /// <summary>Concurrency token; send it back with each save.</summary>
    public long Version { get; init; }

    public IReadOnlyList<ProcessProblemDto> Problems { get; init; } = [];
}

/// <summary>Operations and the links between them. Operations with no path between them run in parallel.</summary>
public sealed record ProcessDefinitionDto
{
    public IReadOnlyList<ProcessOperationDto> Operations { get; init; } = [];
    public IReadOnlyList<ProcessDependencyDto> Dependencies { get; init; } = [];
}

/// <summary>An operation, numbered 10, 20, 30…; <see cref="X"/> and <see cref="Y"/> are only where the editor draws it.</summary>
public sealed record ProcessOperationDto
{
    public Guid Id { get; init; }
    public int Number { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public double X { get; init; }
    public double Y { get; init; }

    /// <summary>The lines or departments that perform it, each with its share of the quantity (totalling 100).</summary>
    public IReadOnlyList<OperationEquipmentDto> Equipment { get; init; } = [];
}

public sealed record OperationEquipmentDto(Guid EquipmentId, decimal SharePercent);

/// <summary><see cref="ToOperationId"/> starts after <see cref="FromOperationId"/> starts (<c>after_start</c>) or ends (<c>after_end</c>), plus the lag.</summary>
public sealed record ProcessDependencyDto(Guid Id, Guid FromOperationId, Guid ToOperationId, string Type, TimeSpan Lag);

public sealed record ProcessProblemDto(string Code, string Message, Guid? OperationId, Guid? DependencyId);

/// <summary>The draft's new concurrency token and what still stops its release.</summary>
public sealed record SaveProcessDraftResponse(long Version, IReadOnlyList<ProcessProblemDto> Problems);

/// <summary>
/// A released version, and what it broke: each BOM for the process that names an operation the
/// new version doesn't have (D-025). The release still happens; orders using those BOMs are
/// refused until a new BOM version is released.
/// </summary>
public sealed record ReleaseProcessVersionResponse(long Version, IReadOnlyList<string> Warnings);
