namespace Pulaski.Domain.Quality.Spc;

/// <summary>
/// The views of a control chart (#198): a subgroup chart (X̄-R or X̄-S) shows its means, ranges or standard deviations; an I-MR chart its
/// individuals or moving ranges; an attribute chart only itself. The first is the default.
/// </summary>
public static class ChartViews
{
    public const string Location = "location";
    public const string Range = "range";
    public const string StdDev = "std_dev";
    public const string MovingRange = "moving_range";

    public static IReadOnlyList<string> For(string chartType) => chartType switch
    {
        ChartTypes.XbarR or ChartTypes.XbarS => [Location, Range, StdDev],
        ChartTypes.IMr => [Location, MovingRange],
        _ => [Location],
    };
}

/// <summary>A centre line and its control limits.</summary>
public sealed record LimitLine(double Center, double Ucl, double Lcl);

/// <summary>
/// Range and standard-deviation limits for a subgroup chart whichever it was set up with (#198): one comes as stored; the other from the same
/// estimate of sigma within subgroups (σ = R̄ ÷ d₂ or S̄ ÷ c₄), so both describe the same process: R̄ = d₂σ with D₃/D₄, S̄ = c₄σ with B₃/B₄.
/// None for other charts or subgroups outside 2–25. Pure.
/// </summary>
public static class DispersionLimits
{
    public static (LimitLine? Range, LimitLine? StdDev) For(string chartType, LimitSet limits, int n)
    {
        if (chartType is not (ChartTypes.XbarR or ChartTypes.XbarS) || n is < ChartConstants.MinSubgroup or > ChartConstants.MaxSubgroup
            || limits.DispersionCenter is not { } center)
        {
            return (null, null);
        }

        var sigma = chartType == ChartTypes.XbarR ? center / ChartConstants.RangeBias(n) : center / ChartConstants.StdDevBias(n);
        var rBar = ChartConstants.RangeBias(n) * sigma;
        var sBar = ChartConstants.StdDevBias(n) * sigma;
        var range = chartType == ChartTypes.XbarR && limits.DispersionUcl is { } ru && limits.DispersionLcl is { } rl
            ? new LimitLine(center, ru, rl)
            : new LimitLine(rBar, ChartConstants.D4(n) * rBar, ChartConstants.D3(n) * rBar);
        var stdDev = chartType == ChartTypes.XbarS && limits.DispersionUcl is { } su && limits.DispersionLcl is { } sl
            ? new LimitLine(center, su, sl)
            : new LimitLine(sBar, ChartConstants.B4(n) * sBar, ChartConstants.B3(n) * sBar);
        return (range, stdDev);
    }
}
