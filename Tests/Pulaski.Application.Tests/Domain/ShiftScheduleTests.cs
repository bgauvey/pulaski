using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Shift schedules worked out into shifts (D-038).</summary>
public class ShiftScheduleTests
{
    private static readonly TimeZoneInfo Chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");

    // A Monday-to-Friday, two-shift week starting on Monday 2026-10-12.
    private static readonly ShiftSchedule TwoShifts = new("2X8", "Two shifts", 7, new DateOnly(2026, 10, 12),
    [
        new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4]),
        new("B", "Lates", new TimeOnly(14, 0), TimeSpan.FromHours(8), [0, 1, 2, 3, 4]),
    ]);

    private static DateTimeOffset Local(int month, int day, int hour) =>
        new(new DateTime(2026, month, day, hour, 0, 0), Chicago.GetUtcOffset(new DateTime(2026, month, day, hour, 0, 0)));

    [Fact]
    public void A_week_gives_each_weekday_its_shifts_in_site_time()
    {
        var shifts = TwoShifts.Expand(Chicago, Local(10, 19, 0), Local(10, 26, 0));

        Assert.Equal(10, shifts.Count);
        Assert.Equal(("A", new DateOnly(2026, 10, 19), Local(10, 19, 6), Local(10, 19, 14)), (shifts[0].Code, shifts[0].ProductionDate, shifts[0].Start, shifts[0].End));
        Assert.DoesNotContain(shifts, s => s.ProductionDate.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(TimeSpan.FromHours(80), TwoShifts.HoursPerCycle);
    }

    [Fact]
    public void A_night_shift_counts_towards_the_day_it_starts_and_is_found_while_running()
    {
        var nights = new ShiftSchedule("N", "Nights", 7, new DateOnly(2026, 10, 12), [new("C", "Nights", new TimeOnly(22, 0), TimeSpan.FromHours(8), [0])]);

        var running = Assert.Single(nights.Expand(Chicago, Local(10, 13, 2), Local(10, 13, 3)));

        Assert.Equal((new DateOnly(2026, 10, 12), Local(10, 12, 22), Local(10, 13, 6)), (running.ProductionDate, running.Start, running.End));
    }

    [Fact]
    public void The_cycle_also_counts_back_before_its_first_day()
    {
        var rotation = new ShiftSchedule("ROT", "Rotation", 4, new DateOnly(2026, 10, 12), []);

        Assert.Equal(0, rotation.CycleDay(new DateOnly(2026, 10, 12)));
        Assert.Equal(3, rotation.CycleDay(new DateOnly(2026, 10, 11)));
        Assert.Equal(1, rotation.CycleDay(new DateOnly(2026, 10, 21)));
    }

    [Fact]
    public void Shifts_keep_clock_time_across_a_daylight_saving_change()
    {
        var daily = new ShiftSchedule("D", "Daily", 1, new DateOnly(2026, 1, 1),
            [new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0]), new("C", "Nights", new TimeOnly(2, 0), TimeSpan.FromHours(2), [0])]);

        // Clocks go back on 1 November 2026 in Chicago and forward on 8 March 2026.
        var before = daily.Expand(Chicago, Local(10, 31, 0), Local(11, 1, 0)).Single(s => s.Code == "A");
        var after = daily.Expand(Chicago, Local(11, 2, 0), Local(11, 3, 0)).Single(s => s.Code == "A");
        Assert.Equal((6, 6), (TimeZoneInfo.ConvertTime(before.Start, Chicago).Hour, TimeZoneInfo.ConvertTime(after.Start, Chicago).Hour));

        // 02:00 doesn't exist on 8 March: that shift starts when the clocks have gone forward.
        var spring = daily.Expand(Chicago, Local(3, 8, 0), Local(3, 8, 12)).Single(s => s.Code == "C");
        Assert.Equal(3, TimeZoneInfo.ConvertTime(spring.Start, Chicago).Hour);
    }

    [Fact]
    public void Overlapping_shifts_and_bad_shifts_are_problems()
    {
        var overlapping = TwoShifts with { Shifts = [.. TwoShifts.Shifts, new("X", "Extra", new TimeOnly(13, 0), TimeSpan.FromHours(2), [2])] };
        Assert.Contains("can't overlap", Assert.Single(overlapping.Problems()), StringComparison.Ordinal);

        // A night shift on the last day of the cycle runs into the next cycle's first morning.
        var wrapping = new ShiftSchedule("W", "Wrap", 2, new DateOnly(2026, 10, 12),
            [new("A", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0]), new("C", "Nights", new TimeOnly(22, 0), TimeSpan.FromHours(9), [1])]);
        Assert.Single(wrapping.Problems());

        var bad = new ShiftSchedule("B", "Bad", 7, new DateOnly(2026, 10, 12),
            [new("A", "A", new TimeOnly(6, 0), TimeSpan.FromMinutes(5), []), new("a", "A again", new TimeOnly(6, 0), TimeSpan.FromHours(8), [9])]);
        var problems = bad.Problems();
        Assert.Contains(problems, p => p.Contains("Two shifts", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("15 minutes", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("no day", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("outside", StringComparison.Ordinal));
        Assert.Empty(TwoShifts.Problems());
        Assert.NotEmpty((TwoShifts with { CycleDays = 60 }).Problems());
    }

    [Fact]
    public void An_override_changes_adds_or_takes_off_a_shift_on_one_date_only()
    {
        // Monday 19 Oct: A starts at 08:00 for 6 hours. Saturday 24 Oct: an extra A. Tuesday 20 Oct: no B.
        var changed = TwoShifts with
        {
            Overrides =
            [
                new(new DateOnly(2026, 10, 19), "A", IsWorking: true, new TimeOnly(8, 0), TimeSpan.FromHours(6), "Late days"),
                new(new DateOnly(2026, 10, 24), "A", IsWorking: true, new TimeOnly(6, 0), TimeSpan.FromHours(8), "Days", Crew: "RED"),
                new(new DateOnly(2026, 10, 20), "B", IsWorking: false, TimeOnly.MinValue, TimeSpan.Zero, "B"),
            ],
        };

        var week = changed.Expand(Chicago, Local(10, 19, 0), Local(10, 26, 0));

        var monday = week.Where(s => s.ProductionDate == new DateOnly(2026, 10, 19)).ToList();
        Assert.Equal([("A", 8, true), ("B", 14, false)], monday.Select(s => (s.Code, TimeZoneInfo.ConvertTime(s.Start, Chicago).Hour, s.IsOverride)));
        Assert.Equal(Local(10, 19, 14), monday[0].End);
        Assert.Equal(["A"], week.Where(s => s.ProductionDate == new DateOnly(2026, 10, 20)).Select(s => s.Code));
        var saturday = Assert.Single(week, s => s.ProductionDate == new DateOnly(2026, 10, 24));
        Assert.Equal(("RED", true), (saturday.Crew, saturday.IsOverride));
        Assert.Equal(10, week.Count);

        // The cycle is untouched: the next Monday is as usual.
        var next = changed.Expand(Chicago, Local(10, 26, 0), Local(10, 27, 0));
        Assert.Equal(6, TimeZoneInfo.ConvertTime(next[0].Start, Chicago).Hour);
        Assert.Empty(changed.Problems());
    }

    [Fact]
    public void An_override_that_overlaps_a_neighbouring_shift_or_repeats_is_a_problem()
    {
        var late = new ShiftOverride(new DateOnly(2026, 10, 19), "A", IsWorking: true, new TimeOnly(9, 0), TimeSpan.FromHours(8), "Days");
        var clash = Assert.Single((TwoShifts with { Overrides = [late] }).Problems());
        Assert.Contains("Mon 19 Oct 2026", clash, StringComparison.Ordinal);
        Assert.Contains("can't overlap", clash, StringComparison.Ordinal);

        Assert.Contains((TwoShifts with { Overrides = [late, late with { Start = new TimeOnly(5, 0) }] }).Problems(),
            p => p.Contains("two overrides", StringComparison.Ordinal));
        Assert.NotEmpty((TwoShifts with { Overrides = [late with { Start = new TimeOnly(4, 0), Length = TimeSpan.FromMinutes(5) }] }).Problems());

        var replaced = TwoShifts.WithOverride(late.ProductionDate, "a", late with { Start = new TimeOnly(5, 0) }).WithOverride(late.ProductionDate, "A", null);
        Assert.Empty(replaced.Overrides!);
    }
}
