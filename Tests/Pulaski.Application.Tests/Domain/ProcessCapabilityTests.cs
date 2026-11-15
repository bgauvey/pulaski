using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Process capability (D-099): Cp/Cpk within subgroups, Pp/Ppk overall, and defect rates.</summary>
public class ProcessCapabilityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    private static List<SpcSubgroup> Measured(int count, Func<int, double[]> values) =>
        [.. Enumerable.Range(0, count).Select(i => SpcSubgroup.Measured(Guid.CreateVersion7(), Start.AddHours(i), values(i)))];

    [Fact]
    public void Individuals_use_the_moving_range_within_and_the_standard_deviation_overall()
    {
        // 10 and 12 alternating: mean 11, MR-bar 2 so sigma within 2/1.128; overall sigma about 1.026.
        var result = ProcessCapability.Measured(ChartTypes.IMr, Measured(20, i => [i % 2 == 0 ? 10 : 12]), 1, 5, 17);
        var within = 2 / 1.128;
        Assert.Equal(11, result.Mean!.Value, 1e-9);
        Assert.Equal(within, result.SigmaWithin!.Value, 1e-9);
        Assert.Equal(12 / (6 * within), result.Cp!.Value, 1e-9);
        Assert.Equal(6 / (3 * within), result.Cpk!.Value, 1e-9);
        Assert.Equal(Math.Sqrt(20.0 / 19), result.SigmaOverall!.Value, 1e-9);
        Assert.True(result.Ppk > result.Cpk);
        Assert.Equal(CapabilityBands.Marginal, result.Band);
    }

    [Fact]
    public void Cpk_takes_the_nearer_limit_and_one_sided_specs_have_no_cp()
    {
        var subgroups = Measured(25, _ => [1, 2, 3, 4, 5]);
        var centred = ProcessCapability.Measured(ChartTypes.XbarR, subgroups, 5, 0, 6);
        var within = 4 / 2.326;
        Assert.Equal(6 / (6 * within), centred.Cp!.Value, 1e-9);
        Assert.Equal(3 / (3 * within), centred.Cpk!.Value, 1e-9);
        Assert.Equal(CapabilityBands.NotCapable, centred.Band);

        var upperOnly = ProcessCapability.Measured(ChartTypes.XbarR, subgroups, 5, null, 8);
        Assert.Null(upperOnly.Cp);
        Assert.Equal(5 / (3 * within), upperOnly.Cpk!.Value, 1e-9);
    }

    [Fact]
    public void Too_little_data_or_no_limits_gives_no_figures_with_why()
    {
        Assert.StartsWith("Only 19 usable", ProcessCapability.Measured(ChartTypes.IMr, Measured(19, i => [i]), 1, 0, 30).NotEnough, StringComparison.Ordinal);
        Assert.Equal("The specification gives no limits for it.", ProcessCapability.Measured(ChartTypes.IMr, Measured(30, i => [i]), 1, null, null).NotEnough);
        Assert.Equal(CapabilityBands.Marginal, CapabilityBands.Of(1.1));
        Assert.Null(CapabilityBands.Of(null));
    }

    [Fact]
    public void Inspected_characteristics_give_their_defective_rate()
    {
        var rate = ProcessCapability.Counted([.. Enumerable.Range(0, 4).Select(i => SpcSubgroup.Counted(Guid.CreateVersion7(), Start.AddHours(i), 50, i))]);
        Assert.Equal((4, 200, 6), (rate.Samples, rate.Inspected, rate.Defects));
        Assert.Equal(0.03, rate.Rate!.Value, 1e-12);
        Assert.Equal(30_000, rate.Ppm!.Value, 1e-6);
    }
}
