namespace Pulaski.Contracts.MachineData;

public sealed record CreateMachineProfileRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? Description { get; init; }
}

public sealed record UpdateMachineProfileRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? Description { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>Adds or changes one state value of a profile.</summary>
public sealed record SetProfileStateRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public int PackMlState { get; init; }
    public Guid? ReasonCodeId { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>Adds or changes one stop, alarm or warning code of a profile.</summary>
public sealed record SetProfileCodeRequest
{
    public long Version { get; init; }
    public string Message { get; init; } = "";
    public string? Category { get; init; }
    public Guid? ReasonCodeId { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>The utilization reasons operators may choose on the profile's machines.</summary>
public sealed record SetProfileReasonsRequest
{
    public long Version { get; init; }
    public IReadOnlyList<Guid> ReasonCodeIds { get; init; } = [];
}

/// <summary>An equipment's machine data (D-041): its profile, where its state comes from, and its micro-stop threshold.</summary>
public sealed record SetEquipmentMachineDataRequest
{
    public long Version { get; init; }
    public Guid? MachineProfileId { get; init; }

    /// <summary><c>own_tags</c>, <c>series</c> (worst of its machines), <c>parallel</c> (best) or <c>constraint_machine</c>.</summary>
    public string StateDerivation { get; init; } = "own_tags";

    /// <summary>The machine below it whose state is the line's, with <c>constraint_machine</c>.</summary>
    public Guid? ConstraintEquipmentId { get; init; }

    /// <summary>Stops shorter than this are micro-stops: 0 to 3600 seconds.</summary>
    public int MicroStopThresholdSeconds { get; init; } = 300;
}

/// <summary>One machine's own reason for a stop code of its profile.</summary>
public sealed record SetStopReasonOverrideRequest
{
    /// <summary>The equipment's version.</summary>
    public long Version { get; init; }

    /// <summary>Null: this machine leaves the code uncoded.</summary>
    public Guid? ReasonCodeId { get; init; }

    /// <summary>False removes the override, so the profile's reason applies again.</summary>
    public bool IsActive { get; init; } = true;
}
