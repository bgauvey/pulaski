using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Shifts;

/// <summary>Maintaining shift schedules and choosing which one equipment follows (D-038).</summary>
public class ShiftHandlerTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);
    private static readonly ShiftDto Days = new() { Code = "a", Name = "Days", Start = "06:00", Minutes = 480, Days = [4, 0, 1, 2, 3, 3] };

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _otherSite = Guid.CreateVersion7();

    public ShiftHandlerTests()
    {
        Shifts.AddSite(_site);
        Shifts.AddSite(_otherSite);
    }

    private InMemoryShifts Shifts => _f.Stores.Shifts;

    private CreateShiftScheduleHandler Create() => new(_f.Stores.UnitOfWork, Shifts, _f.Stores.Events, _f.Events, _f.Ids);

    private UpdateShiftScheduleHandler Update() => new(_f.Stores.UnitOfWork, Shifts, _f.Stores.Events, _f.Events);

    private SetShiftOverrideHandler SetOverride() => new(_f.Stores.UnitOfWork, Shifts, _f.Stores.Events, _f.Events);

    private RemoveShiftOverrideHandler RemoveOverride() => new(_f.Stores.UnitOfWork, Shifts, _f.Stores.Events, _f.Events);

    private SetEquipmentCalendarHandler Set() => new(_f.Stores.UnitOfWork, Shifts, _f.Stores.Events, _f.Events);

    private Task<Contracts.Common.CreatedResponse> CreateAsync(Guid site, string code = "1x8", params ShiftDto[] shifts) =>
        Create().HandleAsync(new CreateShiftScheduleCommand(site, code, "One shift", null, 7, Monday, shifts.Length == 0 ? [Days] : shifts), CancellationToken.None);

    [Fact]
    public async Task A_schedule_is_saved_tidied_with_its_code_in_capitals()
    {
        var created = await CreateAsync(_site);

        var saved = Shifts.Schedule(created.Id)!.Value;
        Assert.Equal(("1X8", true), (saved.Code, saved.IsActive));
        var shift = Assert.Single(saved.Shifts);
        Assert.Equal(("a", "Days"), (shift.Code, shift.Name));
        Assert.Equal([0, 1, 2, 3, 4], shift.Days);
        Assert.Equal(EventTypes.ShiftScheduleCreated, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Bad_schedules_are_refused_with_what_is_wrong()
    {
        await CreateAsync(_site);
        await Assert.ThrowsAsync<ConflictException>(() => CreateAsync(_site));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateAsync(Guid.CreateVersion7(), "X"));

        var overlap = await Assert.ThrowsAsync<ValidationException>(() =>
            CreateAsync(_site, "BAD", Days, Days with { Code = "B", Start = "13:00" }));
        Assert.Contains("can't overlap", overlap.Errors["Shifts"][0], StringComparison.Ordinal);
        await Assert.ThrowsAsync<ValidationException>(() => CreateAsync(_site, "BAD", Days with { Start = "6am" }));
    }

    [Fact]
    public async Task Equipment_inherits_follows_its_own_site_schedule_or_runs_around_the_clock()
    {
        var created = await CreateAsync(_site);
        var line = Shifts.AddEquipment(_site, "EXT-01");

        await Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.Schedule, created.Id), CancellationToken.None);
        Assert.Equal((CalendarModes.Schedule, (Guid?)created.Id), (Shifts.Calendar(line)!.Value.Mode, Shifts.Calendar(line)!.Value.ShiftScheduleId));

        await Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.AroundTheClock, null), CancellationToken.None);
        Assert.Equal(EventTypes.EquipmentCalendarSet, _f.Stores.Events.Appended[^1].Type);

        var elsewhere = await CreateAsync(_otherSite, "OTHER");
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.Schedule, elsewhere.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.Schedule, null), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() =>
            Set().HandleAsync(new SetEquipmentCalendarCommand(line, 1, CalendarModes.Inherit, null), CancellationToken.None));
    }

    [Fact]
    public async Task A_schedule_in_use_cant_be_switched_off_and_a_switched_off_one_cant_be_chosen()
    {
        var created = await CreateAsync(_site);
        var line = Shifts.AddEquipment(_site, "EXT-01");
        await Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.Schedule, created.Id), CancellationToken.None);

        UpdateShiftScheduleCommand Off(long version) => new(created.Id, version, "One shift", null, 7, Monday, [Days], IsActive: false);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() => Update().HandleAsync(Off(Shifts.Schedule(created.Id)!.Version), CancellationToken.None));
        Assert.Contains("EXT-01 follows 1X8", refused.Message, StringComparison.Ordinal);

        await Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.Inherit, null), CancellationToken.None);
        await Update().HandleAsync(Off(Shifts.Schedule(created.Id)!.Version), CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Set().HandleAsync(new SetEquipmentCalendarCommand(line, Shifts.Calendar(line)!.Version, CalendarModes.Schedule, created.Id), CancellationToken.None));
    }

    [Fact]
    public async Task The_preview_lists_shifts_in_site_time_or_the_problems()
    {
        var preview = new PreviewShiftScheduleHandler(Shifts, _f.Time);

        var week = await preview.HandleAsync(new PreviewShiftScheduleQuery(new PreviewShiftScheduleRequest
        {
            SiteId = _site,
            CycleDays = 7,
            CycleStart = Monday,
            Shifts = [Days],
            From = new DateOnly(2026, 10, 19),
            Days = 7,
        }), CancellationToken.None);
        Assert.Equal(5, week.Shifts.Count);
        Assert.Equal((40m, "America/Chicago", 6), (week.HoursPerCycle, week.TimeZone, week.Shifts[0].Start.Hour));

        var bad = await preview.HandleAsync(new PreviewShiftScheduleQuery(new PreviewShiftScheduleRequest
        {
            SiteId = _site,
            CycleDays = 7,
            CycleStart = Monday,
            Shifts = [Days with { Days = [] }],
        }), CancellationToken.None);
        Assert.Empty(bad.Shifts);
        Assert.NotEmpty(bad.Problems);
    }

    [Fact]
    public async Task A_one_off_change_takes_the_cycles_times_when_left_out_and_keeps_the_cycle()
    {
        var created = await CreateAsync(_site);
        var saturday = new DateOnly(2026, 10, 24);

        // An extra Saturday shift A, with A's times from the cycle.
        await SetOverride().HandleAsync(new SetShiftOverrideCommand(
            created.Id, Shifts.Schedule(created.Id)!.Version, saturday, "a", IsWorking: true, null, null, null, null, " Overtime "), CancellationToken.None);

        var saved = Shifts.Schedule(created.Id)!.Value;
        var extra = Assert.Single(saved.Overrides!);
        Assert.Equal(("a", "Days", "06:00", 480, "Overtime"), (extra.Code, extra.Name, extra.Start, extra.Minutes, extra.Note));
        Assert.Equal([0, 1, 2, 3, 4], Assert.Single(saved.Shifts).Days);
        Assert.Equal(EventTypes.ShiftOverrideSet, _f.Stores.Events.Appended[^1].Type);

        // Changing it replaces it; taking A off on a Monday adds a second; changing the schedule keeps both.
        await SetOverride().HandleAsync(new SetShiftOverrideCommand(
            created.Id, Shifts.Schedule(created.Id)!.Version, saturday, "A", IsWorking: true, null, "07:00", 300, null, null), CancellationToken.None);
        await SetOverride().HandleAsync(new SetShiftOverrideCommand(
            created.Id, Shifts.Schedule(created.Id)!.Version, Monday, "A", IsWorking: false, null, null, null, null, "Holiday"), CancellationToken.None);
        await Update().HandleAsync(new UpdateShiftScheduleCommand(
            created.Id, Shifts.Schedule(created.Id)!.Version, "Renamed", null, 7, Monday, [Days], IsActive: true), CancellationToken.None);

        saved = Shifts.Schedule(created.Id)!.Value;
        Assert.Equal([(Monday, false, "06:00"), (saturday, true, "07:00")], saved.Overrides!.Select(o => (o.Date, o.IsWorking, o.Start)));

        await RemoveOverride().HandleAsync(new RemoveShiftOverrideCommand(created.Id, Shifts.Schedule(created.Id)!.Version, saturday, "A"), CancellationToken.None);
        Assert.Equal([Monday], Shifts.Schedule(created.Id)!.Value.Overrides!.Select(o => o.Date));
        Assert.Equal(EventTypes.ShiftOverrideRemoved, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task One_off_changes_are_refused_when_they_cant_apply()
    {
        var created = await CreateAsync(_site);
        long Version() => Shifts.Schedule(created.Id)!.Version;
        SetShiftOverrideCommand Change(DateOnly date, string code, bool working = true, string? start = null, int? minutes = null, string? crew = null) =>
            new(created.Id, Version(), date, code, working, null, start, minutes, crew, null);

        // Saturday has no A to take off; a new code needs its times; it can't run into another shift; the crew must exist.
        await Assert.ThrowsAsync<DomainRuleException>(() => SetOverride().HandleAsync(Change(new DateOnly(2026, 10, 17), "A", working: false), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => SetOverride().HandleAsync(Change(Monday, "OT"), CancellationToken.None));
        await SetOverride().HandleAsync(Change(Monday, "OT", start: "16:00", minutes: 240), CancellationToken.None);
        var clash = await Assert.ThrowsAsync<ValidationException>(() => SetOverride().HandleAsync(Change(Monday, "A", start: "09:00", minutes: 480), CancellationToken.None));
        Assert.Contains("can't overlap", clash.Errors["Shifts"][0], StringComparison.Ordinal);
        await Assert.ThrowsAsync<ValidationException>(() => SetOverride().HandleAsync(Change(Monday, "A", crew: "BLUE"), CancellationToken.None));

        // Approved labor locks the shift that day.
        Shifts.Approved.Add((created.Id, Monday, "OT"));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            RemoveOverride().HandleAsync(new RemoveShiftOverrideCommand(created.Id, Version(), Monday, "OT"), CancellationToken.None));
        await Assert.ThrowsAsync<NotFoundException>(() =>
            RemoveOverride().HandleAsync(new RemoveShiftOverrideCommand(created.Id, Version(), Monday, "A"), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() =>
            RemoveOverride().HandleAsync(new RemoveShiftOverrideCommand(created.Id, 1, Monday, "OT"), CancellationToken.None));
    }
}
