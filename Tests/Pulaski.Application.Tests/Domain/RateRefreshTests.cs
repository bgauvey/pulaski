using Pulaski.Domain;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Run-history rates without stops, and the scheduled recalculation's rules (#70, D-098).</summary>
public class RateRefreshTests
{
    private static readonly DateTimeOffset To = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset From = To.AddDays(-90);
    private static readonly Guid Site = Guid.CreateVersion7();

    private static RunSample Run(int day, double hours, decimal good, decimal? stopped) =>
        new(Guid.CreateVersion7(), $"J-{day}", 1, To.AddDays(-day), To.AddDays(-day).AddHours(hours), good) { StoppedHours = stopped };

    private static RunStandard Standard(decimal rate, string basis) =>
        RunStandard.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), rate, basis, To.AddDays(-30));

    [Fact]
    public void Taking_out_stops_divides_good_output_by_running_time()
    {
        // Five 4-hour runs making 300 each, stopped an hour each: 75/h on run time, 100/h while running.
        IReadOnlyList<RunSample> runs = [.. Enumerable.Range(1, 5).Select(d => Run(d, 4, 300m, 1m))];

        var gross = RunHistoryRate.Calculate(runs, From, To);
        var net = RunHistoryRate.Calculate(runs, From, To, takeOutStops: true);

        Assert.Equal(75m, gross.RatePerHour);
        Assert.Equal(CalculationBasis.RunHistory, gross.Basis);
        Assert.Equal(100m, net.RatePerHour);
        Assert.Equal(15m, net.UsedHours);
        Assert.Equal(5m, net.UsedStoppedHours);
        Assert.Equal(CalculationBasis.RunHistoryNet, net.Basis);
    }

    [Fact]
    public void A_run_without_known_stops_or_with_too_little_running_is_left_out_when_stops_come_out()
    {
        var result = RunHistoryRate.Calculate([Run(1, 4, 300m, null), Run(2, 4, 300m, 3.75m), Run(3, 4, 300m, 1m)], From, To, takeOutStops: true);

        Assert.StartsWith("No machine state", result.Runs.Single(r => r.Run.JobNumber == "J-1").Excluded, StringComparison.Ordinal);
        Assert.StartsWith("Ran under 30 minutes", result.Runs.Single(r => r.Run.JobNumber == "J-2").Excluded, StringComparison.Ordinal);
        Assert.True(result.Runs.Single(r => r.Run.JobNumber == "J-3").IsUsed);
    }

    [Fact]
    public void Stopped_time_is_everything_but_run_time_inside_the_run()
    {
        var start = To.AddHours(-4);
        IReadOnlyList<UseSpan> uses =
        [
            new(start.AddHours(-1), start.AddHours(1), TimeUse.Down, null, false, true, true),
            new(start.AddHours(1), start.AddHours(2), TimeUse.Run, null, false, false, false),
            new(start.AddHours(2), start.AddHours(2.5), TimeUse.Planned, null, false, false, true),
            new(start.AddHours(2.5), start.AddHours(3), TimeUse.Run, null, true, false, true),
            new(start.AddHours(3), start.AddHours(5), TimeUse.NonProduction, null, false, false, false),
        ];

        // 1 h down inside the run, half an hour planned, an hour not in production; the micro-stop is run time.
        Assert.Equal(2.5m, RunHistoryRate.StoppedHours(uses, start, start.AddHours(4)));
    }

    [Fact]
    public void Changes_beyond_the_threshold_are_proposed_and_small_ones_on_history_rates_accepted_when_the_site_opts_in()
    {
        var settings = RateRefreshSettings.Default(Site) with { IsScheduled = true, ThresholdPct = 5m, AutoAccept = true, AutoAcceptLimitPct = 10m };
        var history = Standard(100m, CalculationBasis.RunHistory);

        Assert.False(RateRefresh.IsWorthProposing(history, 104m, CalculationBasis.RunHistory, 5m));
        Assert.True(RateRefresh.IsWorthProposing(history, 106m, CalculationBasis.RunHistory, 5m));
        Assert.True(RateRefresh.IsWorthProposing(history, 102m, CalculationBasis.RunHistoryNet, 5m));

        Assert.Null(RateRefresh.WhyReviewed(settings, history, 108m));
        Assert.StartsWith("A change of 15%", RateRefresh.WhyReviewed(settings, history, 115m), StringComparison.Ordinal);
        Assert.StartsWith("The planning rate is still the ideal rate", RateRefresh.WhyReviewed(settings, Standard(100m, CalculationBasis.IdealRate), 108m), StringComparison.Ordinal);
        Assert.Equal("The site reviews every proposal.", RateRefresh.WhyReviewed(settings with { AutoAccept = false }, history, 101m));
    }

    [Fact]
    public void Settings_are_in_range_and_due_every_so_many_days()
    {
        var settings = RateRefreshSettings.Default(Site) with { IsScheduled = true, EveryDays = 7 };
        Assert.Throws<DomainRuleException>(() => (settings with { EveryDays = 0 }).Ensure());
        Assert.Throws<DomainRuleException>(() => (settings with { WindowDays = 3 }).Ensure());
        Assert.Throws<DomainRuleException>(() => (settings with { IsScheduled = false, AutoAccept = true }).Ensure());

        Assert.True(RateRefresh.IsDue(settings, null, To));
        Assert.False(RateRefresh.IsDue(settings, To.AddDays(-6), To));
        Assert.True(RateRefresh.IsDue(settings, To.AddDays(-7), To));
        Assert.False(RateRefresh.IsDue(settings with { IsScheduled = false }, null, To));
    }
}
