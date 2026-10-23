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

    /// <summary>Worked hours: elapsed time outside the person's breaks × its share, until now while it is open (D-047).</summary>
    public decimal Hours { get; init; }

    /// <summary>The person's break time inside the session, × its share.</summary>
    public decimal BreakHours { get; init; }

    /// <summary>The qualifications its line requires that the person doesn't hold today (D-048), in words; empty when none are missing.</summary>
    public IReadOnlyList<string> MissingQualifications { get; init; } = [];
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

/// <summary>The signed-in user's own labor (D-040): who they are as a person, their open sessions and the break they are on.</summary>
public sealed record MyLaborDto(PersonDto? Person, IReadOnlyList<LaborSessionDto> Sessions, BreakDto? OpenBreak = null);

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

    /// <summary>The open breaks of the people on the line (D-047).</summary>
    public IReadOnlyList<BreakDto> Breaks { get; init; } = [];
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

    /// <summary>Worked hours, breaks left out (D-047).</summary>
    public decimal Hours { get; init; }
    public decimal PaidBreakHours { get; init; }
    public decimal UnpaidBreakHours { get; init; }
    public bool IsApproved { get; init; }
    public string? ApprovedBy { get; init; }
    public DateTimeOffset? ApprovedAt { get; init; }
}

public sealed record ShiftLaborDto
{
    public required LaborShiftDto Shift { get; init; }
    public IReadOnlyList<LaborSessionDto> Sessions { get; init; } = [];

    /// <summary>The shift's breaks, corrections and replaced ones too (D-047).</summary>
    public IReadOnlyList<BreakDto> Breaks { get; init; } = [];
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

    /// <summary>Worked hours: breaks are left out of every kind of time (D-047).</summary>
    public decimal TotalHours { get; init; }
    public decimal PaidBreakHours { get; init; }
    public decimal UnpaidBreakHours { get; init; }
}

public sealed record LaborReportDto
{
    public DateOnly From { get; init; }
    public DateOnly To { get; init; }
    public IReadOnlyList<JobLaborRowDto> Jobs { get; init; } = [];
    public IReadOnlyList<PersonLaborRowDto> People { get; init; } = [];
    public IReadOnlyList<LaborShiftDto> Shifts { get; init; } = [];
}

// ---- Breaks (D-047) ----

/// <summary>A break a person took: its time comes out of their sessions' worked hours, and is reported paid or unpaid.</summary>
public sealed record BreakDto
{
    public required Guid Id { get; init; }
    public required Guid PersonId { get; init; }
    public string PersonName { get; init; } = "";
    public string EmployeeNumber { get; init; } = "";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public bool IsPaid { get; init; }
    public DateOnly ProductionDate { get; init; }
    public Guid? ShiftScheduleId { get; init; }
    public string? ShiftCode { get; init; }

    /// <summary><c>self</c>, <c>supervisor</c> or <c>correction</c>.</summary>
    public string Source { get; init; } = "";
    public Guid? CorrectsBreakId { get; init; }
    public bool IsVoid { get; init; }
    public bool IsSuperseded { get; init; }
    public bool IsApproved { get; init; }
    public string? Note { get; init; }
    public string RecordedBy { get; init; } = "";

    /// <summary>Elapsed hours, until now while it is open.</summary>
    public decimal Hours { get; init; }
    public long Version { get; init; }
}

/// <summary>
/// Starts a break, or records one already over with <see cref="EndedAt"/>. It must start while the person is on a line.
/// <see cref="PersonId"/> is left out on <c>/api/labor/me/breaks</c>.
/// </summary>
public sealed record StartBreakRequest
{
    public Guid? PersonId { get; init; }
    public bool IsPaid { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public string? Note { get; init; }
}

public sealed record EndBreakRequest
{
    public long Version { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
}

/// <summary>Changes a break of a shift not yet approved.</summary>
public sealed record UpdateBreakRequest
{
    public long Version { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }
    public bool IsPaid { get; init; }
    public string? Note { get; init; }
}

/// <summary>Corrects a break of an approved shift: a new one replaces it, or with <see cref="Void"/> removes it. Says why.</summary>
public sealed record CorrectBreakRequest
{
    public bool Void { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset EndedAt { get; init; }
    public bool IsPaid { get; init; }
    public string Note { get; init; } = "";
}

// ---- Qualifications (D-048) ----

/// <summary>A site's skill or certification, with who holds it and where it is required.</summary>
public sealed record QualificationDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }

