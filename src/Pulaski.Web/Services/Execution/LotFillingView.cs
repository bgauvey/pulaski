using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Execution;

/// <summary>What the job page says about lots filled by reports (D-104).</summary>
public static class LotFillingView
{
    /// <summary>True when the job's line fills the material's lots instead of making one per report.</summary>
    public static bool FillsLots(JobExecutionDto job) => job.LotSize is not null || job.LotHours is not null || job.LotByShift;

    /// <summary>"On EXT-01 reports fill a lot until it holds 1000 m, is 8 h old or the shift changes."; null when it doesn't fill lots.</summary>
    public static string? Rule(Text l, JobExecutionDto job)
    {
        if (!FillsLots(job))
        {
            return null;
        }

        var ends = new List<string>();
        if (job.LotSize is { } size)
        {
            ends.Add(l["it holds {0} {1}", FloorView.Qty(size), job.BaseUomCode]);
        }

        if (job.LotHours is { } hours)
        {
            ends.Add(l["it is {0} h old", FloorView.Qty(hours)]);
        }

        if (job.LotByShift)
        {
            ends.Add(l["the shift changes"]);
        }

        var until = ends.Count switch
        {
            1 => ends[0],
            2 => l["{0} or {1}", ends[0], ends[1]],
            _ => l["{0}, {1} or {2}", ends[0], ends[1], ends[2]],
        };
        return l["On {0} reports fill a lot until {1}, then start the next.", job.LineCode, until];
    }

    /// <summary>
    /// What a report with no lot number does on a line that fills lots, at the location chosen: "Goes into lot X (30 of 100 m); a full lot
    /// starts the next."; null when the line doesn't fill lots or no location is chosen yet.
    /// </summary>
    public static string? Hint(Text l, JobExecutionDto job, string? locationCode)
    {
        if (!FillsLots(job) || locationCode is null)
        {
            return null;
        }

        return job.FillingLots.FirstOrDefault(l => string.Equals(l.LocationCode, locationCode, StringComparison.Ordinal)) is { } lot
            ? job.LotSize is null
                ? l["Left blank, it goes into lot {0} ({1}).", lot.LotNumber, Holds(l, job, lot)]
                : l["Left blank, it goes into lot {0} ({1}); what doesn't fit starts the next lot.", lot.LotNumber, Holds(l, job, lot)]
            : l["Left blank, it starts a new lot, which later reports fill."];
    }

    /// <summary>How full a lot is: "700 of 1000 m", or "700 m" when lots have no size.</summary>
    public static string Holds(Text l, JobExecutionDto job, FillingLotDto lot) =>
        job.LotSize is { } size ? l["{0} of {1} {2}", FloorView.Qty(lot.Quantity), FloorView.Qty(size), job.BaseUomCode] : $"{FloorView.Qty(lot.Quantity)} {job.BaseUomCode}";
}
