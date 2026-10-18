namespace Pulaski.Contracts.Labor;

// ---- People and crews (D-040) ----

/// <summary>A site's crew. Shifts in a schedule name the crew that works them by its code.</summary>
public sealed record CrewDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Active people in the crew.</summary>
    public int Members { get; init; }
    public long Version { get; init; }
}

public sealed record CreateCrewRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
}

public sealed record UpdateCrewRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public bool IsActive { get; init; } = true;
}

/// <summary>Someone whose time is recorded. Not necessarily a user: they can be checked in by a supervisor.</summary>
public sealed record PersonDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string EmployeeNumber { get; init; }
    public required string DisplayName { get; init; }
    public Guid? CrewId { get; init; }
    public string? CrewCode { get; init; }

    /// <summary>The Pulaski user they sign in as, so they can clock themselves on; null for none.</summary>
    public string? Username { get; init; }
    public bool IsActive { get; init; }
    public long Version { get; init; }
}

public sealed record CreatePersonRequest
{
    public Guid SiteId { get; init; }
    public string EmployeeNumber { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public Guid? CrewId { get; init; }
    public string? Username { get; init; }
}

public sealed record UpdatePersonRequest
{
    public long Version { get; init; }
    public string DisplayName { get; init; } = "";
    public Guid? CrewId { get; init; }
    public string? Username { get; init; }
    public bool IsActive { get; init; } = true;
}

// ---- Sessions ----

/// <summary>Time a person spent (D-040).</summary>
public sealed record LaborSessionDto
{
    public required Guid Id { get; init; }
    public required Guid PersonId { get; init; }
    public required string PersonName { get; init; }
    public required string EmployeeNumber { get; init; }
    public required Guid EquipmentId { get; init; }
    public required string EquipmentCode { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }

