using System.Globalization;

namespace Pulaski.Domain.Scheduling;

/// <summary>
/// How a site keeps its planning rates from run history up to date (#70, D-098): whether stops are taken out by default, whether a scheduled
/// recalculation proposes rates, how often and over how many days, how big a change is worth proposing, and whether small changes to rates
/// already from run history are accepted without review.
/// </summary>
public sealed record RateRefreshSettings(
    Guid SiteId, bool TakeOutStops, bool IsScheduled, int EveryDays, int WindowDays, decimal ThresholdPct, bool AutoAccept, decimal AutoAcceptLimitPct)
{
    public const int MaxEveryDays = 90;

    public static RateRefreshSettings Default(Guid siteId) => new(siteId, false, false, 7, RunHistoryRate.DefaultDays, 5m, false, 10m);

    /// <exception cref="DomainRuleException">A value is out of range, or proposals would be accepted without being made.</exception>
    public RateRefreshSettings Ensure()
    {
        if (EveryDays is < 1 or > MaxEveryDays)
        {
            throw new DomainRuleException($"Recalculate every 1 to {MaxEveryDays} days.");
        }

        if (WindowDays is < RunHistoryRate.MinDays or > RunHistoryRate.MaxDays)
        {
            throw new DomainRuleException($"Look back {RunHistoryRate.MinDays} to {RunHistoryRate.MaxDays} days.");
        }

        if (ThresholdPct is < 0 or > 100)
        {
            throw new DomainRuleException("Propose a change of 0 to 100 percent or more.");
        }

        if (AutoAcceptLimitPct is <= 0 or > 100)
        {
            throw new DomainRuleException("Accept changes of more than 0 and at most 100 percent without review.");
        }

        if (AutoAccept && !IsScheduled)
        {
            throw new DomainRuleException("Only a scheduled recalculation's proposals can be accepted without review; switch it on first.");
        }

        return this;
    }
}

/// <summary>Values match <c>core.run_standard_proposal.status</c>.</summary>
public static class ProposalStatus
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string AutoAccepted = "auto_accepted";
    public const string Rejected = "rejected";
    public const string Superseded = "superseded";
}

/// <summary>The rules of the scheduled recalculation (#70, D-098). Pure.</summary>
public static class RateRefresh
{
    /// <summary>How far <paramref name="to"/> is from <paramref name="from"/>, in percent of it.</summary>
    public static decimal ChangePct(decimal from, decimal to) => from <= 0 ? 100m : Math.Round(Math.Abs(to - from) / from * 100m, 2);

    /// <summary>A rate is proposed when it moves the calculated rate by more than the site's threshold, or would change its basis.</summary>
    public static bool IsWorthProposing(RunStandard standard, decimal rate, string basis, decimal thresholdPct) =>
        ChangePct(standard.CalculatedRatePerHour, rate) > thresholdPct || (standard.CalculationBasis != basis && rate != standard.CalculatedRatePerHour);

    /// <summary>
    /// Why a proposal waits for review; null when the site accepts it without: the site opted in, the rate is already from run history (a rate
    /// still on the ideal rate is reviewed once), and the change is within the site's limit. A modified rate stays in force either way.
    /// </summary>
    public static string? WhyReviewed(RateRefreshSettings settings, RunStandard standard, decimal rate)
    {
        if (!settings.AutoAccept)
        {
            return "The site reviews every proposal.";
        }

        if (!CalculationBasis.IsRunHistory(standard.CalculationBasis))
        {
            return "The planning rate is still the ideal rate; the first rate from run history is reviewed.";
        }

        var change = ChangePct(standard.CalculatedRatePerHour, rate);
        return change > settings.AutoAcceptLimitPct
            ? $"A change of {change.ToString("0.##", CultureInfo.InvariantCulture)}% is more than the {settings.AutoAcceptLimitPct.ToString("0.##", CultureInfo.InvariantCulture)}% accepted without review."
            : null;
    }

    /// <summary>A site's recalculation is due when it never ran or ran <see cref="RateRefreshSettings.EveryDays"/> or more ago.</summary>
    public static bool IsDue(RateRefreshSettings settings, DateTimeOffset? refreshedAt, DateTimeOffset now) =>
        settings.IsScheduled && (refreshedAt is not { } at || now - at >= TimeSpan.FromDays(settings.EveryDays));
}
