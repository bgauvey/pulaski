using System.Text.Json;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Shifts;

namespace Pulaski.Infrastructure.Shifts;

/// <summary>SQL and the stored shape of shift schedules (D-038): the shifts are one camelCase jsonb document, as the API carries them.</summary>
internal static class ShiftSql
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string Columns = """
        s.id, s.site_id, site.code AS site_code, s.code, s.name, s.description, s.cycle_days, s.cycle_start, s.shifts::text AS shifts_json,
        s.is_active, s.xmin::text::bigint AS version
        """;

    /// <summary>
    /// Where each piece of equipment's working time comes from (D-038): the nearest equipment, itself first, that doesn't
    /// inherit, with that one's schedule, and the equipment's own site and time zone.
    /// </summary>
    public const string EquipmentCalendars = """
        SELECT t.id AS equipment_id, t.site_id, t.code, coalesce(site.time_zone, 'UTC') AS time_zone,
               src.code AS source_code, coalesce(src.calendar_mode, 'inherit') AS mode,
               s.id AS schedule_id, s.code AS schedule_code, s.name AS schedule_name, s.cycle_days, s.cycle_start, s.shifts::text AS shifts_json
        FROM core.equipment t
        LEFT JOIN core.equipment site ON site.id = t.site_id
        LEFT JOIN LATERAL (
            SELECT e.code, e.calendar_mode, e.shift_schedule_id
            FROM core.equipment_closure c
            JOIN core.equipment e ON e.id = c.ancestor_id
            WHERE c.descendant_id = t.id AND e.calendar_mode <> 'inherit'
            ORDER BY c.depth
            LIMIT 1
        ) src ON true
        LEFT JOIN core.shift_schedule s ON s.id = src.shift_schedule_id
        WHERE t.id = ANY(@Ids)
        """;

    /// <summary>A row of <see cref="EquipmentCalendars"/>.</summary>
    public sealed class CalendarRow
    {
        public Guid EquipmentId { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public string TimeZone { get; init; } = "UTC";
        public string? SourceCode { get; init; }
        public string Mode { get; init; } = "inherit";
        public Guid? ScheduleId { get; init; }
        public string? ScheduleCode { get; init; }
        public string? ScheduleName { get; init; }
        public int CycleDays { get; init; }
        public DateOnly CycleStart { get; init; }
        public string? ShiftsJson { get; init; }

        /// <summary>The schedule it follows, or null (none set, or around the clock).</summary>
        public Domain.Shifts.ShiftSchedule? Schedule() => Mode == "schedule" && ScheduleCode is { } code
            ? ShiftMapping.ToDomain(code, ScheduleName ?? code, CycleDays, CycleStart, Read(ShiftsJson ?? "[]"))
            : null;
    }

    public static string Write(IReadOnlyList<ShiftDto> shifts) => JsonSerializer.Serialize(shifts, Json);

    public static IReadOnlyList<ShiftDto> Read(string json) => JsonSerializer.Deserialize<List<ShiftDto>>(json, Json) ?? [];

    public sealed class Row
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string? SiteCode { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public int CycleDays { get; init; }
        public DateOnly CycleStart { get; init; }
        public string ShiftsJson { get; init; } = "[]";
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public ShiftScheduleRecord ToRecord() => new(Id, SiteId, Code, Name, Description, CycleDays, CycleStart, Read(ShiftsJson), IsActive);

        public ShiftScheduleDto ToDto(IReadOnlyList<string> usedBy)
        {
            var shifts = Read(ShiftsJson);
            return new ShiftScheduleDto
            {
                Id = Id,
                SiteId = SiteId,
                SiteCode = SiteCode,
                Code = Code,
                Name = Name,
                Description = Description,
                CycleDays = CycleDays,
                CycleStart = CycleStart,
                Shifts = shifts,
                IsActive = IsActive,
                HoursPerCycle = ShiftMapping.Hours(ShiftMapping.ToDomain(Code, Name, CycleDays, CycleStart, shifts).HoursPerCycle),
                UsedBy = usedBy,
                Version = Version,
            };
        }
    }
}