    /// <summary><c>direct</c>, <c>setup</c>, <c>indirect</c> or <c>training</c>.</summary>
    public required string LaborType { get; init; }
    public Guid? ReasonCodeId { get; init; }
    public string? ReasonName { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public decimal AllocationPct { get; init; }
    public DateOnly ProductionDate { get; init; }
    public Guid? ShiftScheduleId { get; init; }
    public string? ShiftCode { get; init; }

    /// <summary><c>self</c>, <c>supervisor</c>, <c>crew</c> or <c>correction</c>.</summary>
    public required string Source { get; init; }
    public Guid? CorrectsSessionId { get; init; }

    /// <summary>A correction that removes the session it corrects.</summary>
    public bool IsVoid { get; init; }

    /// <summary>Replaced by a correction: kept for history, left out of the hours.</summary>
    public bool IsSuperseded { get; init; }

    /// <summary>Its shift is approved, so it can only be corrected.</summary>
    public bool IsApproved { get; init; }
    public string? Note { get; init; }
    public required string RecordedBy { get; init; }

    /// <summary>Elapsed hours × its share, until now while it is open.</summary>
    public decimal Hours { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Puts a person on a line (D-040). For direct time leave the job out: it counts towards whatever runs on the line.
/// Without <see cref="StartedAt"/> it starts now.
/// </summary>
public sealed record ClockOnRequest
{
    /// <summary>Who; left out on <c>/api/labor/me/sessions</c>, where it is the signed-in user.</summary>
    public Guid? PersonId { get; init; }
    public Guid EquipmentId { get; init; }
    public Guid? JobId { get; init; }
    public string LaborType { get; init; } = "direct";
    public Guid? ReasonCodeId { get; init; }
    public decimal AllocationPct { get; init; } = 100;
    public DateTimeOffset? StartedAt { get; init; }
    public string? Note { get; init; }
}

public sealed record ClockOffRequest
{
    public long Version { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>Checks people onto a line at once, usually the crew on shift (D-040): direct time from now or the given time.</summary>
public sealed record CheckInRequest
{
    public Guid EquipmentId { get; init; }
    public IReadOnlyList<Guid> PersonIds { get; init; } = [];
    public DateTimeOffset? StartedAt { get; init; }
}

/// <summary>Ends every open session on a line.</summary>
public sealed record CheckOutRequest
{
    public Guid EquipmentId { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>Changes a session of a shift not yet approved.</summary>
public sealed record UpdateLaborSessionRequest
{
    public long Version { get; init; }
    public Guid EquipmentId { get; init; }
    public Guid? JobId { get; init; }
    public string LaborType { get; init; } = "direct";
    public Guid? ReasonCodeId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public decimal AllocationPct { get; init; } = 100;
    public string? Note { get; init; }
}

/// <summary>Corrects a session of an approved shift: a new session replaces it, or with <see cref="Void"/> removes it.</summary>
public sealed record CorrectLaborSessionRequest
{
    public bool Void { get; init; }
    public Guid EquipmentId { get; init; }
    public Guid? JobId { get; init; }
    public string LaborType { get; init; } = "direct";
    public Guid? ReasonCodeId { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public decimal AllocationPct { get; init; } = 100;

    /// <summary>Why: required, since the shift was already approved.</summary>
    public string Note { get; init; } = "";
}

/// <summary>The time a session was recorded or changed, and any notes for the person doing it.</summary>
public sealed record LaborResponse(Guid Id, long Version, IReadOnlyList<string> Notes);

/// <summary>The signed-in user's own labor (D-040): who they are as a person, and their open sessions.</summary>
public sealed record MyLaborDto(PersonDto? Person, IReadOnlyList<LaborSessionDto> Sessions);

// ---- Lines and shifts ----

/// <summary>The shift a line is working now, from the schedule it follows (D-038, D-040), with its crew.</summary>
public sealed record CurrentShiftDto
{
    public required Guid ShiftScheduleId { get; init; }
    public required string ScheduleCode { get; init; }
    public required string ShiftCode { get; init; }
    public required string ShiftName { get; init; }
    public DateOnly ProductionDate { get; init; }
    public DateTimeOffset Start { get; init; }
    public DateTimeOffset End { get; init; }
    public string? CrewCode { get; init; }
    public string? CrewName { get; init; }
}

/// <summary>Who is on a line now (D-040).</summary>
public sealed record LineLaborDto
{
    public required Guid LineId { get; init; }
    public required string LineCode { get; init; }
    public required string LineName { get; init; }
    public CurrentShiftDto? Shift { get; init; }
    public Guid? RunningJobId { get; init; }
    public string? RunningJobNumber { get; init; }

    /// <summary>The line's standard crew size for the running job's material, when there is one.</summary>
    public decimal? StandardCrewSize { get; init; }
    public IReadOnlyList<LaborSessionDto> Sessions { get; init; } = [];
}

/// <summary>A shift worked on a day at a site, for choosing which to review (D-040).</summary>
public sealed record LaborShiftDto
{
    public DateOnly ProductionDate { get; init; }
    public Guid? ShiftScheduleId { get; init; }
    public string? ScheduleCode { get; init; }

    /// <summary>Null for time on lines with no shifts, reviewed by day.</summary>
    public string? ShiftCode { get; init; }
    public string? ShiftName { get; init; }
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }
    public string? CrewCode { get; init; }
    public int Sessions { get; init; }
    public int OpenSessions { get; init; }
    public decimal Hours { get; init; }
    public bool IsApproved { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
}

public sealed record ShiftLaborDto
{
    public required LaborShiftDto Shift { get; init; }
    public IReadOnlyList<LaborSessionDto> Sessions { get; init; } = [];
}

/// <summary>Approves a shift's labor (D-040); open sessions in it end when the shift ended.</summary>
public sealed record ApproveShiftRequest
{
    public DateOnly ProductionDate { get; init; }
    public Guid? ShiftScheduleId { get; init; }
    public string? ShiftCode { get; init; }
    public string? Note { get; init; }
}

// ---- Reports ----

/// <summary>Labor on one job's runs: what it took against its standard (D-040).</summary>
public sealed record JobLaborRowDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public string? MaterialCode { get; init; }
    public required string LineCode { get; init; }
    public decimal RunHours { get; init; }
    public decimal DirectHours { get; init; }
    public decimal SetupHours { get; init; }

    /// <summary>Run hours × the standard crew size; null without one.</summary>
    public decimal? StandardHours { get; init; }

    /// <summary>Standard hours ÷ direct hours, as a percentage.</summary>
    public decimal? EfficiencyPct { get; init; }
}

public sealed record PersonLaborRowDto
{
    public required Guid PersonId { get; init; }
    public required string PersonName { get; init; }
    public required string EmployeeNumber { get; init; }
    public decimal DirectHours { get; init; }
    public decimal SetupHours { get; init; }
    public decimal IndirectHours { get; init; }
    public decimal TrainingHours { get; init; }

    /// <summary>Direct time on a line while nothing ran on it.</summary>
    public decimal IdleHours { get; init; }
    public decimal TotalHours { get; init; }
}

public sealed record LaborReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public IReadOnlyList<JobLaborRowDto> Jobs { get; init; } = [];
    public IReadOnlyList<PersonLaborRowDto> People { get; init; } = [];
    public IReadOnlyList<LaborShiftDto> Shifts { get; init; } = [];
}
