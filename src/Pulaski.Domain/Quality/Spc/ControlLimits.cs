namespace Pulaski.Domain.Quality.Spc;

/// <summary>
/// One subgroup as a chart takes it (D-099): a result's values for a variable, or what was inspected and how many were defective (or, on c and u
/// charts, how many defects were found) for an attribute.
/// </summary>
public sealed record SpcSubgroup(Guid SampleId, DateTimeOffset At, IReadOnlyList<double>? Values, int? Inspected, int? Defects)
{
    /// <summary>How many items it holds: its values, or what was inspected.</summary>
    public int Size => Values?.Count ?? Inspected ?? 0;

    public static SpcSubgroup Measured(Guid sampleId, DateTimeOffset at, params double[] values) => new(sampleId, at, values, null, null);

    public static SpcSubgroup Counted(Guid sampleId, DateTimeOffset at, int inspected, int defects) => new(sampleId, at, null, inspected, defects);
}

/// <summary>A plotted point: the subgroup's statistic, and its dispersion (range, standard deviation or moving range) where the chart has one.</summary>
public sealed record SpcPoint(Guid SampleId, DateTimeOffset At, double Value, double? Dispersion, int Size);

/// <summary>
/// A chart's limits: the centre line and 3-sigma limits of the statistic, and of its dispersion where there is one. For p and u charts, whose
/// limits change with each sample's size, <see cref="Ucl"/> and <see cref="Lcl"/> are those of the baseline's average size; use
/// <see cref="ControlLimits.At"/> for a point's own.
/// </summary>
public sealed record LimitSet(double Center, double Ucl, double Lcl, double? DispersionCenter = null, double? DispersionUcl = null, double? DispersionLcl = null);

/// <summary>Limits worked out from a baseline, with what was left out and why, or why there aren't any.</summary>
public sealed record LimitResult(
    string ChartType,
    LimitSet? Limits,
    int Subgroups,
    int? SubgroupSize,
    IReadOnlyList<(SpcSubgroup Subgroup, string Reason)> LeftOut,
    IReadOnlyList<string> Warnings,
    string? NotEnough);

/// <summary>
/// Shewhart control charts (D-099): each subgroup's plotted statistic, and limits worked out from a baseline of them. Pure. Sigma is estimated
/// from the mean range (d2), mean standard deviation (c4) or mean moving range within the baseline, as the charts are meant to be, not from the
/// spread of all values together.
/// </summary>
public static class ControlLimits
{
    /// <summary>Limits need at least this many subgroups.</summary>
    public const int MinSubgroups = 20;

    /// <summary>Fewer than this gives a warning: limits from few subgroups are loose estimates.</summary>
    public const int RecommendedSubgroups = 25;

    /// <summary>Each subgroup's plotted statistic, in time order; a subgroup the chart can't take is left out.</summary>
    public static IReadOnlyList<SpcPoint> Points(string chartType, IEnumerable<SpcSubgroup> subgroups)
    {
        var points = new List<SpcPoint>();
        double? previous = null;
        foreach (var s in subgroups.OrderBy(s => s.At))
        {
            if (Unusable(chartType, s) is not null)
            {
                continue;
            }

            switch (chartType)
            {
                case ChartTypes.IMr:
                    var x = s.Values![0];
                    points.Add(new(s.SampleId, s.At, x, previous is { } p ? Math.Abs(x - p) : null, 1));
                    previous = x;
                    break;
                case ChartTypes.XbarR:
                    points.Add(new(s.SampleId, s.At, s.Values!.Average(), s.Values!.Max() - s.Values!.Min(), s.Size));
                    break;
                case ChartTypes.XbarS:
                    points.Add(new(s.SampleId, s.At, s.Values!.Average(), StdDev(s.Values!), s.Size));
                    break;
                case ChartTypes.P or ChartTypes.U:
                    points.Add(new(s.SampleId, s.At, (double)s.Defects!.Value / s.Inspected!.Value, null, s.Size));
                    break;
                default:
                    points.Add(new(s.SampleId, s.At, s.Defects!.Value, null, s.Size));
                    break;
            }
        }

        return points;
    }

    /// <summary>
    /// Limits from a baseline. Subgroups the chart can't take, or (for charts of one size) of another size than <paramref name="subgroupSize"/>,
    /// are left out with why; with fewer than <see cref="MinSubgroups"/> left there are no limits.
    /// </summary>
    public static LimitResult Compute(string chartType, IReadOnlyList<SpcSubgroup> baseline, int subgroupSize)
    {
        var leftOut = new List<(SpcSubgroup, string)>();
        var used = new List<SpcSubgroup>();
        foreach (var s in baseline.OrderBy(s => s.At))
        {
            var why = Unusable(chartType, s)
                ?? (ChartTypes.NeedsOneSize(chartType) && s.Size != subgroupSize ? $"Holds {s.Size}, not {subgroupSize}" : null);
            if (why is null)
            {
                used.Add(s);
            }
            else
            {
                leftOut.Add((s, why));
            }
        }

        var warnings = new List<string>();
        if (used.Count < MinSubgroups)
        {
            return new(chartType, null, used.Count, Size(chartType, subgroupSize), leftOut, warnings,
                $"Only {used.Count} usable {(used.Count == 1 ? "subgroup" : "subgroups")}; limits need at least {MinSubgroups}.");
        }

        if (used.Count < RecommendedSubgroups)
        {
            warnings.Add($"{used.Count} subgroups give rough limits; {RecommendedSubgroups} or more are better.");
        }

        var limits = chartType switch
        {
            ChartTypes.IMr => Individuals(used),
            ChartTypes.XbarR => MeansAndRanges(used, subgroupSize),
            ChartTypes.XbarS => MeansAndStdDevs(used, subgroupSize),
            ChartTypes.P => Proportions(used),
            ChartTypes.Np => NumberDefective(used, subgroupSize),
            ChartTypes.C => Defects(used),
            _ => DefectsPerUnit(used),
        };

        if (limits.Center == 0 && ChartTypes.IsAttribute(chartType))
        {
            warnings.Add("No defects at all in the baseline: every limit is zero, so any defect will signal.");
        }

        return new(chartType, limits, used.Count, Size(chartType, subgroupSize), leftOut, warnings, null);
    }

