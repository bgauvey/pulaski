using System.Globalization;
using Pulaski.Contracts.Quality;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Quality;

/// <summary>How a due sample reads (#136). Pure.</summary>
public static class SampleView
{
    public static string Chip(string status) => status switch
    {
        "overdue" => "chip chip-danger",
        "due" => "chip chip-warning",
        "done" => "chip chip-live",
        _ => "chip",
    };

    public static string Status(Text l, string status) => status switch
    {
        "overdue" => l["Overdue"],
        "due" => l["Due now"],
        "done" => l["Taken"],
        _ => l["Not due yet"],
    };

    /// <summary>"Due since 14:00; 1 missed earlier. Then every 2 h." in the site's time.</summary>
    public static string Detail(Text l, DueSampleDto s)
    {
        var parts = new List<string>();
        if (s.Status is "due" or "overdue" && s.DueAt is { } at)
        {
            parts.Add(l["Due since {0}.", Display.SiteTime(at, s.SiteTimeZone)]);
        }

        if (s.Missed == 1)
        {
            parts.Add(l["1 earlier sample was missed."]);
        }
        else if (s.Missed > 1)
        {
            parts.Add(l["{0} earlier samples were missed.", s.Missed]);
        }

        if (s.NextAt is { } next)
        {
            parts.Add(l["Next at {0}.", Display.SiteTime(next, s.SiteTimeZone)]);
        }
        else if (s.NextAfterUnits is { } units)
        {
            parts.Add(l["Next after {0} {1} more.", units.ToString("0.###", CultureInfo.CurrentCulture), s.UomCode ?? ""]);
        }

        return string.Join(" ", parts);
    }

    /// <summary>The plan in words: "At the start, then every 2 h" or "Every 500 m".</summary>
    public static string Plan(Text l, DueSampleDto s)
    {
        var every = s.EveryHours is { } h ? l["every {0} h", h.ToString("0.##", CultureInfo.CurrentCulture)]
            : s.EveryUnits is { } u ? l["every {0} {1}", u.ToString("0.###", CultureInfo.CurrentCulture), s.UomCode ?? ""]
            : null;
        return (s.AtStart, every) switch
        {
            (true, { } e) => l["At the start of the run, then {0}", e],
            (true, null) => l["At the start of the run"],
            (false, { } e) => Text.Upper(e),
            _ => "",
        };
    }

    /// <summary>For a line on the Floor: "2 samples overdue", "1 sample due"; null when nothing is due.</summary>
    public static (string Text, string Chip)? Flag(Text l, IReadOnlyCollection<DueSampleDto> samples)
    {
        int overdue = samples.Count(s => s.Status == "overdue"), due = samples.Count(s => s.Status == "due");
        return overdue > 0 ? (overdue == 1 ? l["1 sample overdue"] : l["{0} samples overdue", overdue], "chip chip-danger")
            : due > 0 ? (due == 1 ? l["1 sample due"] : l["{0} samples due", due], "chip chip-warning")
            : null;
    }
}
