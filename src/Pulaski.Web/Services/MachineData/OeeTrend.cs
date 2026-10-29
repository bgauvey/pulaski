using System.Globalization;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>One point of an OEE trend: a shift or a day of one piece of equipment, its figure and the target of its day (D-073).</summary>
public sealed record OeeTrendPoint(DateOnly Day, string Label, decimal? Value, decimal? Target)
{
    public bool? Below => OeeTargetsView.Below(Value, Target);
}

/// <summary>
/// The OEE trend chart's points (#67), taken from the report's own shift (or day) rows, so the chart shows the figures of the table.
/// A day of shifts adds their hours up first (availability = run ÷ planned and so on), never averaging percentages.
/// </summary>
public static class OeeTrend
{
    public const string Oee = "oee";
    public const string Availability = "availability";
    public const string Performance = "performance";
    public const string Quality = "quality";

    public static IReadOnlyList<(string Measure, string Label)> Measures { get; } =
        [(Oee, "OEE"), (Availability, "Availability"), (Performance, "Performance"), (Quality, "Quality")];

    public static string LabelOf(string measure) => Measures.FirstOrDefault(m => m.Measure == measure).Label ?? "OEE";

    public static IReadOnlyList<OeeTrendPoint> Points(OeeReportDto report, Guid equipmentId, string measure, bool byDay)
    {
        var rows = report.Shifts.Where(s => s.EquipmentId == equipmentId).OrderBy(s => s.Start).ToList();
        if (!byDay || report.ByDay)
        {
            return [.. rows.Select(s => new OeeTrendPoint(s.ProductionDate, ShiftLabel(s, report.ByDay), Value(s.Figures, measure), Target(s.Target, measure)))];
        }

        return [.. rows.GroupBy(s => s.ProductionDate).Select(day => new OeeTrendPoint(
            day.Key, DayLabel(day.Key), Value(Combine(day.Select(s => s.Figures)), measure), Target(day.First().Target, measure)))];
    }

    /// <summary>The figure of the points' time together, from their hours, and how many points fell below their target.</summary>
    public static (decimal? Overall, int Below, int Measured) Summary(OeeReportDto report, Guid equipmentId, string measure, IReadOnlyList<OeeTrendPoint> points) =>
        (Value(Combine(report.Shifts.Where(s => s.EquipmentId == equipmentId).Select(s => s.Figures)), measure),
         points.Count(p => p.Below == true), points.Count(p => p.Below is not null));

    public static decimal? Value(OeeFiguresDto figures, string measure) => measure switch
    {
        Availability => figures.AvailabilityPct,
        Performance => figures.PerformancePct,
        Quality => figures.QualityPct,
        _ => figures.OeePct,
    };

    public static decimal? Target(OeeTargetFiguresDto? target, string measure) => target is null ? null : measure switch
    {
        Availability => target.AvailabilityPct,
        Performance => target.PerformancePct,
        Quality => target.QualityPct,
        _ => target.OeePct,
    };

    /// <summary>Figures of several pieces of working time together, from their unrounded hours.</summary>
    public static OeeFiguresDto Combine(IEnumerable<OeeFiguresDto> figures)
    {
        var list = figures.ToList();
        var planned = list.Sum(f => f.PlannedHoursForOee);
        var run = list.Sum(f => f.RunHoursForAvailability);
        var rated = list.Sum(f => f.RatedRunHoursForPerformance);
        var ideal = list.Sum(f => f.IdealHours);
        var goodIdeal = list.Sum(f => f.GoodIdealHours);
        decimal? Ratio(decimal n, decimal d) => d > 0 ? n / d : null;
        var (a, p, q) = (Ratio(run, planned), Ratio(ideal, rated), Ratio(goodIdeal, ideal));
        decimal? Pct(decimal? r) => r is { } v ? Math.Round(v * 100m, 1) : null;
        return new OeeFiguresDto
        {
            AvailabilityPct = Pct(a),
            PerformancePct = Pct(p),
            QualityPct = Pct(q),
            OeePct = a is { } x && p is { } y && q is { } z ? Pct(x * y * z) : null,
            PlannedHoursForOee = planned,
            RunHoursForAvailability = run,
            RatedRunHoursForPerformance = rated,
            IdealHours = ideal,
            GoodIdealHours = goodIdeal,
        };
    }

    private static string DayLabel(DateOnly day) => day.ToString("ddd d MMM", CultureInfo.InvariantCulture);

    private static string ShiftLabel(OeeShiftRowDto s, bool byDay) =>
        byDay || s.ShiftCode is null ? DayLabel(s.ProductionDate) : $"{DayLabel(s.ProductionDate)} {s.ShiftCode}";
}
