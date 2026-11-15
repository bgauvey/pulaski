namespace Pulaski.Domain.Quality.Spc;

/// <summary>Control chart types (D-099). Values match <c>core.quality_characteristic.chart_type</c>.</summary>
public static class ChartTypes
{
    /// <summary>Subgroup means and ranges, for subgroups of 2 to 10.</summary>
    public const string XbarR = "xbar_r";

    /// <summary>Subgroup means and standard deviations, for larger subgroups.</summary>
    public const string XbarS = "xbar_s";

    /// <summary>Individual values and their moving ranges, for one value a sample.</summary>
    public const string IMr = "i_mr";

    /// <summary>Proportion defective, for samples of any size.</summary>
    public const string P = "p";

    /// <summary>Number defective, for samples of one size.</summary>
    public const string Np = "np";

    /// <summary>Defects a sample, for samples of one size.</summary>
    public const string C = "c";

    /// <summary>Defects per unit, for samples of any size.</summary>
    public const string U = "u";

    public static IReadOnlyList<string> All { get; } = [XbarR, XbarS, IMr, P, Np, C, U];

    public static IReadOnlyList<string> ForVariables { get; } = [IMr, XbarR, XbarS];

    public static IReadOnlyList<string> ForAttributes { get; } = [P, Np, C, U];

    /// <summary>True for the charts of counts: p, np, c and u.</summary>
    public static bool IsAttribute(string chartType) => ForAttributes.Contains(chartType);

    /// <summary>True for the charts whose subgroups must all be one size: X-bar charts, np and c.</summary>
    public static bool NeedsOneSize(string chartType) => chartType is XbarR or XbarS or Np or C;

    /// <summary>True for charts drawn with a dispersion chart below (range, standard deviation or moving range).</summary>
    public static bool HasDispersion(string chartType) => chartType is XbarR or XbarS or IMr;

    /// <returns>Why the chart doesn't fit a characteristic of <paramref name="dataKind"/> with subgroups of <paramref name="subgroupSize"/>, or null.</returns>
    public static string? Problem(string chartType, string dataKind, int subgroupSize)
    {
        if (!All.Contains(chartType))
        {
            return $"Unknown chart '{chartType}'.";
        }

        if (IsAttribute(chartType) != (dataKind == CharacteristicKinds.Attribute))
        {
            return dataKind == CharacteristicKinds.Attribute
                ? "An inspected characteristic is charted on a p, np, c or u chart."
                : "A measured characteristic is charted on an individuals, X-bar and R, or X-bar and S chart.";
        }

        return chartType switch
        {
            IMr when subgroupSize != 1 => "An individuals chart takes one value a sample.",
            XbarR when subgroupSize is < 2 or > ChartConstants.MaxSubgroup => $"An X-bar and R chart takes subgroups of 2 to {ChartConstants.MaxSubgroup}.",
            XbarS when subgroupSize is < 2 or > ChartConstants.MaxSubgroup => $"An X-bar and S chart takes subgroups of 2 to {ChartConstants.MaxSubgroup}.",
            _ when subgroupSize < 1 => "The subgroup size is at least 1.",
            _ => null,
        };
    }
}
