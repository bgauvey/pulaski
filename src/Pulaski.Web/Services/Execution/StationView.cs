using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Execution;

/// <summary>What an operator station says about who is signed on (#150).</summary>
public static class StationView
{
    /// <summary>
    /// Why the signed-on person can't do everything here, in a sentence; null when they can run jobs, report and code stops, or no one is
    /// signed on.
    /// </summary>
    public static string? Limits(Text l, StationDto station)
    {
        if (station.PersonName is null)
        {
            return null;
        }

        if (!station.PersonIsUser)
        {
            return l["You have no Pulaski user, so the station can't run jobs or record production for you. Ask a supervisor to link your badge to a user."];
        }

        // Each combination is its own sentence, so every language can word it.
        return (station.CanRunJobs, station.CanReport, station.CanCodeStops) switch
        {
            (true, true, true) => null,
            (false, true, true) => l["Your role doesn't let you start or stop jobs at this site."],
            (true, false, true) => l["Your role doesn't let you report production at this site."],
            (true, true, false) => l["Your role doesn't let you code stops at this site."],
            (false, false, true) => l["Your role doesn't let you start or stop jobs or report production at this site."],
            (false, true, false) => l["Your role doesn't let you start or stop jobs or code stops at this site."],
            (true, false, false) => l["Your role doesn't let you report production or code stops at this site."],
            (false, false, false) => l["Your role doesn't let you run jobs, report production or code stops at this site."],
        };
    }

    /// <summary>What a station action did, first of its notes: "WO-1-10 is running." (started or resumed), "Paused.", "Completed."</summary>
    public static string Done(Text l, string action, StationDto station) => action switch
    {
        "start" => station.RunningJob is { } job ? l["{0} is running.", job.JobNumber] : l["Started."],
        "pause" => l["Paused."],
        _ => l["Completed."],
    };

    /// <summary>A refusal in words: the API's messages for its fields when it gives them ("Reason Other needs a comment."), else its title.</summary>
    public static string Problem(string title, IReadOnlyDictionary<string, string[]> errors) =>
        errors.Count == 0 ? title : string.Join(" ", errors.SelectMany(e => e.Value));

    /// <summary>The machine's count as the one-tap report gives it: "1,200 m good · 12 m scrap".</summary>
    public static string MachineFigures(Text l, MachineCountsDto counts)
    {
        var unit = counts.UomCode is { } code ? " " + code : "";
        return counts.ScrapQty > 0
            ? l["{0} good · {1} scrap", FloorView.Qty(counts.GoodQty) + unit, FloorView.Qty(counts.ScrapQty) + unit]
            : l["{0} good", FloorView.Qty(counts.GoodQty) + unit];
    }

    /// <summary>
    /// Where good output goes on a line that fills lots (D-104), for the location shown: "At FG-STAGE it goes into lot WO-1-10-02 (30 of 100 m)."
    /// or "At FG-STAGE it starts a new lot."; null when the line makes a lot per report or no location is chosen.
    /// </summary>
    public static string? LotHint(Text l, JobExecutionDto job, string? locationCode)
    {
        if (!LotFillingView.FillsLots(job) || locationCode is null)
        {
            return null;
        }

        return job.FillingLots.FirstOrDefault(l => string.Equals(l.LocationCode, locationCode, StringComparison.Ordinal)) is { } lot
            ? l["At {0} it goes into lot {1} ({2}).", locationCode, lot.LotNumber, LotFillingView.Holds(l, job, lot)]
            : l["At {0} it starts a new lot.", locationCode];
    }

    public static string StopsTitle(Text l, int count) => count == 1 ? l["A stop needs its reason"] : l["Stops needing their reasons: {0}", count];

    /// <summary>"stopped 4 min ago, still stopped" for the open stop; "stopped for 12 min, 35 min ago" for an ended one.</summary>
    public static string StopWhen(Text l, Pulaski.Contracts.MachineData.DowntimeDto stop, DateTimeOffset now) => stop.EndedAt is { } ended
        ? l["stopped for {0}, {1} ago", LineDisplayView.Duration(stop.StartedAt, ended), LineDisplayView.Duration(ended, now)]
        : l["stopped {0} ago, still stopped", LineDisplayView.Duration(stop.StartedAt, now)];
}
