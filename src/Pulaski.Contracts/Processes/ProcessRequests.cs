namespace Pulaski.Contracts.Processes;

/// <summary>Creates a process with an empty draft, version 1.</summary>
public sealed record CreateProcessRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
}

public sealed record UpdateProcessRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>Replaces a draft's whole definition. The editor sends it after every change.</summary>
public sealed record SaveProcessDraftRequest
{
    public long Version { get; init; }
    public ProcessDefinitionDto Definition { get; init; } = new();
}

public sealed record ReleaseProcessVersionRequest
{
    public long Version { get; init; }
    public string? Note { get; init; }
}

/// <summary>Replaces what the process makes: all materials, or the listed classes and materials.</summary>
public sealed record SetProcessScopeRequest
{
    public long Version { get; init; }
    public bool AllMaterials { get; init; }
    public IReadOnlyList<Guid> MaterialClassIds { get; init; } = [];
    public IReadOnlyList<Guid> MaterialIds { get; init; } = [];
}

/// <summary>
/// Makes this process the one order creation pre-selects for the material, or clears it (null).
/// The process's scope must cover the material.
/// </summary>
public sealed record SetDefaultProcessRequest
{
    public Guid? ProcessId { get; init; }
}
