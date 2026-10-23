using Pulaski.Domain.MachineData;
using Pulaski.Domain.Shifts;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Utilization and OEE from machine history (D-052): classifying time, lines from machines, shifts, runs and the figures.</summary>
public class UtilizationTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Jam = Guid.CreateVersion7();
    private static readonly Guid Changeover = Guid.CreateVersion7();
    private static readonly Guid Slow = Guid.CreateVersion7();
    private static readonly IReadOnlySet<int> Production = new HashSet<int> { 1 };

    private static DateTimeOffset M(int minutes) => T0.AddMinutes(minutes);

    private static StatePoint State(int minutes, int state, int mode = 1) => new(M(minutes), state, mode);

    private static StopSpan Stop(int from, int? to, Guid? reason = null, string? useClass = null, bool micro = false) =>
        new(M(from), to is { } t ? M(t) : null, reason, useClass, micro);

    private static IEnumerable<(int From, int To, TimeUse Use)> Shape(IEnumerable<UseSpan> spans) =>
        spans.Select(s => ((int)(s.Start - T0).TotalMinutes, (int)(s.End - T0).TotalMinutes, s.Use));

    [Fact]
    public void Time_is_run_while_executing_and_down_while_stopped_by_its_stops_reason_class()
    {
        var states = new[] { State(-30, 6), State(10, 2), State(20, 6), State(30, 2), State(45, 6), State(50, 5), State(52, 6), State(55, 2), State(58, 6) };
        var stops = new[]
        {
            Stop(10, 20, Jam, UtilizationClasses.Downtime),
            Stop(30, 45, Changeover, UtilizationClasses.Planned),
            Stop(50, 52, micro: true),
            Stop(55, 58, Slow, UtilizationClasses.Runtime),
        };

        var spans = Utilization.Classify(states, stops, Production, T0, M(60));

        Assert.Equal(
            [(0, 10, TimeUse.Run), (10, 20, TimeUse.Down), (20, 30, TimeUse.Run), (30, 45, TimeUse.Planned), (45, 50, TimeUse.Run), (50, 52, TimeUse.Run),
             (52, 55, TimeUse.Run), (55, 58, TimeUse.Run), (58, 60, TimeUse.Run)],
            Shape(spans));
        Assert.Equal((Jam, true), (spans[1].ReasonCodeId, spans[1].StartsStop));
        Assert.True(spans[5].IsMicroStop && spans[5].StartsStop);
        Assert.Equal(Slow, spans[7].ReasonCodeId);
    }

    [Fact]
    public void Before_any_signal_a_machine_had_none_and_a_non_production_mode_is_left_out()
    {
        var states = new[] { State(15, 6), State(30, 6, mode: 2), State(40, 0, mode: 2) };
        var stops = new[] { Stop(40, null) };

        var spans = Utilization.Classify(states, stops, Production, T0, M(60));

        Assert.Equal([(0, 15, TimeUse.Down), (15, 30, TimeUse.Run), (30, 60, TimeUse.NonProduction)], Shape(spans));
        Assert.True(spans[0].NoSignal);
        Assert.Null(spans[0].ReasonCodeId);
    }

    [Fact]
    public void A_stop_that_began_before_the_period_isnt_counted_again()
    {
        var spans = Utilization.Classify([State(-10, 2), State(5, 6)], [Stop(-10, 5, Jam, UtilizationClasses.Downtime)], Production, T0, M(10));

        Assert.Equal([(0, 5, TimeUse.Down), (5, 10, TimeUse.Run)], Shape(spans));
        Assert.False(spans[0].StartsStop);
    }

    [Fact]
    public void A_line_in_series_takes_the_worst_of_its_machines_and_in_parallel_the_best()
    {
        IReadOnlyList<UseSpan> a = [new(T0, M(10), TimeUse.Run, null, false, false, false), new(M(10), M(20), TimeUse.Down, Jam, false, false, true), new(M(20), M(30), TimeUse.Run, null, false, false, false)];
        IReadOnlyList<UseSpan> b = [new(T0, M(15), TimeUse.Run, null, false, false, false), new(M(15), M(25), TimeUse.Planned, Changeover, false, false, true), new(M(25), M(30), TimeUse.Run, null, false, false, false)];

        var series = Utilization.Combine(StateDerivations.Series, [a, b], T0, M(30));
        Assert.Equal([(0, 10, TimeUse.Run), (10, 20, TimeUse.Down), (20, 25, TimeUse.Planned), (25, 30, TimeUse.Run)], Shape(series));
        Assert.Equal(Jam, series[1].ReasonCodeId);
        Assert.Equal([false, true, false, false], series.Select(s => s.StartsStop));

        var parallel = Utilization.Combine(StateDerivations.Parallel, [a, b], T0, M(30));
        Assert.Equal([(0, 15, TimeUse.Run), (15, 20, TimeUse.Planned), (20, 30, TimeUse.Run)], Shape(parallel));

        Assert.Equal([(0, 30, TimeUse.Down)], Shape(Utilization.Combine(StateDerivations.Series, [], T0, M(30))));
    }

    [Fact]
    public void Time_is_cut_to_the_shifts_and_the_runs_in_them()
    {
        IReadOnlyList<UseSpan> spans = [new(T0, M(120), TimeUse.Run, null, false, false, false), new(M(120), M(180), TimeUse.Down, Jam, false, false, true)];
        IReadOnlyList<WorkWindow> windows = [new(M(30), M(90), new DateOnly(2026, 10, 19), "A"), new(M(90), M(150), new DateOnly(2026, 10, 19), "B")];
        var run = Guid.CreateVersion7();

        var slices = Utilization.Slice(spans, windows, [new RunWindow(run, M(60), M(130))], M(180));

        Assert.Equal(
            [(30, 60, 0, (Guid?)null), (60, 90, 0, run), (90, 120, 1, run), (120, 130, 1, run), (130, 150, 1, null)],
            slices.Select(s => ((int)(s.Span.Start - T0).TotalMinutes, (int)(s.Span.End - T0).TotalMinutes, s.Window, s.RunId)));
        Assert.Equal([false, false, false, true, false], slices.Select(s => s.Span.StartsStop));
        Assert.Equal(1, Utilization.WindowOf(windows, M(100)));
        Assert.Equal(1, Utilization.WindowOf(windows, M(170)));    // reported after the shift: its last one
        Assert.Equal(-1, Utilization.WindowOf(windows, M(10)));
    }

    [Fact]
    public void Without_shifts_the_windows_are_whole_days_and_with_a_schedule_its_shifts_up_to_now()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/Chicago");
        var day = new DateOnly(2026, 10, 19);
        var now = new DateTimeOffset(2026, 10, 20, 20, 0, 0, TimeSpan.Zero);

        var days = Utilization.Windows(null, zone, day, day.AddDays(1), now);
        Assert.Equal([(day, new DateTimeOffset(2026, 10, 19, 5, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.Zero)), (day.AddDays(1), new DateTimeOffset(2026, 10, 20, 5, 0, 0, TimeSpan.Zero), now)],
            days.Select(w => (w.ProductionDate, w.Start.ToUniversalTime(), w.End.ToUniversalTime())));

        var schedule = new ShiftSchedule("2X12", "Two twelves", 1, day, [new("D", "Days", new TimeOnly(6, 0), TimeSpan.FromHours(12), [0]), new("N", "Nights", new TimeOnly(18, 0), TimeSpan.FromHours(12), [0], "RED")]);
        var shifts = Utilization.Windows(schedule, zone, day, day, now);
        Assert.Equal([("D", day), ("N", day)], shifts.Select(w => (w.ShiftCode!, w.ProductionDate)));
        Assert.Equal(("Nights", "RED", TimeSpan.FromHours(12)), (shifts[1].ShiftName, shifts[1].Crew, shifts[1].End - shifts[1].Start));
    }

    [Fact]
    public void Oee_is_availability_times_performance_times_quality_weighed_by_the_ideal_rate()
    {
        var run = Guid.CreateVersion7();
        var tally = new OeeTally();
        UseSlice Slice(int from, int to, TimeUse use, Guid? runId, bool starts = false, bool micro = false) =>
            new(new UseSpan(M(from), M(to), use, null, micro, false, starts), 0, runId);

        tally.Add(Slice(0, 60, TimeUse.NonProduction, null), false);
        tally.Add(Slice(60, 120, TimeUse.Planned, run), true);
        tally.Add(Slice(120, 420, TimeUse.Run, run), true);       // 5 h run
        tally.Add(Slice(420, 480, TimeUse.Down, run, starts: true), true);    // 1 h down, uncoded
        tally.Add(Slice(480, 490, TimeUse.Run, null, starts: true, micro: true), false);

        tally.AddCount(400m, 40m, 100m);      // 4 earned hours of 5 run, 10% scrap

        Assert.Equal((TimeSpan.FromMinutes(490), TimeSpan.FromMinutes(370)), (tally.Scheduled, tally.Planned));
        Assert.Equal((1, 1), (tally.Stops, tally.MicroStops));
        Assert.Equal(TimeSpan.FromHours(1), tally.Uncoded);
        Assert.Equal(TimeSpan.FromMinutes(10), tally.RunWithoutJob);
        Assert.Equal(310m / 370m, tally.Availability!.Value, 10);
        Assert.Equal(0.8m, tally.Performance);
        Assert.Equal(0.9m, tally.Quality);
        Assert.Equal(310m / 370m * 0.8m * 0.9m, tally.Oee!.Value, 10);
        Assert.Equal((360m, 40m), (tally.Good, tally.Scrap));

        // Output of a run without an ideal rate counts in its quantities only.
        var unrated = new OeeTally();
        unrated.Add(Slice(0, 60, TimeUse.Run, run), false);
        unrated.AddCount(50m, 0m, null);
        Assert.Equal((null, null, 50m), (unrated.Performance, unrated.Quality, unrated.Good));
        Assert.Equal(1m, unrated.Availability);
    }
}
