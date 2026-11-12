using System.Globalization;
using Pulaski.Contracts.Quality;

namespace Pulaski.Web.Services.Quality;

/// <summary>Words and numbers for control charts and their limits (D-099).</summary>
public static class ControlChartView
{
    /// <summary>The charts a characteristic of the kind may use, with their names.</summary>
    public static IReadOnlyList<(string Code, string Name)> ChartsFor(string dataKind) => dataKind == "attribute"
        ? [("p", "p: proportion defective"), ("np", "np: number defective"), ("c", "c: defects a sample"), ("u", "u: defects per unit")]
        : [("i_mr", "Individuals and moving range"), ("xbar_r", "X-bar and R"), ("xbar_s", "X-bar and S")];

    public static string ChartName(string chartType) => chartType switch
    {
        "i_mr" => "Individuals and moving range",
        "xbar_r" => "X-bar and R",
        "xbar_s" => "X-bar and S",
        "p" => "p chart",
        "np" => "np chart",
        "c" => "c chart",
        "u" => "u chart",
        _ => chartType,
    };

    /// <summary>"X-bar and R, subgroups of 5", "p chart", "np chart, 50 inspected".</summary>
    public static string Chart(string chartType, int subgroupSize) => chartType switch
    {
        "xbar_r" or "xbar_s" => $"{ChartName(chartType)}, subgroups of {subgroupSize}",
        "np" or "c" => $"{ChartName(chartType)}, {subgroupSize} inspected",
        _ => ChartName(chartType),
    };

    /// <summary>What the subgroup size means for the chart, or null when it is fixed at 1.</summary>
    public static string? SizeLabel(string chartType) => chartType switch
    {
        "xbar_r" or "xbar_s" => "Values a subgroup",
        "np" or "c" => "Items inspected a sample",
        _ => null,
    };

    /// <summary>The name of the dispersion chart's statistic, or null for charts without one.</summary>
    public static string? DispersionName(string chartType) => chartType switch
    {
        "i_mr" => "Moving range",
        "xbar_r" => "Range",
        "xbar_s" => "Std dev",
        _ => null,
    };

    /// <summary>A limit with a few more decimals than the characteristic's, so close limits can be told apart.</summary>
    public static string Number(double? value, int decimals) =>
        value is { } v && double.IsFinite(v) ? v.ToString("N" + Math.Clamp(decimals + 2, 0, 8), CultureInfo.InvariantCulture) : "—";

    /// <summary>"Computed from 25 subgroups, 1 left out" or "Entered by hand".</summary>
    public static string How(ControlLimitDto l) => l.Method == "manual"
        ? "Entered by hand"
        : $"From {l.BaselineSubgroups} subgroups{(l.LeftOutSampleIds.Count > 0 ? $", {l.LeftOutSampleIds.Count} left out" : "")}";

    /// <summary>"In force", "From 12 Nov", "Ended".</summary>
    public static string State(ControlLimitDto l, DateTimeOffset now) =>
        l.IsInEffect ? "In force" : l.EffectiveFrom > now ? "Starts later" : "Ended";

    /// <summary>Nelson's rules, in his numbering, as people say them.</summary>
    public static IReadOnlyList<(int Number, string Name)> Rules { get; } =
    [
        (1, "Beyond the control limits"),
        (2, "9 in a row on one side of the centre line"),
        (3, "6 in a row rising or falling"),
        (4, "14 in a row alternating up and down"),
        (5, "2 of 3 beyond 2 sigma on one side"),
        (6, "4 of 5 beyond 1 sigma on one side"),
        (7, "15 in a row within 1 sigma"),
        (8, "8 in a row beyond 1 sigma, either side"),
    ];

    public static string RuleName(int number) => Rules.FirstOrDefault(r => r.Number == number).Name ?? $"Rule {number}";

    /// <summary>"Rules 2, 5" for a point's label, or empty.</summary>
    public static string RuleList(IReadOnlyList<int> rules) =>
        rules.Count == 0 ? "" : $"{(rules.Count == 1 ? "Rule" : "Rules")} {string.Join(", ", rules)}";

    /// <summary>The run rules a point breaks other than rule 1, which the diamond already shows.</summary>
    public static IReadOnlyList<int> RunsBroken(IReadOnlyList<int> rules) => [.. rules.Where(r => r != 1)];

    /// <summary>The points of one of a chart's two plots: the statistic, or (<paramref name="dispersion"/>) its range, standard deviation or moving range.</summary>
    public static IReadOnlyList<PlotPoint> Plot(ControlChartDto chart, bool dispersion, string? timeZone) =>
        [.. chart.Points.Select(p => dispersion
            ? new PlotPoint(p.At, p.Dispersion, p.DispersionCenter, p.DispersionUcl, p.DispersionLcl, p.IsDispersionBeyondLimits, Tip(chart, p, true, timeZone))
            : new PlotPoint(p.At, p.Value, p.Center, p.Ucl, p.Lcl, p.IsBeyondLimits, Tip(chart, p, false, timeZone)) { Rules = RunsBroken(p.Rules) })];

    /// <summary>What hovering a point says: when, the value against its limits, and where it came from.</summary>
    public static string Tip(ControlChartDto chart, ControlChartPointDto p, bool dispersion, string? timeZone)
    {
        var value = dispersion ? p.Dispersion : p.Value;
        var (lcl, ucl, beyond) = dispersion ? (p.DispersionLcl, p.DispersionUcl, p.IsDispersionBeyondLimits) : (p.Lcl, p.Ucl, p.IsBeyondLimits);
        var limits = ucl is null ? "no limits in force" : $"limits {Number(lcl, chart.Decimals)} to {Number(ucl, chart.Decimals)}{(beyond ? ", beyond them" : "")}";
        var from = string.Join(" · ", new[] { p.LineCode, p.JobNumber, p.LotLabel }.Where(x => x is not null));
        var runs = dispersion ? [] : RunsBroken(p.Rules);
        return $"{Display.SiteTime(p.At, timeZone)}: {Number(value, chart.Decimals)} ({limits}){(from.Length > 0 ? $". {from}" : "")}"
            + string.Concat(runs.Select(r => $". Rule {r}: {RuleName(r)}"));
    }

    /// <summary>"3 of 40 beyond the limits; 5 with no limits in force."</summary>
    public static string Summary(ControlChartDto chart)
    {
        var beyond = chart.Points.Count(p => p.IsBeyondLimits || p.IsDispersionBeyondLimits);
        var runs = chart.Points.Count(p => RunsBroken(p.Rules).Count > 0);
        var without = chart.Points.Count(p => p.Ucl is null);
        var text = without == chart.Points.Count
            ? $"{chart.Points.Count} results; no control limits in force for them."
            : $"{chart.Points.Count} results; {beyond} beyond the limits{(runs > 0 ? $", {runs} breaking a run rule" : "")}.";
        return text
            + (without > 0 && without < chart.Points.Count ? $" {without} had no limits in force." : "")
            + (chart.Unusable > 0 ? $" {chart.Unusable} can't go on this chart (their size doesn't fit it)." : "")
            + (chart.IsTruncated ? $" Only the latest {chart.MaxPoints} are shown." : "");
    }
}

/// <summary>One point of a plot, with the limits in force for it (null: none) and whether it is beyond them.</summary>
public sealed record PlotPoint(DateTimeOffset At, double? Value, double? Center, double? Ucl, double? Lcl, bool IsBeyond, string Tip)
{
    /// <summary>Run rules other than rule 1 it completes a break of; marked and labelled with their numbers.</summary>
    public IReadOnlyList<int> Rules { get; init; } = [];
}
