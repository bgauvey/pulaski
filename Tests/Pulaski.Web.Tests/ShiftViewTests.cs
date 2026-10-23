using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Shifts;
using Pulaski.Web.Components.Pages.Equipment;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Shifts;

namespace Pulaski.Web.Tests;

/// <summary>How the Shifts and Equipment screens show shift schedules (D-038).</summary>
public class ShiftViewTests
{
    private static readonly DateOnly Monday = new(2026, 10, 12);

    [Theory]
    [InlineData(0, 7, "Mon")]
    [InlineData(6, 7, "Sun")]
    [InlineData(8, 14, "W2 Tue")]
    [InlineData(2, 4, "Day 3")]
    public void Days_are_named_by_weekday_when_the_cycle_is_whole_weeks(int day, int cycle, string expected) =>
        Assert.Equal(expected, ShiftView.DayLabel(day, cycle, Monday));

    [Fact]
    public void One_off_changes_say_what_they_do_against_the_cycle()
    {
        var schedule = new ShiftScheduleDto
        {
            Id = Guid.CreateVersion7(),
            SiteId = Guid.CreateVersion7(),
            Code = "1X8",
            Name = "Days",
            CycleDays = 7,
            CycleStart = Monday,
            Shifts = [new ShiftDto { Code = "A", Start = "06:00", Minutes = 480, Days = [0, 1, 2, 3, 4] }],
            Overrides =
            [
                new ShiftOverrideDto { Date = Monday.AddDays(5), Code = "A", Start = "06:00", Minutes = 300 },
                new ShiftOverrideDto { Date = Monday.AddDays(7), Code = "A", Start = "08:00", Minutes = 480 },
                new ShiftOverrideDto { Date = Monday, Code = "A", IsWorking = false },
            ],
        };

        Assert.Equal("Extra shift 06:00–11:00", ShiftOverrideView.Describe(schedule.Overrides[0], schedule));
        Assert.Equal("08:00–16:00 instead of 06:00–14:00", ShiftOverrideView.Describe(schedule.Overrides[1], schedule));
        Assert.Equal("Doesn't run", ShiftOverrideView.Describe(schedule.Overrides[2], schedule));
        Assert.Contains("adds it as an extra shift", ShiftOverrideView.Hint(schedule, Monday.AddDays(6), "A"), StringComparison.Ordinal);
        Assert.Contains("replaces it for that day only", ShiftOverrideView.Hint(schedule, Monday.AddDays(-7), "a"), StringComparison.Ordinal);
        Assert.Equal([Monday.AddDays(5), Monday.AddDays(7)], ShiftOverrideView.Upcoming(schedule.Overrides, Monday.AddDays(1), all: false).Select(o => o.Date));
        Assert.Equal(3, ShiftOverrideView.Upcoming(schedule.Overrides, Monday.AddDays(1), all: true).Count);
    }

    [Fact]
    public void Shifts_read_as_their_times_with_the_next_day_marked()
    {
        Assert.Equal("06:00–14:00", ShiftView.Times(new ShiftDto { Start = "06:00", Minutes = 480 }));
        Assert.Equal("22:00–06:00 +1", ShiftView.Times(new ShiftDto { Start = "22:00", Minutes = 480 }));
        Assert.Equal("No shifts", ShiftView.Summary([]));
        Assert.Equal(450, ShiftView.Minutes("7.5"));
        Assert.Null(ShiftView.Minutes("-1"));
    }

    [Fact]
    public void Equipment_shows_the_nearest_working_time_and_where_it_comes_from()
    {
        var site = Guid.CreateVersion7();
        var schedule = new ShiftScheduleDto { Id = Guid.CreateVersion7(), SiteId = site, Code = "2X8", Name = "Two shifts", IsActive = true };
        var plant = Row(site, null, "PLT1", "site") with { SiteId = site, CalendarMode = "schedule", ShiftScheduleId = schedule.Id };
        var area = Row(Guid.CreateVersion7(), site, "EXTR", "area") with { SiteId = site };
        var line = Row(Guid.CreateVersion7(), area.Id, "EXT-01", "work_center") with { SiteId = site };
        var tree = new EquipmentTree([plant, area, line]);

        Assert.Equal("2X8 · Two shifts, from PLT1", EquipmentProperties.InEffect(line, tree, [schedule]));
        Assert.Equal("2X8 · Two shifts", EquipmentProperties.InEffect(plant, tree, [schedule]));
        Assert.Equal("Around the clock", EquipmentProperties.InEffect(line with { CalendarMode = "around_the_clock" }, tree, [schedule]));
        Assert.StartsWith("Around the clock: no shift", EquipmentProperties.InEffect(plant with { CalendarMode = "inherit", ShiftScheduleId = null }, new EquipmentTree([plant with { CalendarMode = "inherit", ShiftScheduleId = null }]), []), StringComparison.Ordinal);

        var item = Assert.Single(EquipmentProperties.ForExisting(line, tree, [schedule], canEdit: false, canSetShifts: true), i => i.Key == EquipmentProperties.CalendarKey);
        Assert.Equal(("inherit", false, 3), (item.Value, item.Locked, item.Choices.Count));
        Assert.All(EquipmentProperties.ForExisting(line, tree, [schedule], canEdit: false, canSetShifts: true).Where(i => i.Key == "name"), i => Assert.True(i.Locked));

        var request = EquipmentProperties.CalendarChange(line, "schedule:" + schedule.Id);
        Assert.Equal(("schedule", (Guid?)schedule.Id), (request.Mode, request.ShiftScheduleId));
        Assert.Equal("around_the_clock", EquipmentProperties.CalendarChange(line, "around_the_clock").Mode);
    }

    private static EquipmentDto Row(Guid id, Guid? parent, string code, string level) => new()
    {
        Id = id,
        ParentId = parent,
        Code = code,
        Name = code,
        Level = level,
        EquipmentRole = level == "work_center" ? "production_line" : level,
        IsActive = true,
    };
}
