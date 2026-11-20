using Pulaski.Contracts.Execution;

namespace Pulaski.Web.Services.Execution;

/// <summary>What an operator station says about who is signed on (#150).</summary>
public static class StationView
{
    /// <summary>
    /// Why the signed-on person can't do everything here, in a sentence; null when they can run jobs, report and code stops, or no one is
    /// signed on.
    /// </summary>
    public static string? Limits(StationDto station)
    {
        if (station.PersonName is null)
        {
            return null;
        }

        if (!station.PersonIsUser)
        {
            return "You have no Pulaski user, so the station can't run jobs or record production for you. Ask a supervisor to link your badge to a user.";
        }

        var cannot = new List<string>();
        if (!station.CanRunJobs)
        {
            cannot.Add("start or stop jobs");
        }

        if (!station.CanReport)
        {
            cannot.Add("report production");
        }

        if (!station.CanCodeStops)
        {
            cannot.Add("code stops");
        }

        return cannot.Count switch
        {
            0 => null,
            3 => "Your role doesn't let you run jobs, report production or code stops at this site.",
            _ => $"Your role doesn't let you {string.Join(" or ", cannot)} at this site.",
        };
    }

    /// <summary>What a station action did, first of its notes: "WO-1-10 is running." (started or resumed), "Paused.", "Completed."</summary>
    public static string Done(string action, StationDto station) => action switch
    {
        "start" => station.RunningJob is { } job ? $"{job.JobNumber} is running." : "Started.",
        "pause" => "Paused.",
        _ => "Completed.",
    };

    /// <summary>A refusal in words: the API's messages for its fields when it gives them ("Reason Other needs a comment."), else its title.</summary>
    public static string Problem(string title, IReadOnlyDictionary<string, string[]> errors) =>
        errors.Count == 0 ? title : string.Join(" ", errors.SelectMany(e => e.Value));

    /// <summary>The machine's count as the one-tap report gives it: "1,200 m good · 12 m scrap".</summary>
    public static string MachineFigures(MachineCountsDto counts)
    {
        var unit = counts.UomCode is { } code ? " " + code : "";
        return counts.ScrapQty > 0
            ? $"{FloorView.Qty(counts.GoodQty)}{unit} good · {FloorView.Qty(counts.ScrapQty)}{unit} scrap"
            : $"{FloorView.Qty(counts.GoodQty)}{unit} good";
    }

    /// <summary>
    /// Where good output goes on a line that fills lots (D-104), for the location shown: "At FG-STAGE it goes into lot WO-1-10-02 (30 of 100 m)."
    /// or "At FG-STAGE it starts a new lot."; null when the line makes a lot per report or no location is chosen.
    /// </summary>
    public static string? LotHint(JobExecutionDto job, string? locationCode)
    {
        if (!LotFillingView.FillsLots(job) || locationCode is null)
        {
            return null;
        }

        return job.FillingLots.FirstOrDefault(l => string.Equals(l.LocationCode, locationCode, StringComparison.Ordinal)) is { } lot
            ? $"At {locationCode} it goes into lot {lot.LotNumber} ({LotFillingView.Holds(job, lot)})."
            : $"At {locationCode} it starts a new lot.";
    }

    public static string StopsTitle(int count) => count == 1 ? "A stop needs its reason" : $"{count} stops need their reasons";

    /// <summary>"stopped 4 min ago, still stopped" for the open stop; "stopped for 12 min, 35 min ago" for an ended one.</summary>
    public static string StopWhen(Pulaski.Contracts.MachineData.DowntimeDto stop, DateTimeOffset now) => stop.EndedAt is { } ended
        ? $"stopped for {LineDisplayView.Duration(stop.StartedAt, ended)}, {LineDisplayView.Duration(ended, now)} ago"
        : $"stopped {LineDisplayView.Duration(stop.StartedAt, now)} ago, still stopped";
}
