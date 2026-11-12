using Pulaski.Domain.Quality;
using Pulaski.Domain.Quality.Spc;

namespace Pulaski.Application.Quality;

/// <summary>A plotted point with the limits in force for it and the run rules it completes.</summary>
internal sealed record EvaluatedPoint(SpcPoint Point, ControlLimit? Limit, double Ucl, double Lcl, IReadOnlyList<int> Rules)
{
    public bool IsBeyond => Limit is not null && (Point.Value > Ucl || Point.Value < Lcl);

    public bool IsDispersionBeyond => Limit?.Limits.DispersionUcl is { } du && Point.Dispersion is { } d && (d > du || d < (Limit.Limits.DispersionLcl ?? 0));
}

/// <summary>
/// How a characteristic's chart reads its results (D-099), shared by the chart and by alerts: each usable result plotted, given the limits in force
/// at its time for its line (only limits for the chart it has now), and checked against its run rules.
/// </summary>
internal static class SpcEvaluation
{
    /// <summary>The most points before a result that any rule looks back over (15 in a row, plus one for a moving range).</summary>
    public const int LookBack = 16;

    public static IReadOnlyList<EvaluatedPoint> Evaluate(Characteristic characteristic, IReadOnlyList<ChartSample> samples, IReadOnlyList<ControlLimit> limits)
    {
        var usable = limits.Where(l => l.ChartType == characteristic.ChartType && l.SubgroupSize == characteristic.SubgroupSize).ToList();
        var lineOf = samples.ToDictionary(s => s.Subgroup.SampleId, s => s.EquipmentId);
        var points = ControlLimits.Points(characteristic.ChartType, samples.Select(s => s.Subgroup));
        var withLimits = points.Select(p =>
        {
            var limit = ControlLimitPeriods.InForce(usable, lineOf[p.SampleId], p.At);
            var (ucl, lcl) = limit is null ? (double.NaN, double.NaN) : ControlLimits.At(characteristic.ChartType, limit.Limits, p.Size);
            return (Point: p, Limit: limit, Ucl: ucl, Lcl: lcl);
        }).ToList();
        var broken = RunRules.Check(
            [.. withLimits.Select(x => x.Limit is null
                ? new RulePoint(x.Point.Value, null, null)
                : new RulePoint(x.Point.Value, x.Limit.Limits.Center, ControlLimits.Sigma(characteristic.ChartType, x.Limit.Limits, x.Point.Size)))],
            characteristic.RunRules);
        return [.. withLimits.Select((x, i) => new EvaluatedPoint(x.Point, x.Limit, x.Ucl, x.Lcl, broken[i]))];
    }
}
