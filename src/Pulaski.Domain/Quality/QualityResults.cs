using System.Globalization;

namespace Pulaski.Domain.Quality;

/// <summary>Where a result came from (#65). Values match <c>ts.quality_sample.source</c>.</summary>
public static class QualitySources
{
    /// <summary>Measured or inspected at the line and entered by hand.</summary>
    public const string Manual = "manual";

    /// <summary>From the laboratory.</summary>
    public const string Lab = "lab";

    /// <summary>Sampled automatically from process data (SPC, later).</summary>
    public const string Auto = "auto";

    /// <summary>Recorded by setting a lot attribute that stands for the characteristic.</summary>
    public const string LotAttribute = "lot_attribute";

    /// <summary>What a person may give as the source of a result they record.</summary>
    public static IReadOnlyList<string> Entered { get; } = [Manual, Lab];
}

/// <summary>A sample's measurement: values for a variable, or inspected and defective counts for an attribute.</summary>
public sealed record QualityMeasurement(IReadOnlyList<decimal>? Values, int? Inspected, int? Defects)
{
    public const int MaxValues = 25;

    public static QualityMeasurement Measured(params decimal[] values) => new(values, null, null);

    public static QualityMeasurement Counted(int inspected, int defects) => new(null, inspected, defects);

    /// <summary>How many items the sample holds: its values, or what was inspected.</summary>
    public int SubgroupSize => Values?.Count ?? Inspected ?? 0;

    /// <returns>Why the measurement doesn't fit a characteristic of <paramref name="kind"/>, or null.</returns>
    public string? Problem(string kind)
    {
        if (kind == CharacteristicKinds.Variable)
        {
            return Inspected is not null || Defects is not null
                ? "A measured characteristic takes values, not counts."
                : Values is not { Count: > 0 }
                    ? "Enter at least one value."
                    : Values.Count > MaxValues ? $"At most {MaxValues} values in one sample." : null;
        }

        return Values is { Count: > 0 }
            ? "An inspected characteristic takes counts, not values."
            : Inspected is not > 0
                ? "Enter how many were inspected (at least 1)."
                : Defects is not { } defects || defects < 0 || defects > Inspected
                    ? "Defects must be between 0 and the number inspected."
                    : null;
    }
}

/// <summary>A measurement's statistics, as stored with it: the mean, range and (with at least two values) the sample standard deviation.</summary>
public sealed record SampleStatistics(decimal? Mean, decimal? Range, decimal? StdDev)
{
    public static SampleStatistics Of(QualityMeasurement measurement)
    {
        if (measurement.Values is not { Count: > 0 } values)
        {
            return new(null, null, null);
        }

        var mean = values.Average();
        decimal? stdDev = values.Count < 2
            ? null
            : (decimal)Math.Sqrt((double)(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1)));
        return new(Math.Round(mean, 10), values.Max() - values.Min(), stdDev is { } s ? Math.Round(s, 10) : null);
    }
}

/// <summary>Checks a result against the specification item in force (#65, D-085).</summary>
public static class QualityCheck
{
    /// <summary>
    /// Whether the measurement conforms: every value within the limits (inclusive), or no more defects than allowed. Null when there is no
    /// item to check against, so nothing can be said.
    /// </summary>
    public static bool? Conforms(QualityMeasurement measurement, SpecItem? item)
    {
        if (item is null)
        {
            return null;
        }

        if (measurement.Values is { Count: > 0 } values)
        {
            return values.All(v => (item.Lsl is not { } lsl || v >= lsl) && (item.Usl is not { } usl || v <= usl));
        }

        return measurement.Defects is { } defects && defects <= (item.MaxDefects ?? 0);
    }

    /// <summary>Says why it failed, for warnings and the results list: "2 of 5 values above 3.40", "3 defects, at most 1 allowed".</summary>
    public static string Failure(QualityMeasurement measurement, SpecItem item)
    {
        if (measurement.Values is { Count: > 0 } values)
        {
            var below = item.Lsl is { } lsl ? values.Count(v => v < lsl) : 0;
            var above = item.Usl is { } usl ? values.Count(v => v > usl) : 0;
            var parts = new List<string>();
            if (below > 0)
            {
                parts.Add($"{Count(below, values.Count)} below {Qty(item.Lsl!.Value)}");
            }

            if (above > 0)
            {
                parts.Add($"{Count(above, values.Count)} above {Qty(item.Usl!.Value)}");
            }

            return string.Join(", ", parts);
        }

        return $"{measurement.Defects} {(measurement.Defects == 1 ? "defect" : "defects")}, at most {item.MaxDefects ?? 0} allowed";
    }

    /// <summary>The limits as people read them: "3.20 – 3.40 (target 3.30)", "at most 0.5", "at most 1 defect".</summary>
    public static string Limits(SpecItem item) =>
        item.MaxDefects is { } max
            ? $"at most {max} {(max == 1 ? "defect" : "defects")}"
            : (item.Lsl, item.Usl) switch
            {
                ({ } l, { } u) => $"{Qty(l)} – {Qty(u)}",
                ({ } l, null) => $"at least {Qty(l)}",
                (null, { } u) => $"at most {Qty(u)}",
                _ => "no limits",
            } + (item.Target is { } t ? $" (target {Qty(t)})" : "");

    private static string Count(int n, int of) => of == 1 ? "the value is" : $"{n} of {of} values";

    private static string Qty(decimal value) => value.ToString("0.##########", CultureInfo.InvariantCulture);
}
