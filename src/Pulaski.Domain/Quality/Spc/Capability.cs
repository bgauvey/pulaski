namespace Pulaski.Domain.Quality.Spc;

/// <summary>How capable a process is, by its Cpk (or Ppk): the common convention of 1.33 and 1.00.</summary>
public static class CapabilityBands
{
    public const string Capable = "capable";
    public const string Marginal = "marginal";
    public const string NotCapable = "not_capable";

    public const double CapableAt = 1.33;
    public const double MarginalAt = 1.00;

    public static string? Of(double? index) => index switch
    {
        null => null,
        >= CapableAt => Capable,
        >= MarginalAt => Marginal,
        _ => NotCapable,
    };
}

/// <summary>
/// Process capability of a measured characteristic (D-099) against its specification limits: Cp and Cpk from the within-subgroup sigma (the mean
/// range ÷ d2, mean standard deviation ÷ c4, or mean moving range ÷ d2, as the chart estimates it), Pp and Ppk from the overall standard deviation
/// of every value. A one-sided specification has only its Cpk (and Ppk) side. Null figures, with <see cref="NotEnough"/>, when there aren't enough.
/// </summary>
public sealed record CapabilityResult(
    int Subgroups, double? Mean, double? SigmaWithin, double? SigmaOverall, double? Cp, double? Cpk, double? Pp, double? Ppk, string? NotEnough)
{
    public string? Band => CapabilityBands.Of(Cpk);

    public string? PerformanceBand => CapabilityBands.Of(Ppk);
}

/// <summary>An inspected characteristic's rate (D-099): defectives a unit inspected (p, np) or defects a unit (c, u), over the chart.</summary>
public sealed record DefectRate(int Samples, int Inspected, int Defects, double? Rate)
{
    public double? Ppm => Rate * 1_000_000;
}

/// <summary>Pure capability calculations (D-099).</summary>
public static class ProcessCapability
{
    /// <summary>Capability needs at least this many subgroups, as limits do.</summary>
    public const int MinSubgroups = ControlLimits.MinSubgroups;

    /// <summary>Cp, Cpk, Pp and Ppk of the usable subgroups for the chart, against <paramref name="lsl"/> and/or <paramref name="usl"/>.</summary>
    public static CapabilityResult Measured(string chartType, IReadOnlyList<SpcSubgroup> subgroups, int subgroupSize, double? lsl, double? usl)
    {
        var used = ControlLimits.Compute(chartType, subgroups, subgroupSize);
        var usable = subgroups.Where(s => used.LeftOut.All(l => l.Subgroup.SampleId != s.SampleId)).OrderBy(s => s.At).ToList();
        if (lsl is null && usl is null)
        {
            return new(usable.Count, null, null, null, null, null, null, null, "The specification gives no limits for it.");
        }

        if (ChartTypes.IsAttribute(chartType) || usable.Count < MinSubgroups)
        {
            return new(usable.Count, null, null, null, null, null, null, null,
                ChartTypes.IsAttribute(chartType) ? "Capability indices are for measured values." : $"Only {usable.Count} usable subgroups; capability needs at least {MinSubgroups}.");
        }

        var values = usable.SelectMany(s => s.Values!).ToList();
        var mean = values.Average();
        var overall = values.Count > 1 ? Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)) : (double?)null;
        double? within = chartType switch
        {
            ChartTypes.IMr => usable.Zip(usable.Skip(1), (a, b) => Math.Abs(b.Values![0] - a.Values![0])).Average() / ChartConstants.RangeBias(2),
            ChartTypes.XbarR => usable.Average(s => s.Values!.Max() - s.Values!.Min()) / ChartConstants.RangeBias(subgroupSize),
            ChartTypes.XbarS => usable.Average(s => StdDev(s.Values!)) / ChartConstants.StdDevBias(subgroupSize),
            _ => null,
        };

        return new(usable.Count, mean, within, overall,
            Spread(lsl, usl, within), Centred(mean, lsl, usl, within), Spread(lsl, usl, overall), Centred(mean, lsl, usl, overall), null);
    }

    /// <summary>The rate of an inspected characteristic over its samples.</summary>
    public static DefectRate Counted(IReadOnlyList<SpcSubgroup> subgroups)
    {
        var counted = subgroups.Where(s => s.Inspected is > 0 && s.Defects is >= 0).ToList();
        var inspected = counted.Sum(s => s.Inspected!.Value);
        var defects = counted.Sum(s => s.Defects!.Value);
        return new(counted.Count, inspected, defects, inspected > 0 ? (double)defects / inspected : null);
    }

    private static double? Spread(double? lsl, double? usl, double? sigma) =>
        lsl is { } l && usl is { } u && sigma is > 0 ? (u - l) / (6 * sigma.Value) : null;

    private static double? Centred(double mean, double? lsl, double? usl, double? sigma)
    {
        if (sigma is not > 0)
        {
            return null;
        }

        var sides = new List<double>();
        if (usl is { } u)
        {
            sides.Add((u - mean) / (3 * sigma.Value));
        }

        if (lsl is { } l)
        {
            sides.Add((mean - l) / (3 * sigma.Value));
        }

        return sides.Min();
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}
