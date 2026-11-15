using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Shewhart control chart statistics and limits (D-099).</summary>
public class ControlLimitsTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 6, 0, 0, TimeSpan.Zero);

    private static List<SpcSubgroup> Measured(int count, Func<int, double[]> values) =>
        [.. Enumerable.Range(0, count).Select(i => SpcSubgroup.Measured(Guid.CreateVersion7(), Start.AddHours(i), values(i)))];

    private static List<SpcSubgroup> Counted(int count, int inspected, int defects) =>
        [.. Enumerable.Range(0, count).Select(i => SpcSubgroup.Counted(Guid.CreateVersion7(), Start.AddHours(i), inspected, defects))];

    public static TheoryData<int> Sizes => [.. Enumerable.Range(ChartConstants.MinSubgroup, ChartConstants.MaxSubgroup - 1)];

    [Theory]
    [MemberData(nameof(Sizes))]
    public void The_factor_tables_agree_with_their_definitions(int n)
    {
        var d2 = ChartConstants.RangeBias(n);
        var c4 = ChartConstants.StdDevBias(n);
        Assert.Equal(3 / (d2 * Math.Sqrt(n)), ChartConstants.A2(n), 0.002);
        Assert.Equal(3 / (c4 * Math.Sqrt(n)), ChartConstants.A3(n), 0.002);
        Assert.Equal(1 + 3 * Math.Sqrt(1 - c4 * c4) / c4, ChartConstants.B4(n), 0.002);
        Assert.Equal(Math.Max(0, 1 - 3 * Math.Sqrt(1 - c4 * c4) / c4), ChartConstants.B3(n), 0.002);
        Assert.Equal(Math.Max(0, 2 - ChartConstants.D4(n)), ChartConstants.D3(n), 0.002);
        Assert.Throws<ArgumentOutOfRangeException>(() => ChartConstants.A2(26));
    }

    [Fact]
    public void Individuals_limits_come_from_the_mean_moving_range()
    {
        var result = ControlLimits.Compute(ChartTypes.IMr, Measured(20, i => [i % 2 == 0 ? 10 : 12]), 1);

        var l = result.Limits!;
        Assert.Equal(11, l.Center, 1e-9);
        Assert.Equal(16.32, l.Ucl, 1e-9);
        Assert.Equal(5.68, l.Lcl, 1e-9);
        Assert.Equal(2, l.DispersionCenter!.Value, 1e-9);
        Assert.Equal(6.534, l.DispersionUcl!.Value, 1e-9);
        Assert.Equal(0, l.DispersionLcl);

        var points = ControlLimits.Points(ChartTypes.IMr, Measured(3, i => [10 + i]));
        Assert.Equal([null, 1d, 1d], points.Select(p => p.Dispersion));
    }

    [Fact]
    public void Xbar_limits_come_from_the_mean_range_or_standard_deviation()
    {
        var subgroups = Measured(20, _ => [1, 2, 3, 4, 5]);

        var r = ControlLimits.Compute(ChartTypes.XbarR, subgroups, 5).Limits!;
        Assert.Equal(3, r.Center, 1e-9);
        Assert.Equal(5.308, r.Ucl, 1e-9);
        Assert.Equal(0.692, r.Lcl, 1e-9);
        Assert.Equal(8.456, r.DispersionUcl!.Value, 1e-9);
        Assert.Equal(0, r.DispersionLcl!.Value, 1e-9);

        var s = ControlLimits.Compute(ChartTypes.XbarS, subgroups, 5).Limits!;
        Assert.Equal(Math.Sqrt(2.5), s.DispersionCenter!.Value, 1e-9);
        Assert.Equal(3 + 1.427 * Math.Sqrt(2.5), s.Ucl, 1e-9);
        Assert.Equal(2.089 * Math.Sqrt(2.5), s.DispersionUcl!.Value, 1e-9);
    }

    [Fact]
    public void Attribute_limits_follow_the_binomial_and_poisson_and_p_limits_follow_each_sample_size()
    {
        var p = ControlLimits.Compute(ChartTypes.P, Counted(20, 100, 5), 1).Limits!;
        Assert.Equal(0.05, p.Center, 1e-9);
        Assert.Equal(0.05 + 3 * Math.Sqrt(0.0475 / 100), p.Ucl, 1e-9);
        Assert.Equal(0, p.Lcl);
        var (ucl, lcl) = ControlLimits.At(ChartTypes.P, p, 400);
        Assert.Equal(0.05 + 3 * Math.Sqrt(0.0475 / 400), ucl, 1e-9);
        Assert.Equal(0.05 - 3 * Math.Sqrt(0.0475 / 400), lcl, 1e-9);

        var np = ControlLimits.Compute(ChartTypes.Np, Counted(20, 100, 5), 100).Limits!;
        Assert.Equal(5 + 3 * Math.Sqrt(5 * 0.95), np.Ucl, 1e-9);

        var c = ControlLimits.Compute(ChartTypes.C, Counted(20, 1, 4), 1).Limits!;
        Assert.Equal((4d, 10d, 0d), (c.Center, c.Ucl, c.Lcl));

        var u = ControlLimits.Compute(ChartTypes.U, Counted(20, 10, 20), 1).Limits!;
        Assert.Equal(2, u.Center, 1e-9);
        Assert.Equal(2 + 3 * Math.Sqrt(0.2), u.Ucl, 1e-9);
        Assert.Equal(0.2, ControlLimits.Points(ChartTypes.P, Counted(1, 10, 2))[0].Value, 1e-9);
    }

    [Fact]
    public void Too_few_subgroups_give_no_limits_and_unusable_ones_are_left_out_with_why()
    {
        var thin = ControlLimits.Compute(ChartTypes.IMr, Measured(19, i => [i]), 1);
        Assert.Null(thin.Limits);
        Assert.Equal("Only 19 usable subgroups; limits need at least 20.", thin.NotEnough);

        var mixed = Measured(22, _ => [1, 2, 3, 4, 5]);
        mixed.Add(SpcSubgroup.Measured(Guid.CreateVersion7(), Start.AddDays(2), 1, 2, 3));
        var result = ControlLimits.Compute(ChartTypes.XbarR, mixed, 5);
        Assert.Equal(22, result.Subgroups);
        Assert.Equal("Holds 3, not 5", Assert.Single(result.LeftOut).Reason);
        Assert.StartsWith("22 subgroups give rough limits", Assert.Single(result.Warnings), StringComparison.Ordinal);

        var clean = ControlLimits.Compute(ChartTypes.C, Counted(25, 1, 0), 1);
        Assert.StartsWith("No defects at all", Assert.Single(clean.Warnings), StringComparison.Ordinal);
    }

    [Fact]
    public void A_chart_fits_its_characteristic()
    {
        Assert.Null(ChartTypes.Problem(ChartTypes.XbarR, CharacteristicKinds.Variable, 5));
        Assert.Null(ChartTypes.Problem(ChartTypes.P, CharacteristicKinds.Attribute, 1));
        Assert.NotNull(ChartTypes.Problem(ChartTypes.P, CharacteristicKinds.Variable, 1));
        Assert.NotNull(ChartTypes.Problem(ChartTypes.IMr, CharacteristicKinds.Variable, 3));
        Assert.NotNull(ChartTypes.Problem(ChartTypes.XbarR, CharacteristicKinds.Variable, 1));
        Assert.NotNull(ChartTypes.Problem("pareto", CharacteristicKinds.Variable, 1));
    }
}
