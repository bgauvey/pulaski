using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Shifts;
using Pulaski.Infrastructure.Persistence;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.Labor;

/// <summary>Write side for crews, people and labor (D-040). Every statement runs in the command's transaction.</summary>
internal sealed class LaborRepository(DbSession session) : ILaborRepository
{
    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')";

    private const string GetCrew = "SELECT id, site_id, code, name, is_active, xmin::text::bigint AS version FROM core.crew WHERE id = @Id FOR UPDATE";

    private const string CrewExists = "SELECT EXISTS (SELECT 1 FROM core.crew WHERE site_id = @SiteId AND code = @Code)";

    private const string InsertCrew = """
        INSERT INTO core.crew (id, site_id, code, name, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateCrew = "UPDATE core.crew SET name = @Name, is_active = @IsActive, updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint";

    private const string PersonColumns =
        "id, site_id, employee_number, display_name, crew_id, app_user_id, is_active, origin, external_system, external_key, external_version, xmin::text::bigint AS version";

    private const string GetPerson = $"SELECT {PersonColumns} FROM core.person WHERE id = @Id FOR UPDATE";

    private const string GetPeople = $"SELECT {PersonColumns} FROM core.person WHERE id = ANY(@Ids) ORDER BY id FOR UPDATE";

    private const string GetPersonOfUser = $"SELECT {PersonColumns} FROM core.person WHERE app_user_id = @UserId FOR UPDATE";

    private const string PersonByKey = $"SELECT {PersonColumns} FROM core.person WHERE external_system = @System AND external_key = @Key FOR UPDATE";

    private const string PersonByNumber = $"SELECT {PersonColumns} FROM core.person WHERE site_id = @SiteId AND employee_number = @Number FOR UPDATE";

    private const string CrewByCode = "SELECT id, site_id, code, name, is_active FROM core.crew WHERE site_id = @SiteId AND code = @Code";

    private const string NumberExists = "SELECT EXISTS (SELECT 1 FROM core.person WHERE site_id = @SiteId AND employee_number = @Number)";

    private const string FindUser = "SELECT id FROM sec.app_user WHERE idp_issuer = 'pulaski-local' AND lower(username) = @Username";

    private const string UserElsewhere = "SELECT EXISTS (SELECT 1 FROM core.person WHERE app_user_id = @UserId AND id <> @PersonId)";

    private const string InsertPerson = """
        INSERT INTO core.person (id, site_id, employee_number, display_name, crew_id, app_user_id, origin, external_system, external_key, external_version,
                                 is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @EmployeeNumber, @DisplayName, @CrewId, @AppUserId, @Origin, @ExternalSystem, @ExternalKey, @ExternalVersion, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdatePerson = """
        UPDATE core.person SET employee_number = @EmployeeNumber, display_name = @DisplayName, crew_id = @CrewId, app_user_id = @AppUserId, is_active = @IsActive,
            origin = @Origin, external_system = @ExternalSystem, external_key = @ExternalKey, external_version = @ExternalVersion, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetSchedule = "SELECT code, name, cycle_days, cycle_start, shifts::text AS shifts_json, overrides::text AS overrides_json FROM core.shift_schedule WHERE id = @Id";

    private const string TimeZone = "SELECT time_zone FROM core.equipment WHERE id = @Id AND level = 'site'";

    private const string GetJob = "SELECT id, site_id, job_number, equipment_id FROM core.job WHERE id = @Id";

    private const string IndirectReason = """
        SELECT EXISTS (SELECT 1 FROM core.reason_code
                       WHERE id = @Id AND reason_kind = 'indirect_labor' AND is_active AND is_selectable AND (site_id IS NULL OR site_id = @SiteId))
        """;

    private const string GetSession = $"SELECT {LaborSql.SessionColumns} FROM core.labor_session s WHERE s.id = @Id FOR UPDATE";

    private const string OpenOfPerson = $"SELECT {LaborSql.SessionColumns} FROM core.labor_session s WHERE s.person_id = @PersonId AND s.ended_at IS NULL ORDER BY s.id FOR UPDATE";

    private const string OpenOnEquipment = $"SELECT {LaborSql.SessionColumns} FROM core.labor_session s WHERE s.equipment_id = @EquipmentId AND s.ended_at IS NULL ORDER BY s.id FOR UPDATE";

    private const string ShiftSessions = $"""
        SELECT {LaborSql.SessionColumns} FROM core.labor_session s
        WHERE s.site_id = @SiteId AND s.production_date = @Date
          AND s.shift_schedule_id IS NOT DISTINCT FROM @ScheduleId::uuid AND s.shift_code IS NOT DISTINCT FROM @Code::text
        ORDER BY s.id
        FOR UPDATE
        """;

    private const string IsCorrected = "SELECT EXISTS (SELECT 1 FROM core.labor_session WHERE corrects_session_id = @Id)";

    private const string InsertSession = """
        INSERT INTO core.labor_session (id, site_id, person_id, equipment_id, job_id, labor_type, reason_code_id, started_at, ended_at, allocation_pct,
                                        production_date, shift_schedule_id, shift_code, source, corrects_session_id, is_void, note, recorded_by, created_at, updated_at)
        VALUES (@Id, @SiteId, @PersonId, @EquipmentId, @JobId, @LaborType, @ReasonCodeId, @StartedAt, @EndedAt, @AllocationPct,
                @ProductionDate, @ShiftScheduleId, @ShiftCode, @Source, @CorrectsSessionId, @IsVoid, @Note, @RecordedBy, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateSession = """
        UPDATE core.labor_session
        SET equipment_id = @EquipmentId, job_id = @JobId, labor_type = @LaborType, reason_code_id = @ReasonCodeId, started_at = @StartedAt,
            ended_at = @EndedAt, allocation_pct = @AllocationPct, production_date = @ProductionDate, shift_schedule_id = @ShiftScheduleId,
            shift_code = @ShiftCode, note = @Note, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string IsApproved = """
        SELECT EXISTS (SELECT 1 FROM core.labor_approval
                       WHERE site_id = @SiteId AND production_date = @Date
                         AND shift_schedule_id IS NOT DISTINCT FROM @ScheduleId::uuid AND shift_code IS NOT DISTINCT FROM @Code::text)
        """;

    private const string InsertApproval = """
        INSERT INTO core.labor_approval (id, site_id, production_date, shift_schedule_id, shift_code, approved_by, approved_by_user_id, approved_at, note)
        VALUES (@Id, @SiteId, @Date, @ScheduleId, @Code, @ApprovedBy, @UserId, @At, @Note)
        """;

    private const string GetBreak = $"SELECT {LaborSql.BreakColumns} FROM core.labor_break b WHERE b.id = @Id FOR UPDATE";

    private const string OpenBreak = $"SELECT {LaborSql.BreakColumns} FROM core.labor_break b WHERE b.person_id = @PersonId AND b.ended_at IS NULL FOR UPDATE";

    private const string BreaksOfPerson = $"""
        SELECT {LaborSql.BreakColumns} FROM core.labor_break b
        WHERE b.person_id = @PersonId AND b.started_at < @To AND (b.ended_at IS NULL OR b.ended_at > @From) AND {LaborSql.BreakCounts}
        ORDER BY b.started_at
        """;

    private const string SessionsAt = $"""
        SELECT {LaborSql.SessionColumns} FROM core.labor_session s
        WHERE s.person_id = @PersonId AND s.started_at <= @At AND (s.ended_at IS NULL OR s.ended_at > @At)
          AND NOT s.is_void AND NOT EXISTS (SELECT 1 FROM core.labor_session c WHERE c.corrects_session_id = s.id)
        ORDER BY s.started_at, s.id
        """;

    private const string ShiftBreaks = $"""
        SELECT {LaborSql.BreakColumns} FROM core.labor_break b
        WHERE b.site_id = @SiteId AND b.production_date = @Date
          AND b.shift_schedule_id IS NOT DISTINCT FROM @ScheduleId::uuid AND b.shift_code IS NOT DISTINCT FROM @Code::text
        ORDER BY b.id
        FOR UPDATE
        """;

    private const string IsBreakCorrected = "SELECT EXISTS (SELECT 1 FROM core.labor_break WHERE corrects_break_id = @Id)";

    private const string InsertBreak = """
        INSERT INTO core.labor_break (id, site_id, person_id, started_at, ended_at, is_paid, production_date, shift_schedule_id, shift_code,
                                      source, corrects_break_id, is_void, note, recorded_by, created_at, updated_at)
        VALUES (@Id, @SiteId, @PersonId, @StartedAt, @EndedAt, @IsPaid, @ProductionDate, @ShiftScheduleId, @ShiftCode,
                @Source, @CorrectsBreakId, @IsVoid, @Note, @RecordedBy, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateBreak = """
        UPDATE core.labor_break
        SET started_at = @StartedAt, ended_at = @EndedAt, is_paid = @IsPaid, production_date = @ProductionDate,
            shift_schedule_id = @ShiftScheduleId, shift_code = @ShiftCode, note = @Note, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string RequiredOn = $"{LaborSql.Requirements} WHERE c.descendant_id = @Id ORDER BY q.code";

    private const string GrantsOfPerson = $"SELECT {LaborSql.GrantColumns} FROM core.person_qualification g WHERE g.person_id = @Id";

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) => session.ExecuteScalarAsync<bool>(IsSite, new { Id = siteId }, cancellationToken);

    public async Task<Versioned<BreakRecord>?> GetBreakForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LaborSql.BreakDbRow>(GetBreak, new { Id = id }, cancellationToken) is { } r ? new(r.ToRecord(), r.Version) : null;

    public async Task<Versioned<BreakRecord>?> GetOpenBreakForUpdateAsync(Guid personId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LaborSql.BreakDbRow>(OpenBreak, new { PersonId = personId }, cancellationToken) is { } r ? new(r.ToRecord(), r.Version) : null;

    public async Task<IReadOnlyList<BreakRecord>> ListBreaksOfPersonAsync(Guid personId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.BreakDbRow>(BreaksOfPerson, new { PersonId = personId, From = windowStart, To = windowEnd }, cancellationToken)).Select(r => r.ToRecord())];

    public async Task<IReadOnlyList<SessionRecord>> ListSessionsOfPersonAtAsync(Guid personId, DateTimeOffset at, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.SessionDbRow>(SessionsAt, new { PersonId = personId, At = at }, cancellationToken)).Select(r => r.ToRecord())];

    public async Task<IReadOnlyList<BreakRecord>> ListShiftBreaksForUpdateAsync(ShiftKey shift, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.BreakDbRow>(ShiftBreaks, KeyArgs(shift), cancellationToken)).Select(r => r.ToRecord())];

    public Task<bool> IsBreakCorrectedAsync(Guid breakId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsBreakCorrected, new { Id = breakId }, cancellationToken);

    public Task<long> InsertBreakAsync(BreakRecord record, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertBreak, BreakArgs(record, at), cancellationToken);

    public Task<long> UpdateBreakAsync(BreakRecord record, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateBreak, BreakArgs(record, at), cancellationToken);

    public async Task<IReadOnlyList<RequiredQualification>> ListRequiredQualificationsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.RequirementDbRow>(RequiredOn, new { Id = equipmentId }, cancellationToken))
            .Select(r => new RequiredQualification(r.QualificationId, r.Code, r.Name))];

    public async Task<IReadOnlyList<QualificationGrant>> ListGrantsOfPersonAsync(Guid personId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.GrantDbRow>(GrantsOfPerson, new { Id = personId }, cancellationToken))
            .Select(r => new QualificationGrant(r.QualificationId, r.ValidFrom, r.ExpiresOn, r.RevokedAt))];

    private static object BreakArgs(BreakRecord b, DateTimeOffset at) => new
    {
        b.Id,
        b.SiteId,
        b.PersonId,
        b.StartedAt,
        b.EndedAt,
        b.IsPaid,
        b.ProductionDate,
        b.ShiftScheduleId,
        b.ShiftCode,
        b.Source,
        b.CorrectsBreakId,
        b.IsVoid,
        b.Note,
        b.RecordedBy,
        At = at,
    };

    public async Task<Versioned<CrewRecord>?> GetCrewForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CrewRow>(GetCrew, new { Id = id }, cancellationToken) is { } r
            ? new Versioned<CrewRecord>(new CrewRecord(r.Id, r.SiteId, r.Code, r.Name, r.IsActive), r.Version)
            : null;

    public Task<bool> CrewCodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CrewExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<long> InsertCrewAsync(CrewRecord crew, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertCrew, new { crew.Id, crew.SiteId, crew.Code, crew.Name, crew.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateCrewAsync(CrewRecord crew, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateCrew, new { crew.Id, crew.Name, crew.IsActive, At = at }, cancellationToken);

    public async Task<Versioned<PersonRecord>?> GetPersonForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<PersonRow>(GetPerson, new { Id = id }, cancellationToken))?.ToVersioned();

    public async Task<IReadOnlyList<PersonRecord>> GetPeopleForUpdateAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<PersonRow>(GetPeople, new { Ids = ids.ToArray() }, cancellationToken)).Select(r => r.ToVersioned().Value)];

    public async Task<Versioned<PersonRecord>?> GetPersonOfUserForUpdateAsync(Guid userId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<PersonRow>(GetPersonOfUser, new { UserId = userId }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<PersonRecord>?> FindPersonByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<PersonRow>(PersonByKey, new { System = externalSystem, Key = externalKey }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<PersonRecord>?> FindPersonByNumberForUpdateAsync(Guid siteId, string employeeNumber, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<PersonRow>(PersonByNumber, new { SiteId = siteId, Number = employeeNumber }, cancellationToken))?.ToVersioned();

    public async Task<CrewRecord?> FindCrewByCodeAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CrewRow>(CrewByCode, new { SiteId = siteId, Code = code }, cancellationToken) is { } r
            ? new CrewRecord(r.Id, r.SiteId, r.Code, r.Name, r.IsActive)
            : null;

    public Task<bool> EmployeeNumberExistsAsync(Guid siteId, string employeeNumber, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(NumberExists, new { SiteId = siteId, Number = employeeNumber }, cancellationToken);

    public Task<Guid?> FindLocalUserIdAsync(string username, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(FindUser, new { Username = username }, cancellationToken);

    public Task<bool> UserIsAnotherPersonAsync(Guid userId, Guid exceptPersonId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(UserElsewhere, new { UserId = userId, PersonId = exceptPersonId }, cancellationToken);

    public Task<long> InsertPersonAsync(PersonRecord person, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertPerson, PersonArgs(person, at), cancellationToken);

    public Task<long> UpdatePersonAsync(PersonRecord person, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdatePerson, PersonArgs(person, at), cancellationToken);

    public async Task<EquipmentShifts?> GetEquipmentShiftsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ShiftSql.CalendarRow>(ShiftSql.EquipmentCalendars, new { Ids = new[] { equipmentId } }, cancellationToken)) is { } row
            ? LaborSql.ToShifts(row)
            : null;

    public async Task<ShiftSchedule?> GetScheduleAsync(Guid scheduleId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ScheduleRow>(GetSchedule, new { Id = scheduleId }, cancellationToken) is { } r
            ? Application.Shifts.ShiftMapping.ToDomain(r.Code, r.Name, r.CycleDays, r.CycleStart, ShiftSql.Read(r.ShiftsJson), ShiftSql.ReadOverrides(r.OverridesJson))
            : null;

    public Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<string?>(TimeZone, new { Id = siteId }, cancellationToken);

    public async Task<LaborJob?> GetJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<JobRow>(GetJob, new { Id = jobId }, cancellationToken) is { } r ? new LaborJob(r.Id, r.SiteId, r.JobNumber, r.EquipmentId) : null;

    public Task<bool> IsIndirectReasonAsync(Guid reasonCodeId, Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IndirectReason, new { Id = reasonCodeId, SiteId = siteId }, cancellationToken);

    public async Task<Versioned<SessionRecord>?> GetSessionForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<LaborSql.SessionDbRow>(GetSession, new { Id = id }, cancellationToken) is { } r
            ? new Versioned<SessionRecord>(r.ToRecord(), r.Version)
            : null;

    public async Task<IReadOnlyList<SessionRecord>> ListOpenSessionsOfPersonForUpdateAsync(Guid personId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.SessionDbRow>(OpenOfPerson, new { PersonId = personId }, cancellationToken)).Select(r => r.ToRecord())];

    public async Task<IReadOnlyList<Versioned<SessionRecord>>> ListOpenSessionsOnEquipmentForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.SessionDbRow>(OpenOnEquipment, new { EquipmentId = equipmentId }, cancellationToken))
            .Select(r => new Versioned<SessionRecord>(r.ToRecord(), r.Version))];

    public async Task<IReadOnlyList<SessionRecord>> ListShiftSessionsForUpdateAsync(ShiftKey shift, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LaborSql.SessionDbRow>(ShiftSessions, KeyArgs(shift), cancellationToken)).Select(r => r.ToRecord())];

    public Task<bool> IsCorrectedAsync(Guid sessionId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsCorrected, new { Id = sessionId }, cancellationToken);

    public Task<long> InsertSessionAsync(SessionRecord s, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertSession, SessionArgs(s, at), cancellationToken);

    public Task<long> UpdateSessionAsync(SessionRecord s, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateSession, SessionArgs(s, at), cancellationToken);

    public Task<bool> IsApprovedAsync(ShiftKey shift, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsApproved, KeyArgs(shift), cancellationToken);

    public Task InsertApprovalAsync(Guid id, ShiftKey shift, string approvedBy, Guid? approvedByUserId, DateTimeOffset at, string? note, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertApproval, new
        {
            Id = id,
            shift.SiteId,
            Date = shift.ProductionDate,
            ScheduleId = shift.ShiftScheduleId,
            Code = shift.ShiftCode,
            ApprovedBy = approvedBy,
            UserId = approvedByUserId,
            At = at,
            Note = note,
        }, cancellationToken);

    private static object KeyArgs(ShiftKey k) => new { k.SiteId, Date = k.ProductionDate, ScheduleId = k.ShiftScheduleId, Code = k.ShiftCode };

    private static object PersonArgs(PersonRecord p, DateTimeOffset at) =>
        new { p.Id, p.SiteId, p.EmployeeNumber, p.DisplayName, p.CrewId, p.AppUserId, p.IsActive, p.Origin, p.ExternalSystem, p.ExternalKey, p.ExternalVersion, At = at };

    private static object SessionArgs(SessionRecord s, DateTimeOffset at) => new
    {
        s.Id,
        s.SiteId,
        s.PersonId,
        s.EquipmentId,
        s.JobId,
        s.LaborType,
        s.ReasonCodeId,
        s.StartedAt,
        s.EndedAt,
        s.AllocationPct,
        s.ProductionDate,
        s.ShiftScheduleId,
        s.ShiftCode,
        s.Source,
        s.CorrectsSessionId,
        s.IsVoid,
        s.Note,
        s.RecordedBy,
        At = at,
    };

    private sealed class CrewRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }

    private sealed class PersonRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string EmployeeNumber { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public Guid? CrewId { get; init; }
        public Guid? AppUserId { get; init; }
        public bool IsActive { get; init; }
        public string Origin { get; init; } = "";
        public string? ExternalSystem { get; init; }
        public string? ExternalKey { get; init; }
        public string? ExternalVersion { get; init; }
        public long Version { get; init; }

        public Versioned<PersonRecord> ToVersioned() =>
            new(new PersonRecord(Id, SiteId, EmployeeNumber, DisplayName, CrewId, AppUserId, IsActive, Origin, ExternalSystem, ExternalKey, ExternalVersion), Version);
    }

    private sealed class ScheduleRow
    {
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public int CycleDays { get; init; }
        public DateOnly CycleStart { get; init; }
        public string ShiftsJson { get; init; } = "[]";
        public string OverridesJson { get; init; } = "[]";
    }

    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string JobNumber { get; init; } = "";
        public Guid? EquipmentId { get; init; }
    }
}
