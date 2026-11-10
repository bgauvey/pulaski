using Pulaski.Contracts.Execution;

namespace Pulaski.Web.Services.Execution;

/// <summary>Words for what a run's machine counter counted (#27): the figures a report can take, and the run reconciled.</summary>
public static class MachineCountView
{
    /// <summary>True when there are counts to take into a report.</summary>
    public static bool CanUse(MachineCountsDto counts) => counts.Unavailable is null && counts.Through is not null && counts.GoodQty + counts.ScrapQty > 0;

    /// <summary>"PACK1 counted 1,200 good and 12 scrap ea since 08:15, to 10:40." or why there is nothing to take.</summary>
    public static string Since(MachineCountsDto counts, string? timeZone)
    {
        if (counts.Unavailable is { } why)
        {
            return why;
        }

        var from = counts.Since is { } since ? $"since the last report from the machine ({Display.SiteTime(since, timeZone)})" : "since the run started";
        if (!CanUse(counts))
        {
            return $"{counts.CounterCode} hasn't counted anything {from}.";
        }

        return $"{counts.CounterCode} counted {FloorView.Qty(counts.GoodQty)} good and {FloorView.Qty(counts.ScrapQty)} scrap {counts.UomCode} {from}, "
            + $"to {Display.SiteTime(counts.Through, timeZone)}{PerCount(counts)}.";
    }

    /// <summary>"Run so far: machine 5,000 good, 60 scrap ea; reported 4,900 good, 58 scrap ea." for reconciling the run.</summary>
    public static string? Run(MachineCountsDto counts) =>
        counts.Unavailable is not null || counts.RunId is null
            ? null
            : $"Run so far: the machine counted {FloorView.Qty(counts.RunMachineGoodQty)} good, {FloorView.Qty(counts.RunMachineScrapQty)} scrap {counts.UomCode}; "
              + $"reported {FloorView.Qty(counts.RunReportedGoodQty)} good, {FloorView.Qty(counts.RunReportedScrapQty)} scrap {counts.ReportedUomCode}.";

    private static string PerCount(MachineCountsDto counts) =>
        counts.UnitsPerCount == 1m ? "" : $" ({FloorView.Qty(counts.UnitsPerCount)} {counts.UomCode} a count)";
}
