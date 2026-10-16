using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

public class WorkCalendarTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 12, 0, 0, 0, TimeSpan.Zero);

    private static TimeWindow W(double fromHours, double toHours) =>
        new(Day.AddHours(fromHours), Day.AddHours(toHours));

    [Fact]
    public void Always_open_places_work_back_to_back()
    {
        var placed = WorkCalendar.AlwaysOpen.Place(Day, TimeSpan.FromHours(30));

        Assert.Equal(new CalendarPlacement(Day, Day.AddHours(30), false), placed);
    }

    [Fact]
    public void Around_the_clock_still_honours_exceptions()
    {
        var calendar = WorkCalendar.AroundTheClock([W(10, 12)]);

        var placed = calendar.Place(Day.AddHours(9), TimeSpan.FromHours(3));

        Assert.False(calendar.HasShiftCalendar);
        Assert.Equal(new CalendarPlacement(Day.AddHours(9), Day.AddHours(14), false), placed);
    }

    [Fact]
    public void Work_inside_one_window_ends_in_it()
    {
        var calendar = WorkCalendar.Create([W(6, 14)], []);

        var placed = calendar.Place(Day.AddHours(7), TimeSpan.FromHours(2));

        Assert.Equal(new CalendarPlacement(Day.AddHours(7), Day.AddHours(9), false), placed);
    }

    [Fact]
    public void Work_starting_in_a_gap_waits_for_the_next_window()
    {
        var calendar = WorkCalendar.Create([W(6, 14)], []);

        var placed = calendar.Place(Day.AddHours(2), TimeSpan.FromHours(1));

        Assert.Equal(Day.AddHours(6), placed.Start);
        Assert.Equal(Day.AddHours(7), placed.End);
    }

    [Fact]
    public void Work_spills_across_gaps_into_later_windows()
    {
        // Two 8 h day shifts; 10 h of work from 10:00 = 4 h today + 6 h tomorrow.
        var calendar = WorkCalendar.Create([W(6, 14), W(30, 38)], []);

        var placed = calendar.Place(Day.AddHours(10), TimeSpan.FromHours(10));

        Assert.Equal(Day.AddHours(10), placed.Start);
        Assert.Equal(Day.AddHours(36), placed.End);
        Assert.False(placed.BeyondCalendar);
    }

    [Fact]
    public void Work_that_exactly_fills_a_window_ends_at_its_close()
    {
        var calendar = WorkCalendar.Create([W(6, 14), W(30, 38)], []);

        Assert.Equal(Day.AddHours(14), calendar.Place(Day.AddHours(6), TimeSpan.FromHours(8)).End);
    }

    [Fact]
    public void Zero_work_lands_at_the_next_working_instant()
    {
        var calendar = WorkCalendar.Create([W(6, 14)], []);

        var placed = calendar.Place(Day.AddHours(15), TimeSpan.Zero);

        Assert.True(placed.BeyondCalendar);
        Assert.Equal(Day.AddHours(15), placed.Start);
        Assert.Equal(calendar.Place(Day.AddHours(1), TimeSpan.Zero), new CalendarPlacement(Day.AddHours(6), Day.AddHours(6), false));
    }

    [Fact]
    public void Touching_and_overlapping_shifts_merge()
    {
        var calendar = WorkCalendar.Create([W(14, 22), W(6, 14), W(20, 30)], []);

        Assert.Equal([W(6, 30)], calendar.Windows);
    }

    [Fact]
    public void Exceptions_cut_holes_in_windows()
    {
        // A day shift with a 1 h planned stop and a holiday that wipes the second shift.
        var calendar = WorkCalendar.Create([W(6, 14), W(30, 38)], [W(9, 10), W(24, 48)]);

        Assert.Equal([W(6, 9), W(10, 14)], calendar.Windows);
    }

    [Fact]
    public void Exception_covering_window_edges_trims_both_ends()
    {
        var calendar = WorkCalendar.Create([W(6, 14), W(22, 30)], [W(12, 23)]);

        Assert.Equal([W(6, 12), W(23, 30)], calendar.Windows);
    }

    [Fact]
    public void Work_past_the_last_window_runs_around_the_clock_and_is_flagged()
    {
        var calendar = WorkCalendar.Create([W(6, 14)], []);

        var placed = calendar.Place(Day.AddHours(12), TimeSpan.FromHours(5));

        Assert.Equal(Day.AddHours(12), placed.Start);
        Assert.Equal(Day.AddHours(17), placed.End);  // 2 h in the shift, 3 h after it
        Assert.True(placed.BeyondCalendar);
    }

    [Fact]
    public void Daylight_saving_change_needs_no_special_case_because_windows_are_instants()
    {
        // US fall-back night, 2026-11-01: a 22:00–06:00 Chicago shift is 9 real hours.
        var chicago = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var start = new DateTimeOffset(2026, 10, 31, 22, 0, 0, chicago.GetUtcOffset(new DateTime(2026, 10, 31, 22, 0, 0)));
        var end = new DateTimeOffset(2026, 11, 1, 6, 0, 0, chicago.GetUtcOffset(new DateTime(2026, 11, 1, 6, 0, 0)));
        var calendar = WorkCalendar.Create([new TimeWindow(start, end)], []);

        var placed = calendar.Place(start, TimeSpan.FromHours(9));

        Assert.Equal(end, placed.End);
        Assert.False(placed.BeyondCalendar);
    }

    [Fact]
    public void Negative_work_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkCalendar.AlwaysOpen.Place(Day, TimeSpan.FromMinutes(-1)));
}
