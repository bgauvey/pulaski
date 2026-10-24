using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Labor;

public sealed record CrewRecord(Guid Id, Guid SiteId, string Code, string Name, bool IsActive);

/// <summary>
/// A person (D-040). <paramref name="Origin"/> is <c>local</c>, or <c>hr</c> / <c>erp</c> for one imported from that system, which then owns
/// the employee number, name and whether they are active (D-054).
/// </summary>
public sealed record PersonRecord(
    Guid Id, Guid SiteId, string EmployeeNumber, string DisplayName, Guid? CrewId, Guid? AppUserId, bool IsActive,
    string Origin = PersonOrigins.Local, string? ExternalSystem = null, string? ExternalKey = null, string? ExternalVersion = null)
{
    public bool IsImported => Origin != PersonOrigins.Local;
}

/// <summary>Where a person is maintained (D-054).</summary>
public static class PersonOrigins
{
    public const string Local = "local";
    public const string Hr = "hr";
    public const string Erp = "erp";
}

/// <summary>A labor session as the commands see it (D-040).</summary>
public sealed record SessionRecord(
    Guid Id, Guid SiteId, Guid PersonId, Guid EquipmentId, Guid? JobId, string LaborType, Guid? ReasonCodeId,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, decimal AllocationPct, DateOnly ProductionDate, Guid? ShiftScheduleId, string? ShiftCode,
    string Source, Guid? CorrectsSessionId, bool IsVoid, string? Note, string RecordedBy)
{
    public ShiftKey Shift => new(SiteId, ProductionDate, ShiftScheduleId, ShiftCode);
}

/// <summary>A break as the commands see it (D-047).</summary>
public sealed record BreakRecord(
    Guid Id, Guid SiteId, Guid PersonId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, bool IsPaid, DateOnly ProductionDate,
    Guid? ShiftScheduleId, string? ShiftCode, string Source, Guid? CorrectsBreakId, bool IsVoid, string? Note, string RecordedBy)
{
    public ShiftKey Shift => new(SiteId, ProductionDate, ShiftScheduleId, ShiftCode);

    public BreakSpan ToSpan() => new(PersonId, StartedAt, EndedAt, IsPaid);
}

/// <summary>Which shift labor belongs to (D-040): a schedule's shift on a production day, or a day for lines without shifts.</summary>
public sealed record ShiftKey(Guid SiteId, DateOnly ProductionDate, Guid? ShiftScheduleId, string? ShiftCode);

/// <summary>Where equipment's working time comes from (D-038): its schedule, or none, and its site's time zone.</summary>
public sealed record EquipmentShifts(Guid EquipmentId, Guid? SiteId, string Code, TimeZoneInfo Zone, Guid? ScheduleId, string? ScheduleCode, ShiftSchedule? Schedule)
{
    /// <summary>The shift work starting at <paramref name="at"/> belongs to, or (with none) the site-local day.</summary>
    public (DateOnly Date, ShiftOccurrence? Shift) ShiftAt(DateTimeOffset at)
    {
        var shift = Schedule?.ShiftAt(Zone, at, LaborRules.ShiftLead);
        return (shift?.ProductionDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, Zone).DateTime), shift);
    }
}

/// <summary>A job as labor checks it.</summary>
public sealed record LaborJob(Guid Id, Guid SiteId, string JobNumber, Guid? EquipmentId);

