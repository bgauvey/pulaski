using Pulaski.Application.Labor;
using Pulaski.Infrastructure.Shifts;

namespace Pulaski.Infrastructure.Labor;

/// <summary>SQL shared by the labor repository and read store (D-040).</summary>
internal static class LaborSql
{
    public const string SessionColumns = """
        s.id, s.site_id, s.person_id, s.equipment_id, s.job_id, s.labor_type, s.reason_code_id, s.started_at, s.ended_at, s.allocation_pct,
        s.production_date, s.shift_schedule_id, s.shift_code, s.source, s.corrects_session_id, s.is_void, s.note, s.recorded_by,
        s.xmin::text::bigint AS version
        """;

    // A session's shift is approved when an approval names the same site, day, schedule and shift (both null for a day).
    public const string IsApprovedExpr = """
        EXISTS (SELECT 1 FROM core.labor_approval a
                WHERE a.site_id = s.site_id AND a.production_date = s.production_date
                  AND a.shift_schedule_id IS NOT DISTINCT FROM s.shift_schedule_id AND a.shift_code IS NOT DISTINCT FROM s.shift_code)
        """;

    public const string RowColumns = $"""
        {SessionColumns}, p.display_name AS person_name, p.employee_number, e.code AS equipment_code, j.job_number, rc.name AS reason_name,
        EXISTS (SELECT 1 FROM core.labor_session c WHERE c.corrects_session_id = s.id) AS is_superseded,
        {IsApprovedExpr} AS is_approved
        """;

    public const string RowJoins = """
        FROM core.labor_session s
        JOIN core.person p ON p.id = s.person_id
        JOIN core.equipment e ON e.id = s.equipment_id
        LEFT JOIN core.job j ON j.id = s.job_id
        LEFT JOIN core.reason_code rc ON rc.id = s.reason_code_id
        """;

    // ---- Breaks (D-047) ----

    public const string BreakColumns = """
        b.id, b.site_id, b.person_id, b.started_at, b.ended_at, b.is_paid, b.production_date, b.shift_schedule_id, b.shift_code,
        b.source, b.corrects_break_id, b.is_void, b.note, b.recorded_by, b.xmin::text::bigint AS version
        """;

    /// <summary>A break counts unless it voids another or a correction replaced it.</summary>
    public const string BreakCounts = "NOT b.is_void AND NOT EXISTS (SELECT 1 FROM core.labor_break c WHERE c.corrects_break_id = b.id)";

    public const string BreakRowColumns = $"""
        {BreakColumns}, p.display_name AS person_name, p.employee_number,
        EXISTS (SELECT 1 FROM core.labor_break c WHERE c.corrects_break_id = b.id) AS is_superseded,
        EXISTS (SELECT 1 FROM core.labor_approval a
                WHERE a.site_id = b.site_id AND a.production_date = b.production_date
                  AND a.shift_schedule_id IS NOT DISTINCT FROM b.shift_schedule_id AND a.shift_code IS NOT DISTINCT FROM b.shift_code) AS is_approved
        """;

    // ---- Qualifications (D-048) ----

    /// <summary>What equipment requires, itself and through every equipment above it: active qualifications, requirements switched on.</summary>
    public const string Requirements = """
        SELECT c.descendant_id AS equipment_id, q.id AS qualification_id, q.code, q.name, eq.equipment_id AS from_equipment_id, src.code AS from_equipment_code
        FROM core.equipment_closure c
        JOIN core.equipment_qualification eq ON eq.equipment_id = c.ancestor_id AND eq.is_required
        JOIN core.qualification q ON q.id = eq.qualification_id AND q.is_active
        JOIN core.equipment src ON src.id = eq.equipment_id
        """;

    public const string GrantColumns = """
        g.id, g.person_id, g.qualification_id, g.valid_from, g.expires_on, g.note, g.granted_by, g.revoked_at, g.revoked_by, g.revoke_reason,
        g.xmin::text::bigint AS version
        """;

    internal sealed class RequirementDbRow
    {
        public Guid EquipmentId { get; init; }
        public Guid QualificationId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public Guid FromEquipmentId { get; init; }
        public string FromEquipmentCode { get; init; } = "";
    }

