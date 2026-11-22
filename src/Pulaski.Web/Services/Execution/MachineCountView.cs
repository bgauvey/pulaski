using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Execution;

/// <summary>Words for what a run's machine counter counted (#27): the figures a report can take, and the run reconciled.</summary>
public static class MachineCountView
{
    /// <summary>True when there are counts to take into a report.</summary>
    public static bool CanUse(MachineCountsDto counts) => counts.Unavailable is null && counts.Through is not null && counts.GoodQty + counts.ScrapQty > 0;

    /// <summary>"PACK1 counted 1,200 good and 12 scrap ea since 08:15, to 10:40." or why there is nothing to take.</summary>
    public static string Since(Text l, MachineCountsDto counts, string? timeZone)
    {
        if (counts.Unavailable is { } why)
        {
            return why;
        }

        var from = counts.Since is { } since ? l["since the last report from the machine ({0})", Display.SiteTime(since, timeZone)] : l["since the run started"];
        if (!CanUse(counts))
        {
            return l["{0} hasn't counted anything {1}.", counts.CounterCode, from];
        }

        return l["{0} counted {1} good and {2} scrap {3} {4}, to {5}{6}.", counts.CounterCode, FloorView.Qty(counts.GoodQty), FloorView.Qty(counts.ScrapQty),
            counts.UomCode, from, Display.SiteTime(counts.Through, timeZone), PerCount(l, counts)];
    }

    /// <summary>"Run so far: machine 5,000 good, 60 scrap ea; reported 4,900 good, 58 scrap ea." for reconciling the run.</summary>
    public static string? Run(Text l, MachineCountsDto counts) =>
        counts.Unavailable is not null || counts.RunId is null
            ? null
            : l["Run so far: the machine counted {0} good, {1} scrap {2}; reported {3} good, {4} scrap {5}.",
                FloorView.Qty(counts.RunMachineGoodQty), FloorView.Qty(counts.RunMachineScrapQty), counts.UomCode,
                FloorView.Qty(counts.RunReportedGoodQty), FloorView.Qty(counts.RunReportedScrapQty), counts.ReportedUomCode];

    private static string PerCount(Text l, MachineCountsDto counts) =>
        counts.UnitsPerCount == 1m ? "" : " " + l["({0} {1} a count)", FloorView.Qty(counts.UnitsPerCount), counts.UomCode];
}
