using System.Globalization;
using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>Words for planning rates from run history and their proposals (#70).</summary>
public static class RateProposalView
{
    /// <summary>What a calculated rate is based on: "from the ideal rate", "from run history", "from run history, stops taken out".</summary>
    public static string Basis(string? basis) => basis switch
    {
        "run_history" => Term.Mark("from run history"),
        "run_history_net" => Term.Mark("from run history, stops taken out"),
        _ => Term.Mark("from the ideal rate"),
    };

    /// <summary>True for a rate from run history, with or without stops.</summary>
    public static bool IsRunHistory(string? basis) => basis is "run_history" or "run_history_net";

    public static string Status(string status) => status switch
    {
        "pending" => Term.Mark("Waiting for review"),
        "accepted" => Term.Mark("Accepted"),
        "auto_accepted" => Term.Mark("Accepted without review"),
        "rejected" => Term.Mark("Rejected"),
        "superseded" => Term.Mark("Replaced"),
        _ => status,
    };

    /// <summary>"8.5% faster than", "about the same as" the rate it was proposed against.</summary>
    public static string Change(Text l, decimal proposed, decimal from)
    {
        if (from <= 0)
        {
            return l["against {0} then", Qty(from)];
        }

        var pct = (proposed - from) / from * 100m;
        return Math.Abs(pct) < 0.5m ? l["about the same as {0} then", Qty(from)]
            : pct > 0 ? l["{0}% faster than {1} then", Qty(Math.Abs(pct)), Qty(from)] : l["{0}% slower than {1} then", Qty(Math.Abs(pct)), Qty(from)];
    }

    /// <summary>"6 runs, 10.5 h running (1.5 h of stops taken out)" or "6 runs, 12 h".</summary>
    public static string Sample(Text l, RateProposalDto p) =>
        p.CalculationBasis == "run_history_net"
            ? l["{0} runs, {1} h running ({2} h of stops taken out) over {3} days", p.UsedRuns, Qty(p.UsedHours), Qty(p.StoppedHours), p.WindowDays]
            : l["{0} runs, {1} h over {2} days", p.UsedRuns, Qty(p.UsedHours), p.WindowDays];

    public static string Qty(decimal value) => value.ToString("#,0.##", CultureInfo.CurrentCulture);
}
