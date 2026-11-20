using Pulaski.Contracts.Execution;

namespace Pulaski.Web.Services.Execution;

/// <summary>What the job page says about lots filled by reports (D-104).</summary>
public static class LotFillingView
{
    /// <summary>True when the job's line fills the material's lots instead of making one per report.</summary>
    public static bool FillsLots(JobExecutionDto job) => job.LotSize is not null || job.LotHours is not null || job.LotByShift;

    /// <summary>"On EXT-01 reports fill a lot until it holds 1000 m, is 8 h old or the shift changes."; null when it doesn't fill lots.</summary>
    public static string? Rule(JobExecutionDto job)
    {
        if (!FillsLots(job))
        {
            return null;
        }

        var ends = new List<string>();
        if (job.LotSize is { } size)
        {
            ends.Add($"it holds {FloorView.Qty(size)} {job.BaseUomCode}");
        }

        if (job.LotHours is { } hours)
        {
            ends.Add($"it is {FloorView.Qty(hours)} h old");
        }

        if (job.LotByShift)
        {
            ends.Add("the shift changes");
        }

        var until = ends.Count == 1 ? ends[0] : string.Join(", ", ends.Take(ends.Count - 1)) + " or " + ends[^1];
        return $"On {job.LineCode} reports fill a lot until {until}, then start the next.";
    }

    /// <summary>
    /// What a report with no lot number does on a line that fills lots, at the location chosen: "Goes into lot X (30 of 100 m); a full lot
    /// starts the next."; null when the line doesn't fill lots or no location is chosen yet.
    /// </summary>
    public static string? Hint(JobExecutionDto job, string? locationCode)
    {
        if (!FillsLots(job) || locationCode is null)
        {
            return null;
        }

        return job.FillingLots.FirstOrDefault(l => string.Equals(l.LocationCode, locationCode, StringComparison.Ordinal)) is { } lot
            ? $"Left blank, it goes into lot {lot.LotNumber} ({Holds(job, lot)}){(job.LotSize is null ? "" : "; what doesn't fit starts the next lot")}."
            : "Left blank, it starts a new lot, which later reports fill.";
    }

    /// <summary>How full a lot is: "700 of 1000 m", or "700 m" when lots have no size.</summary>
    public static string Holds(JobExecutionDto job, FillingLotDto lot) =>
        job.LotSize is { } size ? $"{FloorView.Qty(lot.Quantity)} of {FloorView.Qty(size)} {job.BaseUomCode}" : $"{FloorView.Qty(lot.Quantity)} {job.BaseUomCode}";
}
