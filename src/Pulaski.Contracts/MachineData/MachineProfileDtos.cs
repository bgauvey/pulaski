namespace Pulaski.Contracts.MachineData;

/// <summary>One kind of machine, as its controller reports it (D-041), as profile lists show it.</summary>
public sealed record MachineProfileDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Active stop codes (first-out stop reasons), and how many of them have no reason.</summary>
    public int StopCodeCount { get; init; }
    public int UnmappedStopCodeCount { get; init; }

    /// <summary>Equipment using the profile.</summary>
    public int MachineCount { get; init; }

    /// <summary>Concurrency token for the profile and its lists; send it back unchanged with any change to them.</summary>
    public long Version { get; init; }
}

/// <summary>A value the machine's state tag takes, and the PackML state it means (D-041).</summary>
public sealed record ProfileStateDto
{
    public required int RawValue { get; init; }
    public required string Name { get; init; }
    public required int PackMlState { get; init; }
    public required string PackMlName { get; init; }

    /// <summary>The utilization reason while in this state with no stop code, e.g. Suspended = starved.</summary>
    public Guid? ReasonCodeId { get; init; }
    public string? ReasonCode { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>One of the machine's stop, alarm or warning codes, and the utilization reason it maps to (D-041).</summary>
public sealed record ProfileCodeDto
{
    /// <summary><c>stop_reason</c>, <c>alarm</c> or <c>warning</c>.</summary>
    public required string Kind { get; init; }
    public required int RawValue { get; init; }
    public required string Message { get; init; }
    public string? Category { get; init; }
    public Guid? ReasonCodeId { get; init; }
    public string? ReasonCode { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>A machine's own reason for a stop code, where it differs from its profile.</summary>
public sealed record StopReasonOverrideDto
{
    public required int RawValue { get; init; }

    /// <summary>Null: this machine leaves the code uncoded.</summary>
    public Guid? ReasonCodeId { get; init; }
    public string? ReasonCode { get; init; }
}

/// <summary>Equipment that uses a profile, with its overrides.</summary>
public sealed record ProfileMachineDto
{
    public required Guid EquipmentId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }
    public IReadOnlyList<StopReasonOverrideDto> Overrides { get; init; } = [];

    /// <summary>The equipment's concurrency token, for an override.</summary>
    public long Version { get; init; }
}

/// <summary>A profile with its lists (D-041).</summary>
public sealed record MachineProfileDetailDto
{
    public required MachineProfileDto Profile { get; init; }
    public IReadOnlyList<ProfileStateDto> States { get; init; } = [];
    public IReadOnlyList<ProfileCodeDto> Codes { get; init; } = [];

    /// <summary>The utilization reasons operators may choose on its machines, besides those its codes map to.</summary>
    public IReadOnlyList<Guid> OfferedReasonIds { get; init; } = [];
    public IReadOnlyList<ProfileMachineDto> Machines { get; init; } = [];
}

/// <summary>What a profile import did, or would do, row by row (D-041).</summary>
public sealed record ProfileImportResponse
{
    /// <summary>True when the rows were saved: no errors and not a dry run.</summary>
    public bool Applied { get; init; }
    public int Created { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }

    /// <summary>Entries of the profile missing from a list the file replaces, switched off.</summary>
    public int SwitchedOff { get; init; }
    public int Errors { get; init; }

    /// <summary>What was wrong with the file as a whole, such as a missing column; the rows weren't read.</summary>
    public IReadOnlyList<string> FileProblems { get; init; } = [];
    public IReadOnlyList<ProfileImportRowDto> Rows { get; init; } = [];

    /// <summary>The profile's version after an applied import.</summary>
    public long Version { get; init; }
}

public sealed record ProfileImportRowDto
{
    /// <summary>The line of the file; 0 for an entry switched off because the file left it out.</summary>
    public int Line { get; init; }

    /// <summary><c>state</c>, <c>stop_reason</c>, <c>alarm</c> or <c>warning</c>.</summary>
    public required string Kind { get; init; }
    public int RawValue { get; init; }

    /// <summary><c>create</c>, <c>update</c>, <c>unchanged</c>, <c>switch_off</c> or <c>error</c>.</summary>
    public required string Action { get; init; }
    public string? Message { get; init; }
}
