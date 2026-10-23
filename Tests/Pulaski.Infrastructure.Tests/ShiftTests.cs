using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain;
using Pulaski.Domain.Shifts;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Shift schedules end to end (D-038): saved, followed by equipment, inherited, and used by the timeline.</summary>
[Collection(DatabaseTests.Name)]
public sealed class ShiftTests(DatabaseFixture db)
{
    private readonly string _s = Guid.NewGuid().ToString("N")[..6];

    private async Task<TResult> QueryAsync<TQuery, TResult>(TQuery query)
        where TQuery : IQuery<TResult>
    {
        await using var scope = db.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IQueryHandler<TQuery, TResult>>().HandleAsync(query, CancellationToken.None);
    }

    private async Task<long> VersionAsync(Guid id) =>
        (await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(id))).Version;

    private Task<VersionResponse> SetAsync(Guid id, long version, string mode, Guid? schedule = null) =>
        db.SendAsync(new SetEquipmentCalendarCommand(id, version, mode, schedule));

    private static ShiftDto Shift(string code, string start, int hours) => new() { Code = code, Name = code, Start = start, Minutes = hours * 60, Days = [0, 1, 2, 3, 4] };

    [Fact]
    public async Task Lines_follow_the_nearest_schedule_above_them_unless_they_have_their_own()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "HENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "HS-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        CreatedResponse area = await db.SendAsync(new CreateEquipmentCommand(site.Id, "HA-" + _s, "Area", null, "area", "area", false, false, false, null));
        CreatedResponse line = await db.SendAsync(new CreateEquipmentCommand(area.Id, "HL-" + _s, "Line", null, "work_center", "production_line", true, true, false, null));

        var monday = new DateOnly(2026, 10, 12);
        CreatedResponse twoShifts = await db.SendAsync(new CreateShiftScheduleCommand(site.Id, "2x8", "Two shifts", "Weekdays", 7, monday, [Shift("A", "06:00", 8), Shift("B", "14:00", 8)]));
        CreatedResponse oneShift = await db.SendAsync(new CreateShiftScheduleCommand(site.Id, "1X8", "One shift", null, 7, monday, [Shift("A", "06:00", 8)]));

        var none = await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(line.Id));
        Assert.Equal((false, (string?)null), (none.HasShiftCalendar, none.CalendarName));

        await SetAsync(site.Id, await VersionAsync(site.Id), CalendarModes.Schedule, twoShifts.Id);
        var inherited = await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(line.Id));
        Assert.True(inherited.HasShiftCalendar);
        Assert.Equal($"2X8 · Two shifts, from HS-{_s}", inherited.CalendarName);
        Assert.Empty(inherited.Warnings);

        await SetAsync(area.Id, await VersionAsync(area.Id), CalendarModes.Schedule, oneShift.Id);
        Assert.Equal($"1X8 · One shift, from HA-{_s}", (await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(line.Id))).CalendarName);

        await SetAsync(line.Id, await VersionAsync(line.Id), CalendarModes.AroundTheClock);
        var own = await QueryAsync<GetLineTimelineQuery, LineTimelineDto>(new GetLineTimelineQuery(line.Id));
        Assert.Equal((true, $"Around the clock, from HL-{_s}"), (own.HasShiftCalendar, own.CalendarName));

        var equipment = await QueryAsync<GetEquipmentQuery, EquipmentDto>(new GetEquipmentQuery(area.Id));
        Assert.Equal((CalendarModes.Schedule, (Guid?)oneShift.Id), (equipment.CalendarMode, equipment.ShiftScheduleId));

        var schedules = await QueryAsync<ListShiftSchedulesQuery, IReadOnlyList<ShiftScheduleDto>>(new ListShiftSchedulesQuery(site.Id));
        var two = Assert.Single(schedules, s => s.Code == "2X8");
        Assert.Equal((80m, $"HS-{_s}", 2), (two.HoursPerCycle, two.SiteCode, two.Shifts.Count));
        Assert.Equal([$"HS-{_s}"], two.UsedBy);

        // In use, it can't be switched off; its shifts can still change, and the version moves on.
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            db.SendAsync(new UpdateShiftScheduleCommand(twoShifts.Id, two.Version, two.Name, null, 7, monday, two.Shifts, IsActive: false)));
        var changed = await db.SendAsync(new UpdateShiftScheduleCommand(twoShifts.Id, two.Version, "Two long shifts", null, 7, monday, [Shift("A", "06:00", 10)], IsActive: true));
        Assert.NotEqual(two.Version, changed.Version);
        await Assert.ThrowsAsync<ConflictException>(() =>
            db.SendAsync(new UpdateShiftScheduleCommand(twoShifts.Id, two.Version, "Stale", null, 7, monday, [], IsActive: true)));
    }

    [Fact]
    public async Task Shifts_are_stored_as_one_document()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "JENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "JS-" + _s, "Site", null, "site", "site", false, false, false, "Europe/Berlin"));
        CreatedResponse created = await db.SendAsync(new CreateShiftScheduleCommand(site.Id, "NIGHTS", "Nights", null, 1, new DateOnly(2026, 10, 1), [new ShiftDto { Code = "C", Start = "22:00", Minutes = 480, Days = [0] }]));

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var json = await connection.ExecuteScalarAsync<string>("SELECT shifts::text FROM core.shift_schedule WHERE id = @Id", new { created.Id });
        Assert.Contains("\"start\": \"22:00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"C\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_off_changes_are_kept_with_the_schedule_and_read_back()
    {
        CreatedResponse enterprise = await db.SendAsync(new CreateEquipmentCommand(null, "KENT-" + _s, "Enterprise", null, "enterprise", "enterprise", false, false, false, null));
        CreatedResponse site = await db.SendAsync(new CreateEquipmentCommand(enterprise.Id, "KS-" + _s, "Site", null, "site", "site", false, false, false, "America/Chicago"));
        var monday = new DateOnly(2026, 10, 12);
        CreatedResponse created = await db.SendAsync(new CreateShiftScheduleCommand(site.Id, "1X8", "One shift", null, 7, monday, [Shift("A", "06:00", 8)]));

        var saturday = monday.AddDays(5);
        var set = await db.SendAsync(new SetShiftOverrideCommand(created.Id, created.Version, saturday, "a", true, null, null, 240, null, "Overtime"));
        var off = await db.SendAsync(new SetShiftOverrideCommand(created.Id, set.Version, monday, "A", false, null, null, null, null, null));

        var schedule = await QueryAsync<GetShiftScheduleQuery, ShiftScheduleDto>(new GetShiftScheduleQuery(created.Id));
        Assert.Equal([(monday, "A", false), (saturday, "A", true)], schedule.Overrides.Select(o => (o.Date, o.Code, o.IsWorking)));
        Assert.Equal((240, "06:00", "Overtime"), (schedule.Overrides[1].Minutes, schedule.Overrides[1].Start, schedule.Overrides[1].Note));
        Assert.Equal(off.Version, schedule.Version);

        var removed = await db.SendAsync(new RemoveShiftOverrideCommand(created.Id, off.Version, monday, "a"));
        schedule = await QueryAsync<GetShiftScheduleQuery, ShiftScheduleDto>(new GetShiftScheduleQuery(created.Id));
        Assert.Equal([saturday], schedule.Overrides.Select(o => o.Date));
        await Assert.ThrowsAsync<ConflictException>(() => db.SendAsync(new RemoveShiftOverrideCommand(created.Id, off.Version, saturday, "A")));
        Assert.Equal(removed.Version, schedule.Version);
    }
}
