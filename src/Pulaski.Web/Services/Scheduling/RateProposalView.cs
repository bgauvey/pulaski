using System.Globalization;
using Pulaski.Contracts.Scheduling;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>Words for planning rates from run history and their proposals (#70).</summary>
public static class RateProposalView
{
    /// <summary>What a calculated rate is based on: "from the ideal rate", "from run history", "from run history, stops taken out".</summary>
    public static string Basis(string? basis) => basis switch
    {
        "run_history" => "from run history",
        "run_history_net" => "from run history, stops taken out",
        _ => "from the ideal rate",
    };

    /// <summary>True for a rate from run history, with or without stops.</summary>
    public static bool IsRunHistory(string? basis) => basis is "run_history" or "run_history_net";

    public static string Status(string status) => status switch
    {
        "pending" => "Waiting for review",
        "accepted" => "Accepted",
        "auto_accepted" => "Accepted without review",
        "rejected" => "Rejected",
        "superseded" => "Replaced",
        _ => status,
    };

    /// <summary>"8.5% faster than", "about the same as" the rate it was proposed against.</summary>
    public static string Change(decimal proposed, decimal from)
    {
        if (from <= 0)
        {
            return "against";
        }

        var pct = (proposed - from) / from * 100m;
        return Math.Abs(pct) < 0.5m ? "about the same as" : $"{Qty(Math.Abs(pct))}% {(pct > 0 ? "faster than" : "slower than")}";
    }

    /// <summary>"6 runs, 10.5 h running (1.5 h of stops taken out)" or "6 runs, 12 h".</summary>
    public static string Sample(RateProposalDto p) =>
        p.CalculationBasis == "run_history_net"
            ? $"{p.UsedRuns} runs, {Qty(p.UsedHours)} h running ({Qty(p.StoppedHours)} h of stops taken out) over {p.WindowDays} days"
            : $"{p.UsedRuns} runs, {Qty(p.UsedHours)} h over {p.WindowDays} days";

    public static string Qty(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);
}