/// <summary>Write side for crews, people and labor (D-040): runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface ILaborRepository
{
    Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<Versioned<CrewRecord>?> GetCrewForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CrewCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<long> InsertCrewAsync(CrewRecord crew, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateCrewAsync(CrewRecord crew, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<PersonRecord>?> GetPersonForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The people, locked in id order (ordered in SQL), so check-ins of overlapping people can't deadlock.</summary>
    Task<IReadOnlyList<PersonRecord>> GetPeopleForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The person a user is, locked; null when they aren't set up as one.</summary>
    Task<Versioned<PersonRecord>?> GetPersonOfUserForUpdateAsync(Guid userId, CancellationToken cancellationToken);

    Task<bool> EmployeeNumberExistsAsync(Guid siteId, string employeeNumber, CancellationToken cancellationToken);

    Task<Guid?> FindLocalUserIdAsync(string username, CancellationToken cancellationToken);

    Task<bool> UserIsAnotherPersonAsync(Guid userId, Guid exceptPersonId, CancellationToken cancellationToken);

    /// <summary>The person with this external system and key, locked; null when none (D-054).</summary>
    Task<Versioned<PersonRecord>?> FindPersonByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken);

    /// <summary>The site's person with this employee number, locked; null when none.</summary>
    Task<Versioned<PersonRecord>?> FindPersonByNumberForUpdateAsync(Guid siteId, string employeeNumber, CancellationToken cancellationToken);

    /// <summary>The site's crew with this code (upper case); null when none.</summary>
    Task<CrewRecord?> FindCrewByCodeAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task<long> InsertPersonAsync(PersonRecord person, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdatePersonAsync(PersonRecord person, DateTimeOffset at, CancellationToken cancellationToken);

    Task<EquipmentShifts?> GetEquipmentShiftsAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<ShiftSchedule?> GetScheduleAsync(Guid scheduleId, CancellationToken cancellationToken);

    Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken);

    Task<LaborJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>An active indirect-labor reason for every site or for this one.</summary>
    Task<bool> IsIndirectReasonAsync(Guid reasonCodeId, Guid siteId, CancellationToken cancellationToken);

    Task<Versioned<SessionRecord>?> GetSessionForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<IReadOnlyList<SessionRecord>> ListOpenSessionsOfPersonForUpdateAsync(Guid personId, CancellationToken cancellationToken);

    Task<IReadOnlyList<Versioned<SessionRecord>>> ListOpenSessionsOnEquipmentForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken);

    /// <summary>Every session of a shift, locked, so approval and edits serialize.</summary>
    Task<IReadOnlyList<SessionRecord>> ListShiftSessionsForUpdateAsync(ShiftKey shift, CancellationToken cancellationToken);

    Task<bool> IsCorrectedAsync(Guid sessionId, CancellationToken cancellationToken);

    Task<long> InsertSessionAsync(SessionRecord session, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateSessionAsync(SessionRecord session, DateTimeOffset at, CancellationToken cancellationToken);

    Task<bool> IsApprovedAsync(ShiftKey shift, CancellationToken cancellationToken);

    Task InsertApprovalAsync(Guid id, ShiftKey shift, string approvedBy, Guid? approvedByUserId, DateTimeOffset at, string? note, CancellationToken cancellationToken);

    // ---- Breaks (D-047) ----

    Task<Versioned<BreakRecord>?> GetBreakForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The person's open break, locked; null when they aren't on one.</summary>
    Task<Versioned<BreakRecord>?> GetOpenBreakForUpdateAsync(Guid personId, CancellationToken cancellationToken);

    /// <summary>The person's breaks that count (not replaced or voided) overlapping <c>[windowStart, windowEnd)</c>, open ones too.</summary>
    Task<IReadOnlyList<BreakRecord>> ListBreaksOfPersonAsync(Guid personId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>The person's sessions that count and cover <paramref name="at"/>, earliest first.</summary>
    Task<IReadOnlyList<SessionRecord>> ListSessionsOfPersonAtAsync(Guid personId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Every break of a shift, locked, so approval and edits serialize.</summary>
    Task<IReadOnlyList<BreakRecord>> ListShiftBreaksForUpdateAsync(ShiftKey shift, CancellationToken cancellationToken);

    Task<bool> IsBreakCorrectedAsync(Guid breakId, CancellationToken cancellationToken);

    Task<long> InsertBreakAsync(BreakRecord record, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateBreakAsync(BreakRecord record, DateTimeOffset at, CancellationToken cancellationToken);

    // ---- Qualifications, as labor checks them (D-048) ----

    /// <summary>What the equipment and everything above it require: active qualifications, requirements switched on.</summary>
    Task<IReadOnlyList<RequiredQualification>> ListRequiredQualificationsAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<IReadOnlyList<QualificationGrant>> ListGrantsOfPersonAsync(Guid personId, CancellationToken cancellationToken);
}

/// <summary>A labor session as reports read it: the record with names, and whether a correction replaced it.</summary>
public sealed record SessionRow
{
    public required SessionRecord Session { get; init; }
    public required string PersonName { get; init; }
    public required string EmployeeNumber { get; init; }
    public required string EquipmentCode { get; init; }
    public string? JobNumber { get; init; }
    public string? ReasonName { get; init; }
    public bool IsSuperseded { get; init; }
    public bool IsApproved { get; init; }
    public long Version { get; init; }
}

/// <summary>A run in a report window, with what labor efficiency needs.</summary>
public sealed record RunRow(Guid RunId, Guid JobId, string JobNumber, string? MaterialCode, Guid EquipmentId, string LineCode,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, decimal? StandardCrewSize);

/// <summary>A line as the labor screens list it, with what runs on it now.</summary>
public sealed record LaborLineRow(Guid Id, string Code, string Name, Guid? RunningJobId, string? RunningJobNumber, decimal? StandardCrewSize);

public sealed record ApprovalRow(ShiftKey Shift, string ApprovedBy, DateTimeOffset ApprovedAt);

/// <summary>A break as the screens read it (D-047): the record with the person, and whether a correction replaced it.</summary>
public sealed record BreakRow
{
    public required BreakRecord Break { get; init; }
    public required string PersonName { get; init; }
    public required string EmployeeNumber { get; init; }
    public bool IsSuperseded { get; init; }
    public bool IsApproved { get; init; }
    public long Version { get; init; }
}

/// <summary>A qualification a line requires, directly or through the equipment above it (D-048).</summary>
public sealed record LineRequirement(Guid LineId, RequiredQualification Qualification);

/// <summary>A person's grant, for checking who is qualified where.</summary>
public sealed record PersonGrant(Guid PersonId, QualificationGrant Grant);

/// <summary>Read side for labor (D-040).</summary>
public interface ILaborReadStore
{
    Task<IReadOnlyList<CrewDto>> ListCrewsAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PersonDto>> ListPeopleAsync(Guid siteId, CancellationToken cancellationToken);

    Task<PersonDto?> GetPersonOfUserAsync(Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyList<LaborLineRow>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EquipmentShifts>> ListEquipmentShiftsAsync(IReadOnlyCollection<Guid> equipmentIds, CancellationToken cancellationToken);

    Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>Open sessions at the site, or of one person.</summary>
    Task<IReadOnlyList<SessionRow>> ListOpenSessionsAsync(Guid siteId, Guid? personId, CancellationToken cancellationToken);

    /// <summary>Sessions with a production day in the range, corrections and replaced ones too.</summary>
    Task<IReadOnlyList<SessionRow>> ListSessionsAsync(Guid siteId, DateOnly first, DateOnly last, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunRow>> ListRunsAsync(Guid siteId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprovalRow>> ListApprovalsAsync(Guid siteId, DateOnly first, DateOnly last, CancellationToken cancellationToken);

    /// <summary>The site's shift schedules, for naming shifts and their crews.</summary>
    Task<IReadOnlyList<(Guid Id, string Code, ShiftSchedule Schedule)>> ListSchedulesAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, string>> CrewNamesAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's breaks overlapping <c>[windowStart, windowEnd)</c>, of one person or everyone; corrections and replaced ones too (D-047).</summary>
    Task<IReadOnlyList<BreakRow>> ListBreaksAsync(Guid siteId, Guid? personId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken);

    /// <summary>What each line of the site requires, including what it inherits (D-048).</summary>
    Task<IReadOnlyList<LineRequirement>> ListLineRequirementsAsync(Guid siteId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PersonGrant>> ListSiteGrantsAsync(Guid siteId, CancellationToken cancellationToken);
}
