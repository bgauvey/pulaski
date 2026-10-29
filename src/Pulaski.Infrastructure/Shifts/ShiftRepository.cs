using Pulaski.Application.Abstractions;
using Pulaski.Application.Shifts;
using Pulaski.Infrastructure.Persistence;

using Pulaski.Infrastructure.MachineData;

namespace Pulaski.Infrastructure.Shifts;

/// <summary>Write side for shift schedules and the working time equipment follows (D-038).</summary>
internal sealed class ShiftRepository(DbSession session) : IShiftRepository
{
    private const string GetSchedule = $"""
        SELECT {ShiftSql.Columns}
        FROM core.shift_schedule s
        JOIN core.equipment site ON site.id = s.site_id
        WHERE s.id = @Id
        """;

    private const string CodeExists = "SELECT EXISTS (SELECT 1 FROM core.shift_schedule WHERE site_id = @SiteId AND code = @Code)";

    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site')";

    private const string Insert = """
        INSERT INTO core.shift_schedule (id, site_id, code, name, description, cycle_days, cycle_start, shifts, overrides, is_active, created_at, updated_at)
        VALUES (@Id, @SiteId, @Code, @Name, @Description, @CycleDays, @CycleStart, @Shifts::jsonb, @Overrides::jsonb, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.shift_schedule
        SET name = @Name, description = @Description, cycle_days = @CycleDays, cycle_start = @CycleStart, shifts = @Shifts::jsonb,
            overrides = @Overrides::jsonb, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string CrewCodes = "SELECT code FROM core.crew WHERE site_id = @SiteId AND is_active ORDER BY code";

    private const string Users = "SELECT code FROM core.equipment WHERE shift_schedule_id = @Id ORDER BY code";

    private const string ShiftApproved = """
        SELECT EXISTS (SELECT 1 FROM core.labor_approval
                       WHERE site_id = @SiteId AND production_date = @Date AND shift_schedule_id = @ScheduleId AND shift_code = @Code)
        """;

    private const string GetCalendar = """
        SELECT id, site_id, code, level, calendar_mode AS mode, shift_schedule_id, xmin::text::bigint AS version
        FROM core.equipment WHERE id = @Id
        FOR UPDATE
        """;

    private const string SetCalendar = """
        UPDATE core.equipment SET calendar_mode = @Mode, shift_schedule_id = @ScheduleId, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<ShiftScheduleRecord>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ShiftSql.Row>(GetSchedule + "\nFOR UPDATE OF s", new { Id = id }, cancellationToken) is { } row
            ? new Versioned<ShiftScheduleRecord>(row.ToRecord(), row.Version)
            : null;

    public async Task<ShiftScheduleRecord?> GetForShareAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ShiftSql.Row>(GetSchedule + "\nFOR SHARE OF s", new { Id = id }, cancellationToken))?.ToRecord();

    public Task<bool> CodeExistsAsync(Guid siteId, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(CodeExists, new { SiteId = siteId, Code = code }, cancellationToken);

    public Task<bool> IsSiteAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsSite, new { Id = siteId }, cancellationToken);

    public Task<long> InsertAsync(ShiftScheduleRecord schedule, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(schedule, at), cancellationToken);

    public async Task<long> UpdateAsync(ShiftScheduleRecord schedule, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(Update, Args(schedule, at), cancellationToken);
        await OeeRollupMarks.SiteChangedAsync(session, schedule.SiteId, cancellationToken);
        return version;
    }

    public async Task<IReadOnlyList<string>> ListCrewCodesAsync(Guid siteId, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<string>(CrewCodes, new { SiteId = siteId }, cancellationToken)];

    public async Task<IReadOnlyList<string>> ListUsersAsync(Guid scheduleId, CancellationToken cancellationToken) =>
        [.. await session.QueryAsync<string>(Users, new { Id = scheduleId }, cancellationToken)];

    public async Task<Versioned<EquipmentCalendar>?> GetEquipmentCalendarForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CalendarRow>(GetCalendar, new { Id = equipmentId }, cancellationToken) is { } r
            ? new Versioned<EquipmentCalendar>(new EquipmentCalendar(r.Id, r.SiteId, r.Code, r.Level, r.Mode, r.ShiftScheduleId), r.Version)
            : null;

    public async Task<long> SetEquipmentCalendarAsync(Guid equipmentId, string mode, Guid? shiftScheduleId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(SetCalendar, new { Id = equipmentId, Mode = mode, ScheduleId = shiftScheduleId, At = at }, cancellationToken);
        await OeeRollupMarks.SiteOfEquipmentChangedAsync(session, equipmentId, cancellationToken);
        return version;
    }

    public Task<bool> IsShiftApprovedAsync(Guid siteId, Guid scheduleId, DateOnly productionDate, string code, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(ShiftApproved, new { SiteId = siteId, ScheduleId = scheduleId, Date = productionDate, Code = code }, cancellationToken);

    private static object Args(ShiftScheduleRecord s, DateTimeOffset at) => new
    {
        s.Id,
        s.SiteId,
        s.Code,
        s.Name,
        s.Description,
        s.CycleDays,
        s.CycleStart,
        Shifts = ShiftSql.Write(s.Shifts),
        Overrides = ShiftSql.WriteOverrides(s.Overrides),
        s.IsActive,
        At = at,
    };

    private sealed class CalendarRow
    {
        public Guid Id { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Level { get; init; } = "";
        public string Mode { get; init; } = "";
        public Guid? ShiftScheduleId { get; init; }
        public long Version { get; init; }
    }
}
