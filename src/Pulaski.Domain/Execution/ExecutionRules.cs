using System.Globalization;

namespace Pulaski.Domain.Execution;

/// <summary>A site's tolerances for production reporting (D-035). A site without a policy uses <see cref="Default"/>.</summary>
public sealed record ExecutionPolicy(decimal RateTolerancePct, decimal ConsumptionTolerancePct)
{
    public static ExecutionPolicy Default { get; } = new(110m, 110m);
}

/// <summary>
/// The most a run can have made (D-035): its elapsed time at the line's ideal rate, plus the site's
/// tolerance. The ideal (nameplate) rate is what the line can physically do; the planning rate is
/// usually lower, so capping at it would refuse honest reports.
/// </summary>
public static class RateCap
{
    /// <param name="idealRatePerHour">The capability's ideal rate, already in the unit of the quantities.</param>
    /// <param name="tolerancePct">Percent of the ideal rate allowed, at least 100.</param>
    /// <param name="elapsed">How long the run has lasted.</param>
    public static decimal Cap(decimal idealRatePerHour, decimal tolerancePct, TimeSpan elapsed) =>
        idealRatePerHour * (decimal)elapsed.TotalHours * tolerancePct / 100m;

    /// <summary>
    /// Refuses a report that would take the run over its cap: <paramref name="reportedSoFar"/> (good plus
    /// scrap already in the run) and <paramref name="adding"/> (good plus scrap being reported) together.
    /// </summary>
    /// <exception cref="DomainRuleException">The run would exceed its cap.</exception>
    public static void Ensure(
        decimal idealRatePerHour, decimal tolerancePct, TimeSpan elapsed, decimal reportedSoFar, decimal adding, string unitCode, string lineCode)
    {
        var cap = Cap(idealRatePerHour, tolerancePct, elapsed);
        if (reportedSoFar + adding <= cap)
        {
            return;
        }

        var room = Math.Max(0m, cap - reportedSoFar);
        throw new DomainRuleException(
            $"{Qty(reportedSoFar + adding)} {unitCode} in {Duration(elapsed)} is more than {lineCode} can make: its ideal rate is " +
            $"{Qty(idealRatePerHour)} {unitCode}/h, allowing {Qty(cap)} {unitCode} with the site's {Qty(tolerancePct)}% tolerance. " +
            $"At most {Qty(room)} {unitCode} more can be reported into this run.");
    }

    public static string Qty(decimal value) => Math.Round(value, 3).ToString("#,0.###", CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan span) => span.TotalHours >= 1
        ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
        : $"{Math.Max(0, (int)span.TotalMinutes)} min";
}

/// <summary>A process link into a job's operation (D-023): the upstream operation, and whether to wait for its start or its end.</summary>
public sealed record UpstreamLink(Guid FromOperationId, string FromOperationLabel, string DependencyType, TimeSpan Lag);

/// <summary>A job of the same work order on an upstream operation, as readiness sees it.</summary>
public sealed record UpstreamJob(Guid ProcessOperationId, string JobNumber, string Status, DateTimeOffset? FirstStartedAt, DateTimeOffset? CompletedAt);

/// <summary>Whether a job may start now, and if not, what it waits for and until when (if known).</summary>
public sealed record Readiness(bool IsReady, string? Waiting, DateTimeOffset? ReadyAt)
{
    public static Readiness Ready { get; } = new(true, null, null);
}

/// <summary>
/// Downstream jobs become ready when their process links are met (D-035, from D-023): an "after
/// start" link once any job of the upstream operation has started, an "after end" link once all
/// of them are completed, each plus its lag. Pulaski never starts a job by itself; it says when one can.
/// </summary>
public static class JobReadiness
{
    public const string AfterStart = "after_start";
    public const string AfterEnd = "after_end";

    public static Readiness For(IReadOnlyList<UpstreamLink> links, IReadOnlyList<UpstreamJob> upstream, DateTimeOffset now)
    {
        foreach (var link in links)
        {
            var jobs = upstream.Where(j => j.ProcessOperationId == link.FromOperationId && j.Status != "cancelled").ToList();
            if (jobs.Count == 0)
            {
                continue;
            }

            DateTimeOffset? metAt;
            if (link.DependencyType == AfterStart)
            {
                metAt = jobs.Select(j => j.FirstStartedAt).Where(t => t is not null).Min();
                if (metAt is null)
                {
                    return new Readiness(false, $"Waiting for {link.FromOperationLabel} to start{LagText(link.Lag)}.", null);
                }
            }
            else
            {
                if (jobs.Any(j => j.Status != "completed" && j.Status != "closed"))
                {
                    var open = jobs.Where(j => j.Status != "completed" && j.Status != "closed").Select(j => j.JobNumber);
                    return new Readiness(false, $"Waiting for {link.FromOperationLabel} to end ({string.Join(", ", open)}){LagText(link.Lag)}.", null);
                }

                metAt = jobs.Max(j => j.CompletedAt);
            }

            var readyAt = metAt + link.Lag;
            if (readyAt > now)
            {
                return new Readiness(false, $"{link.FromOperationLabel} {(link.DependencyType == AfterStart ? "started" : "ended")}; waiting out its {RateCap.Duration(link.Lag)} lag.", readyAt);
            }
        }

        return Readiness.Ready;
    }

    private static string LagText(TimeSpan lag) => lag > TimeSpan.Zero ? $", then {RateCap.Duration(lag)}" : "";
}

/// <summary>
/// Whether a job's consumption of a material is beyond what its planned components allow (D-035).
/// The allowance scales up when the job makes more than planned, so over-production doesn't warn.
/// It warns, never refuses: the floor knows best what it used.
/// </summary>
public static class ConsumptionCheck
{
    /// <returns>A warning, or null when within the allowance or nothing was planned to compare with.</returns>
    public static string? Warning(
        decimal plannedComponentQty, decimal jobPlannedQty, decimal madeSoFar, decimal consumedSoFar, decimal consuming, decimal tolerancePct,
        string materialCode, string unitCode)
    {
        if (plannedComponentQty <= 0 || jobPlannedQty <= 0)
        {
            return null;
        }

        var allowed = plannedComponentQty * Math.Max(1m, madeSoFar / jobPlannedQty) * tolerancePct / 100m;
        var total = consumedSoFar + consuming;
        return total <= allowed
            ? null
            : $"{RateCap.Qty(total)} {unitCode} of {materialCode} is more than planned: the components allow {RateCap.Qty(allowed)} {unitCode} " +
              $"for what the job has made, with the site's {RateCap.Qty(tolerancePct)}% tolerance. It was recorded; check it.";
    }
}
