using System.Globalization;
using Pulaski.Contracts.Quality;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Quality;

/// <summary>How a certificate's lines read on screen (#137). Pure.</summary>
public static class CertificateView
{
    public static string Outcome(Text l, string outcome) => outcome switch
    {
        "pass" => l["Pass"],
        "fail" => l["Fail"],
        "not_judged" => l["Not judged"],
        _ => l["No result"],
    };

    public static string Chip(string outcome) => outcome switch
    {
        "pass" => "chip chip-live",
        "fail" or "no_result" => "chip chip-danger",
        _ => "chip chip-warning",
    };

    /// <summary>"mean 3.31 (3.28 to 3.34), 2 results" / "1 defect in 50 inspected, 1 result, from its job" / "—".</summary>
    public static string Results(Text l, CertificateLineDto line)
    {
        if (line.Results == 0)
        {
            return "—";
        }

        var count = line.Results == 1 ? l["1 result"] : l["{0} results", line.Results];
        var from = line.Source == "job" ? l[", from its job"] : "";
        if (!line.Measured)
        {
            return l["{0} defects in {1} inspected, {2}{3}", line.Defects ?? 0, line.Inspected ?? 0, count, from];
        }

        string N(decimal? v) => v is { } d ? decimal.Round(d, line.Decimals).ToString(CultureInfo.CurrentCulture) : "—";
        return line.Results == 1 && line.Min == line.Max
            ? $"{N(line.Mean)}{from}"
            : l["mean {0} ({1} to {2}), {3}{4}", N(line.Mean), N(line.Min), N(line.Max), count, from];
    }
}
