using System.Globalization;
using Pulaski.Contracts.Quality;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Quality;

/// <summary>Words and numbers for control charts and their limits (D-099). Labels are terms the page translates (<c>L.Of</c>); sentences take the text.</summary>
public static class ControlChartView
{
    /// <summary>The charts a characteristic of the kind may use, with their names.</summary>
    public static IReadOnlyList<(string Code, string Name)> ChartsFor(string dataKind) => dataKind == "attribute"
        ? [("p", Term.Mark("p: proportion defective")), ("np", Term.Mark("np: number defective")), ("c", Term.Mark("c: defects a sample")), ("u", Term.Mark("u: defects per unit"))]
        : [("i_mr", Term.Mark("Individuals and moving range")), ("xbar_r", Term.Mark("X-bar and R")), ("xbar_s", Term.Mark("X-bar and S"))];

    /// <summary>
    /// Where a refreshed chart's window ends: one that reached the present when it was loaded keeps up with it (a minute ahead, as it
    /// opens), so new results appear; one ending earlier stays where it was.
    /// </summary>
    public static DateTimeOffset RefreshedTo(DateTimeOffset shownTo, DateTimeOffset loadedAt, DateTimeOffset now) =>
        shownTo >= loadedAt ? now.AddMinutes(1) : shownTo;

    public static string ChartName(string chartType) => chartType switch
    {
        "i_mr" => Term.Mark("Individuals and moving range"),
        "xbar_r" => Term.Mark("X-bar and R"),
        "xbar_s" => Term.Mark("X-bar and S"),
        "p" => Term.Mark("p chart"),
        "np" => Term.Mark("np chart"),
        "c" => Term.Mark("c chart"),
        "u" => Term.Mark("u chart"),
        _ => chartType,
    };

    /// <summary>"X-bar and R, subgroups of 5", "p chart", "np chart, 50 inspected".</summary>
    public static string Chart(Text l, string chartType, int subgroupSize) => chartType switch
    {
        "xbar_r" or "xbar_s" => l["{0}, subgroups of {1}", l[ChartName(chartType)], subgroupSize],
        "np" or "c" => l["{0}, {1} inspected", l[ChartName(chartType)], subgroupSize],
        _ => l[ChartName(chartType)],
    };

    /// <summary>What the subgroup size means for the chart, or null when it is fixed at 1.</summary>
    public static string? SizeLabel(string chartType) => chartType switch
    {
        "xbar_r" or "xbar_s" => Term.Mark("Values a subgroup"),
        "np" or "c" => Term.Mark("Items inspected a sample"),
        _ => null,
    };

    /// <summary>The name of the dispersion chart's statistic, or null for charts without one.</summary>
    public static string? DispersionName(string chartType) => chartType switch
    {
        "i_mr" => Term.Mark("Moving range"),
        "xbar_r" => Term.Mark("Range"),
        "xbar_s" => Term.Mark("Std dev"),
        _ => null,
    };

    /// <summary>A limit with a few more decimals than the characteristic's, so close limits can be told apart.</summary>
    public static string Number(double? value, int decimals) =>
        value is { } v && double.IsFinite(v) ? v.ToString("N" + Math.Clamp(decimals + 2, 0, 8), CultureInfo.CurrentCulture) : "—";

    /// <summary>"Computed from 25 subgroups, 1 left out" or "Entered by hand".</summary>
    public static string How(Text t, ControlLimitDto l) => l.Method == "manual"
        ? t["Entered by hand"]
        : l.LeftOutSampleIds.Count > 0
            ? t["From {0} subgroups, {1} left out", l.BaselineSubgroups, l.LeftOutSampleIds.Count]
            : t["From {0} subgroups", l.BaselineSubgroups];

    /// <summary>"In force", "From 12 Nov", "Ended".</summary>
    public static string State(ControlLimitDto l, DateTimeOffset now) =>
        l.IsInEffect ? Term.Mark("In force") : l.EffectiveFrom > now ? Term.Mark("Starts later") : Term.Mark("Ended");

    /// <summary>A capability index to two decimals, or a dash.</summary>
    public static string Index(double? value) => value is { } v && double.IsFinite(v) ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    /// <summary>The band in words, for a label next to its color.</summary>
    public static string? BandName(string? band) => band switch
    {
        "capable" => Term.Mark("Capable"),
        "marginal" => Term.Mark("Marginal"),
        "not_capable" => Term.Mark("Not capable"),
        _ => null,
    };

    public static string BandChip(string? band) => band switch
    {
        "capable" => "chip chip-ok",
        "marginal" => "chip chip-warn",
        "not_capable" => "chip chip-danger",
        _ => "chip",
    };

