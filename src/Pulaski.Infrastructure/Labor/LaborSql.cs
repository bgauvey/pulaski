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