    internal class GrantDbRow
    {
        public Guid Id { get; init; }
        public Guid PersonId { get; init; }
        public Guid QualificationId { get; init; }
        public DateOnly ValidFrom { get; init; }
        public DateOnly? ExpiresOn { get; init; }
        public string? Note { get; init; }
        public string GrantedBy { get; init; } = "";
        public DateTimeOffset? RevokedAt { get; init; }
        public string? RevokedBy { get; init; }
        public string? RevokeReason { get; init; }
        public long Version { get; init; }

        public GrantRecord ToRecord() => new(Id, PersonId, QualificationId, ValidFrom, ExpiresOn, Note, GrantedBy, RevokedAt, RevokedBy, RevokeReason);
    }

    internal class BreakDbRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid PersonId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public bool IsPaid { get; init; }
        public DateOnly ProductionDate { get; init; }
        public Guid? ShiftScheduleId { get; init; }
        public string? ShiftCode { get; init; }
        public string Source { get; init; } = "";
        public Guid? CorrectsBreakId { get; init; }
        public bool IsVoid { get; init; }
        public string? Note { get; init; }
        public string RecordedBy { get; init; } = "";
        public long Version { get; init; }

        public BreakRecord ToRecord() => new(Id, SiteId, PersonId, StartedAt, EndedAt, IsPaid, ProductionDate, ShiftScheduleId, ShiftCode,
            Source, CorrectsBreakId, IsVoid, Note, RecordedBy);
    }

    internal sealed class BreakViewRow : BreakDbRow
    {
        public string PersonName { get; init; } = "";
        public string EmployeeNumber { get; init; } = "";
        public bool IsSuperseded { get; init; }
        public bool IsApproved { get; init; }

        public BreakRow ToRow() => new()
        {
            Break = ToRecord(),
            PersonName = PersonName,
            EmployeeNumber = EmployeeNumber,
            IsSuperseded = IsSuperseded,
            IsApproved = IsApproved,
            Version = Version,
        };
    }

    public static EquipmentShifts ToShifts(ShiftSql.CalendarRow row) =>
        new(row.EquipmentId, row.SiteId, row.Code, TimeZoneInfo.FindSystemTimeZoneById(row.TimeZone),
            row.Mode == "schedule" ? row.ScheduleId : null, row.Mode == "schedule" ? row.ScheduleCode : null, row.Schedule());

    internal class SessionDbRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid PersonId { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid? JobId { get; init; }
        public string LaborType { get; init; } = "";
        public Guid? ReasonCodeId { get; init; }
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset? EndedAt { get; init; }
        public decimal AllocationPct { get; init; }
        public DateOnly ProductionDate { get; init; }
        public Guid? ShiftScheduleId { get; init; }
        public string? ShiftCode { get; init; }
        public string Source { get; init; } = "";
        public Guid? CorrectsSessionId { get; init; }
        public bool IsVoid { get; init; }
        public string? Note { get; init; }
        public string RecordedBy { get; init; } = "";
        public long Version { get; init; }

        public SessionRecord ToRecord() => new(Id, SiteId, PersonId, EquipmentId, JobId, LaborType, ReasonCodeId, StartedAt, EndedAt, AllocationPct,
            ProductionDate, ShiftScheduleId, ShiftCode, Source, CorrectsSessionId, IsVoid, Note, RecordedBy);
    }

    internal sealed class SessionViewRow : SessionDbRow
    {
        public string PersonName { get; init; } = "";
        public string EmployeeNumber { get; init; } = "";
        public string EquipmentCode { get; init; } = "";
        public string? JobNumber { get; init; }
        public string? ReasonName { get; init; }
        public bool IsSuperseded { get; init; }
        public bool IsApproved { get; init; }

        public SessionRow ToRow() => new()
        {
            Session = ToRecord(),
            PersonName = PersonName,
            EmployeeNumber = EmployeeNumber,
            EquipmentCode = EquipmentCode,
            JobNumber = JobNumber,
            ReasonName = ReasonName,
            IsSuperseded = IsSuperseded,
            IsApproved = IsApproved,
            Version = Version,
        };
    }
}
