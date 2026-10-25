using Pulaski.Application.Overview;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Overview;

/// <summary>The Overview's figures from a line's history (D-070). Now is Monday 2026-10-12 09:25 UTC.</summary>
public class OverviewFiguresTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 9, 25, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Six = new(2026, 10, 12, 6, 0, 0, TimeSpan.Zero);

    private static readonly ShiftSchedule TwoTwelves = new("2X12", "Two twelves", 1, new DateOnly(2026, 10, 1),
        [new("D", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(12), [0]), new("N", "Nights", new TimeOnly(18, 0), TimeSpan.FromHours(12), [0], "RED")]);

    private static UseSpan Use(int fromMinutes, int toMinutes, TimeUse use, Guid? reason = null, bool noSignal = false, bool startsStop = false) =>
        new(Six.AddMinutes(fromMinutes), Six.AddMinutes(toMinutes), use, reason, false, noSignal, startsStop);

    [Fact]
    public void A_line_on_a_shift_covers_it_and_one_without_covers_the_site_day()
    {
        var onShift = OverviewFigures.Scope(TwoTwelves, TimeZoneInfo.Utc, Now);
        Assert.Equal((Six, "D"), (onShift.From, onShift.Shift?.Code));

        var noShifts = OverviewFigures.Scope(null, TimeZoneInfo.Utc, Now);
        Assert.Equal((new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero), (ShiftOccurrence?)null), (noShifts.From, noShifts.Shift));
    }

    [Fact]
    public void The_window_spans_the_current_shifts_else_rolls_around_now()
    {
        var days = OverviewFigures.Scope(TwoTwelves, TimeZoneInfo.Utc, Now).Shift!;
        var night = new ShiftOccurrence("N", "Nights", new DateOnly(2026, 10, 11), Six.AddHours(-12), Six.AddHours(4));

        Assert.Equal((Six.AddHours(-12), Six.AddHours(12), true), OverviewFigures.Window([days, night], Now));
        Assert.Equal((Now.AddMinutes(-25).AddHours(-6), Now.AddMinutes(-25).AddHours(18), false), OverviewFigures.Window([], Now));
    }

    [Fact]
    public void Down_time_is_cut_to_the_shift_and_grouped_by_reason_most_first()
    {
        var jam = Guid.CreateVersion7();
        var uses = new[]
        {
            Use(-60, 10, TimeUse.Down, noSignal: true),     // 10 minutes of it in the shift
            Use(10, 40, TimeUse.Run),
            Use(40, 45, TimeUse.Down, startsStop: true),     // not coded
            Use(45, 60, TimeUse.Down, jam, startsStop: true),
            Use(60, 70, TimeUse.Planned, startsStop: true),  // planned: not down
            Use(70, 80, TimeUse.Down, jam, startsStop: true),
        };

        var down = OverviewFigures.Down(uses, Six);

        Assert.Equal([(jam, false, 25d), (null, true, 10d), (null, false, 5d)], down.Select(d => (d.ReasonCodeId, d.NoSignal, d.Length.TotalMinutes)));
    }

    [Fact]
    public void Uncoded_stops_are_those_without_a_reason_in_the_span_each_counted_once()
    {
        var filler = Guid.CreateVersion7();
        var stops = new (Guid, StopSpan)[]
        {
            (filler, new StopSpan(Six.AddMinutes(-20), Six.AddMinutes(5), null, null, false)),     // runs into the shift
            (filler, new StopSpan(Six.AddMinutes(30), null, null, null, false)),                   // open
            (filler, new StopSpan(Six.AddMinutes(30), null, null, null, false)),                   // the same, seen through two lines
            (filler, new StopSpan(Six.AddMinutes(50), Six.AddMinutes(51), null, null, true)),      // micro-stop
            (filler, new StopSpan(Six.AddMinutes(60), Six.AddMinutes(70), Guid.CreateVersion7(), "downtime", false)),
            (filler, new StopSpan(Six.AddMinutes(-90), Six.AddMinutes(-60), null, null, false)),   // before the shift
        };

        Assert.Equal(2, OverviewFigures.Uncoded(stops, Six, Now));
    }

    [Fact]
    public void The_lane_joins_neighbours_of_one_use_and_starts_at_the_window()
    {
        var uses = new[]
        {
            Use(-90, -30, TimeUse.Down, noSignal: true),
            Use(-30, 10, TimeUse.Run),
            new UseSpan(Six.AddMinutes(10), Six.AddMinutes(11), TimeUse.Run, null, true, false, true),   // micro-stop: still running
            Use(11, 20, TimeUse.Run),
            Use(20, 30, TimeUse.Down, Guid.CreateVersion7()),
            Use(30, 40, TimeUse.NonProduction),
        };

        var lane = OverviewFigures.Lane(uses, Six.AddMinutes(-60));

        Assert.Equal(
            [("no_signal", -60, -30), ("run", -30, 20), ("down", 20, 30), ("non_production", 30, 40)],
            lane.Select(s => (s.Use, (int)(s.Start - Six).TotalMinutes, (int)(s.End - Six).TotalMinutes)));
    }
}
