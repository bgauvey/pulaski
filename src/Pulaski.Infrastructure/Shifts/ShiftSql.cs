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