    /// <summary>A point's limits: the set's own, except on p and u charts, where they follow the point's sample size.</summary>
    public static (double Ucl, double Lcl) At(string chartType, LimitSet limits, int size)
    {
        if (chartType is not (ChartTypes.P or ChartTypes.U) || size <= 0)
        {
            return (limits.Ucl, limits.Lcl);
        }

        var spread = chartType == ChartTypes.P
            ? 3 * Math.Sqrt(limits.Center * (1 - limits.Center) / size)
            : 3 * Math.Sqrt(limits.Center / size);
        return (chartType == ChartTypes.P ? Math.Min(1, limits.Center + spread) : limits.Center + spread, Math.Max(0, limits.Center - spread));
    }

    /// <summary>Sigma of the individual values the limits imply (for capability and zone rules): the 3-sigma half-width ÷ 3, per point size.</summary>
    public static double Sigma(string chartType, LimitSet limits, int size)
    {
        var (ucl, _) = At(chartType, limits, size);
        return (ucl - limits.Center) / 3;
    }

    private static int? Size(string chartType, int subgroupSize) => ChartTypes.NeedsOneSize(chartType) || chartType == ChartTypes.IMr ? subgroupSize : null;

    private static string? Unusable(string chartType, SpcSubgroup s)
    {
        if (ChartTypes.IsAttribute(chartType))
        {
            return s.Inspected is not > 0 || s.Defects is not >= 0
                ? "No counts"
                : chartType is ChartTypes.P or ChartTypes.Np && s.Defects > s.Inspected ? "More defective than inspected" : null;
        }

        return s.Values is not { Count: > 0 } values
            ? "No values"
            : chartType == ChartTypes.IMr && values.Count != 1
                ? "More than one value"
                : chartType != ChartTypes.IMr && values.Count < ChartConstants.MinSubgroup ? "Only one value" : null;
    }

    private static LimitSet Individuals(List<SpcSubgroup> used)
    {
        var xs = used.Select(s => s.Values![0]).ToList();
        var center = xs.Average();
        var mrBar = xs.Zip(xs.Skip(1), (a, b) => Math.Abs(b - a)).Average();
        return new(center, center + ChartConstants.E2 * mrBar, center - ChartConstants.E2 * mrBar, mrBar, ChartConstants.MovingRangeD4 * mrBar, 0);
    }

    private static LimitSet MeansAndRanges(List<SpcSubgroup> used, int n)
    {
        var center = used.Average(s => s.Values!.Average());
        var rBar = used.Average(s => s.Values!.Max() - s.Values!.Min());
        return new(center, center + ChartConstants.A2(n) * rBar, center - ChartConstants.A2(n) * rBar, rBar, ChartConstants.D4(n) * rBar, ChartConstants.D3(n) * rBar);
    }

    private static LimitSet MeansAndStdDevs(List<SpcSubgroup> used, int n)
    {
        var center = used.Average(s => s.Values!.Average());
        var sBar = used.Average(s => StdDev(s.Values!));
        return new(center, center + ChartConstants.A3(n) * sBar, center - ChartConstants.A3(n) * sBar, sBar, ChartConstants.B4(n) * sBar, ChartConstants.B3(n) * sBar);
    }

    private static LimitSet Proportions(List<SpcSubgroup> used)
    {
        var pBar = (double)used.Sum(s => s.Defects!.Value) / used.Sum(s => s.Inspected!.Value);
        var nBar = used.Average(s => s.Inspected!.Value);
        var spread = 3 * Math.Sqrt(pBar * (1 - pBar) / nBar);
        return new(pBar, Math.Min(1, pBar + spread), Math.Max(0, pBar - spread));
    }

    private static LimitSet NumberDefective(List<SpcSubgroup> used, int n)
    {
        var pBar = (double)used.Sum(s => s.Defects!.Value) / used.Sum(s => s.Inspected!.Value);
        var center = n * pBar;
        var spread = 3 * Math.Sqrt(center * (1 - pBar));
        return new(center, center + spread, Math.Max(0, center - spread));
    }

    private static LimitSet Defects(List<SpcSubgroup> used)
    {
        var cBar = used.Average(s => s.Defects!.Value);
        var spread = 3 * Math.Sqrt(cBar);
        return new(cBar, cBar + spread, Math.Max(0, cBar - spread));
    }

    private static LimitSet DefectsPerUnit(List<SpcSubgroup> used)
    {
        var uBar = (double)used.Sum(s => s.Defects!.Value) / used.Sum(s => s.Inspected!.Value);
        var spread = 3 * Math.Sqrt(uBar / used.Average(s => s.Inspected!.Value));
        return new(uBar, uBar + spread, Math.Max(0, uBar - spread));
    }

    private static double StdDev(IReadOnlyList<double> values)
    {
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }
}