    /// <summary>"LSL 3.2 · target 3.3 · USL 3.4, version 2" or what is missing.</summary>
    public static string Specification(Text l, CapabilityDto c, string? uom)
    {
        var parts = new List<string>();
        if (c.Lsl is { } lsl)
        {
            parts.Add($"LSL {lsl.ToString("0.######", CultureInfo.CurrentCulture)}");
        }

        if (c.Target is { } t)
        {
            parts.Add(l["target {0}", t.ToString("0.######", CultureInfo.CurrentCulture)]);
        }

        if (c.Usl is { } u)
        {
            parts.Add($"USL {u.ToString("0.######", CultureInfo.CurrentCulture)}");
        }

        var text = $"{string.Join(" · ", parts)}{(uom is null ? "" : $" {uom}")}";
        return parts.Count == 0 ? "" : c.SpecVersion is { } v ? l["{0}, specification version {1}", text, v] : text;
    }

    /// <summary>"3.00% defective (30,000 ppm): 6 of 200 inspected."</summary>
    public static string Rate(Text l, CapabilityDto c, string chartType)
    {
        if (c.DefectRate is not { } rate)
        {
            return c.NotEnough is { } why ? l.Of(why) : "";
        }

        var pct = (rate * 100).ToString("0.00", CultureInfo.CurrentCulture);
        var ppm = c.Ppm!.Value.ToString("#,0", CultureInfo.CurrentCulture);
        var text = chartType is "c" or "u"
            ? l["{0}% defects a unit ({1} ppm): {2} defects in {3} inspected", pct, ppm, c.Defects, c.Inspected]
            : l["{0}% defective ({1} ppm): {2} defective in {3} inspected", pct, ppm, c.Defects, c.Inspected];
        return c.MaxDefects is { } max ? l["{0}. The specification allows at most {1} a sample.", text, max] : text + ".";
    }

    /// <summary>Where a result came from, as people say it.</summary>
    public static string Source(string source) => source switch
    {
        "manual" => Term.Mark("At the line"),
        "lab" => Term.Mark("Laboratory"),
        "auto" => Term.Mark("From process values"),
        "lot_attribute" => Term.Mark("Lot attribute"),
        _ => source,
    };

    /// <summary>"Every 15 min", "Every 2 h", "Every 1 h 30 min".</summary>
    public static string Interval(Text l, int minutes) => minutes switch
    {
        < 60 => l["Every {0} min", minutes],
        _ when minutes % 60 == 0 => l["Every {0} h", minutes / 60],
        _ => l["Every {0} h {1} min", minutes / 60, minutes % 60],
    };

    /// <summary>Nelson's rules, in his numbering, as people say them.</summary>
    public static IReadOnlyList<(int Number, string Name)> Rules { get; } =
    [
        (1, Term.Mark("Beyond the control limits")),
        (2, Term.Mark("9 in a row on one side of the center line")),
        (3, Term.Mark("6 in a row rising or falling")),
        (4, Term.Mark("14 in a row alternating up and down")),
        (5, Term.Mark("2 of 3 beyond 2 sigma on one side")),
        (6, Term.Mark("4 of 5 beyond 1 sigma on one side")),
        (7, Term.Mark("15 in a row within 1 sigma")),
        (8, Term.Mark("8 in a row beyond 1 sigma, either side")),
    ];

    public static string RuleName(int number) => Rules.FirstOrDefault(r => r.Number == number).Name ?? $"Rule {number}";

    /// <summary>"Rules 2, 5" for a point's label, or empty.</summary>
    public static string RuleList(Text l, IReadOnlyList<int> rules) =>
        rules.Count == 0 ? "" : rules.Count == 1 ? l["Rule {0}", rules[0]] : l["Rules {0}", string.Join(", ", rules)];

    /// <summary>The run rules a point breaks other than rule 1, which the diamond already shows.</summary>
    public static IReadOnlyList<int> RunsBroken(IReadOnlyList<int> rules) => [.. rules.Where(r => r != 1)];

    /// <summary>The points of one of a chart's two plots: the statistic, or (<paramref name="dispersion"/>) its range, standard deviation or moving range.</summary>
    public static IReadOnlyList<PlotPoint> Plot(Text l, ControlChartDto chart, bool dispersion, string? timeZone) =>
        Plot(l, chart, dispersion ? OwnDispersion(chart.ChartType) : "location", timeZone);

    /// <summary>One view's points (#198): <c>location</c> (with its run rules), <c>range</c>, <c>std_dev</c> or <c>moving_range</c>.</summary>
    public static IReadOnlyList<PlotPoint> Plot(Text l, ControlChartDto chart, string view, string? timeZone) =>
        [.. chart.Points.Select(p =>
        {
            var (value, center, ucl, lcl, beyond) = Series(chart, p, view);
            return new PlotPoint(p.At, value, center, ucl, lcl, beyond, Tip(l, chart, p, view, timeZone)) { Rules = view == "location" ? RunsBroken(p.Rules) : [] };
        })];

    /// <summary>The dispersion a chart was set up with: range for X̄-R, standard deviation for X̄-S, moving range for I-MR.</summary>
    public static string OwnDispersion(string chartType) => chartType switch
    {
        "xbar_s" => "std_dev",
        "i_mr" => "moving_range",
        _ => "range",
    };

    /// <summary>A point's value in a view, with the limits in force for it and whether it is beyond them.</summary>
    public static (double? Value, double? Center, double? Ucl, double? Lcl, bool Beyond) Series(ControlChartDto chart, ControlChartPointDto p, string view) => view switch
    {
        "range" when chart.ChartType == "xbar_r" => (p.Dispersion, p.DispersionCenter, p.DispersionUcl, p.DispersionLcl, p.IsDispersionBeyondLimits),
        "std_dev" when chart.ChartType == "xbar_s" => (p.Dispersion, p.DispersionCenter, p.DispersionUcl, p.DispersionLcl, p.IsDispersionBeyondLimits),
        "range" => (p.Range, p.RangeCenter, p.RangeUcl, p.RangeLcl, p.IsRangeBeyondLimits),
        "std_dev" => (p.StdDev, p.StdDevCenter, p.StdDevUcl, p.StdDevLcl, p.IsStdDevBeyondLimits),
        "moving_range" => (p.Dispersion, p.DispersionCenter, p.DispersionUcl, p.DispersionLcl, p.IsDispersionBeyondLimits),
        _ => (p.Value, p.Center, p.Ucl, p.Lcl, p.IsBeyondLimits),
    };

    /// <summary>A view's name, as its tab and its column say.</summary>
    public static string ViewName(Text l, string chartType, string view) => (chartType, view) switch
    {
        ("xbar_r" or "xbar_s", "location") => l["X̄ chart (means)"],
        ("i_mr", "location") => l["Individuals"],
        (_, "range") => l["R chart (ranges)"],
        (_, "std_dev") => l["S chart (standard deviations)"],
        (_, "moving_range") => l["Moving range"],
        _ => l["Chart"],
    };

    /// <summary>What a view's values are, for its column: the mean, the range, the standard deviation, the moving range, or the chart's statistic.</summary>
    public static string? ViewNote(Text l, string chartType, string view) => (chartType, view) switch
    {
        ("xbar_r", "std_dev") => l["This chart is set up as X̄-R: its standard deviation limits come from the same sigma as its range limits."],
        ("xbar_s", "range") => l["This chart is set up as X̄-S: its range limits come from the same sigma as its standard deviation limits."],
        _ => null,
    };

    /// <summary>What hovering a point says: when, the value against its limits, and where it came from.</summary>
    public static string Tip(Text l, ControlChartDto chart, ControlChartPointDto p, bool dispersion, string? timeZone) =>
        Tip(l, chart, p, dispersion ? OwnDispersion(chart.ChartType) : "location", timeZone);

    public static string Tip(Text l, ControlChartDto chart, ControlChartPointDto p, string view, string? timeZone)
    {
        var (value, _, ucl, lcl, beyond) = Series(chart, p, view);
        var limits = ucl is null
            ? l["no limits in force"]
            : beyond
                ? l["limits {0} to {1}, beyond them", Number(lcl, chart.Decimals), Number(ucl, chart.Decimals)]
                : l["limits {0} to {1}", Number(lcl, chart.Decimals), Number(ucl, chart.Decimals)];
        var from = string.Join(" · ", new[] { p.LineCode, p.JobNumber, p.LotLabel }.Where(x => x is not null));
        var runs = view == "location" ? RunsBroken(p.Rules) : [];
        return $"{Display.SiteTime(p.At, timeZone)}: {Number(value, chart.Decimals)} ({limits}){(from.Length > 0 ? $". {from}" : "")}"
            + string.Concat(runs.Select(r => ". " + l["Rule {0}: {1}", r, l[RuleName(r)]]));
    }

    /// <summary>"3 of 40 beyond the limits; 5 with no limits in force."</summary>
    public static string Summary(Text l, ControlChartDto chart)
    {
        var beyond = chart.Points.Count(p => p.IsBeyondLimits || p.IsDispersionBeyondLimits);
        var runs = chart.Points.Count(p => RunsBroken(p.Rules).Count > 0);
        var without = chart.Points.Count(p => p.Ucl is null);
        var text = without == chart.Points.Count
            ? l["{0} results; no control limits in force for them.", chart.Points.Count]
            : runs > 0
                ? l["{0} results; {1} beyond the limits, {2} breaking a run rule.", chart.Points.Count, beyond, runs]
                : l["{0} results; {1} beyond the limits.", chart.Points.Count, beyond];
        return text
            + (without > 0 && without < chart.Points.Count ? " " + l["{0} had no limits in force.", without] : "")
            + (chart.Unusable > 0 ? " " + l["{0} can't go on this chart (their size doesn't fit it).", chart.Unusable] : "")
            + (chart.IsTruncated ? " " + l["Only the latest {0} are shown.", chart.MaxPoints] : "");
    }
}

/// <summary>One point of a plot, with the limits in force for it (null: none) and whether it is beyond them.</summary>
public sealed record PlotPoint(DateTimeOffset At, double? Value, double? Center, double? Ucl, double? Lcl, bool IsBeyond, string Tip)
{
    /// <summary>Run rules other than rule 1 it completes a break of; marked and labeled with their numbers.</summary>
    public IReadOnlyList<int> Rules { get; init; } = [];
}
