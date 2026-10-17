using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Remaining components against available stock, in the order jobs are needed (D-029).</summary>
public class ShortageCalculatorTests
{
    private static readonly Guid Resin = Guid.CreateVersion7();
    private static readonly Guid Caps = Guid.CreateVersion7();
    private static readonly DateTimeOffset Monday = new(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

    private static ComponentDemand Need(string job, Guid material, DateTimeOffset? at, decimal remaining) =>
        new(Guid.CreateVersion7(), job, material, at, remaining);

    [Fact]
    public void Earlier_jobs_are_covered_first_and_the_shortfall_lands_on_later_ones()
    {
        var result = Assert.Single(ShortageCalculator.Calculate(
            [
                Need("WO-3", Resin, Monday.AddDays(2), 500m),
                Need("WO-1", Resin, Monday, 600m),
                Need("WO-2", Resin, Monday.AddDays(1), 600m),
            ],
            new Dictionary<Guid, decimal> { [Resin] = 1000m }));

        Assert.Equal((1700m, 700m), (result.Demand, result.Shortfall));
        Assert.Equal(
            [("WO-1", 600m, 0m), ("WO-2", 1200m, 200m), ("WO-3", 1700m, 500m)],
            result.Lines.Select(l => (l.Demand.JobNumber, l.Cumulative, l.Uncovered)));
        Assert.Equal("WO-2", result.FirstShort!.Demand.JobNumber);
    }

    [Fact]
    public void Jobs_with_no_time_come_last_ties_go_by_job_number_and_nothing_left_is_ignored()
    {
        var result = Assert.Single(ShortageCalculator.Calculate(
            [
                Need("WO-9", Caps, null, 10m),
                Need("WO-5", Caps, Monday, 10m),
                Need("WO-4", Caps, Monday, 10m),
                Need("WO-1", Caps, Monday.AddDays(-1), 0m),
            ],
            new Dictionary<Guid, decimal>()));

        Assert.Equal(["WO-4", "WO-5", "WO-9"], result.Lines.Select(l => l.Demand.JobNumber));
        Assert.Equal((0m, 30m), (result.Available, result.Shortfall));
    }

    [Fact]
    public void Enough_stock_means_no_shortfall_and_no_first_short_job()
    {
        var result = Assert.Single(ShortageCalculator.Calculate([Need("WO-1", Resin, Monday, 400m)], new Dictionary<Guid, decimal> { [Resin] = 400m }));

        Assert.Equal(0m, result.Shortfall);
        Assert.Null(result.FirstShort);
        Assert.Empty(ShortageCalculator.Calculate([], new Dictionary<Guid, decimal>()));
    }
}