    /// <summary>How long a new grant lasts by default; null when it doesn't expire.</summary>
    public int? ValidityMonths { get; init; }
    public bool IsActive { get; init; }

    /// <summary>People holding a valid grant today.</summary>
    public int Holders { get; init; }

    /// <summary>Of those, how many expire within 30 days.</summary>
    public int ExpiringSoon { get; init; }

    /// <summary>Codes of the equipment that requires it (and so everything below it).</summary>
    public IReadOnlyList<string> RequiredOn { get; init; } = [];
    public long Version { get; init; }
}

public sealed record CreateQualificationRequest
{
    public Guid SiteId { get; init; }
    public string Code { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int? ValidityMonths { get; init; }
}

public sealed record UpdateQualificationRequest
{
    public long Version { get; init; }
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public int? ValidityMonths { get; init; }
    public bool IsActive { get; init; } = true;
}

/// <summary>A person's grant of a qualification and its state today: <c>valid</c>, <c>not_yet</c>, <c>expired</c> or <c>revoked</c>.</summary>
public sealed record QualificationGrantDto
{
    public required Guid Id { get; init; }
    public required Guid PersonId { get; init; }
    public string PersonName { get; init; } = "";
    public string EmployeeNumber { get; init; } = "";
    public required Guid QualificationId { get; init; }
    public string QualificationCode { get; init; } = "";
    public string QualificationName { get; init; } = "";
    public DateOnly ValidFrom { get; init; }

    /// <summary>The last valid day; null when it doesn't expire.</summary>
    public DateOnly? ExpiresOn { get; init; }
    public string Status { get; init; } = "";
    public string? Note { get; init; }
    public string GrantedBy { get; init; } = "";
    public DateTimeOffset? RevokedAt { get; init; }
    public string? RevokedBy { get; init; }
    public string? RevokeReason { get; init; }
    public long Version { get; init; }
}

/// <summary>Grants a qualification; without <see cref="ValidFrom"/> from today, without <see cref="ExpiresOn"/> for its usual validity.</summary>
public sealed record GrantQualificationRequest
{
    public Guid PersonId { get; init; }
    public Guid QualificationId { get; init; }
    public DateOnly? ValidFrom { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public string? Note { get; init; }
}

public sealed record UpdateQualificationGrantRequest
{
    public long Version { get; init; }
    public DateOnly ValidFrom { get; init; }
    public DateOnly? ExpiresOn { get; init; }
    public string? Note { get; init; }
}

public sealed record RevokeQualificationGrantRequest
{
    public long Version { get; init; }
    public string Reason { get; init; } = "";
}

/// <summary>What a piece of equipment requires (D-048): its own requirements, and those it inherits from above it.</summary>
public sealed record EquipmentQualificationsDto
{
    public required Guid EquipmentId { get; init; }
    public IReadOnlyList<EquipmentQualificationDto> Own { get; init; } = [];
    public IReadOnlyList<EquipmentQualificationDto> Inherited { get; init; } = [];
}

public sealed record EquipmentQualificationDto(Guid QualificationId, string Code, string Name, Guid FromEquipmentId, string FromEquipmentCode);

/// <summary>Requires a qualification on equipment (and everything below it), or with <see cref="IsRequired"/> false no longer.</summary>
public sealed record SetEquipmentQualificationRequest
{
    public Guid QualificationId { get; init; }
    public bool IsRequired { get; init; } = true;
}
