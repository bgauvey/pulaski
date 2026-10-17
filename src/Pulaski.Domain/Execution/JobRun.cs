namespace Pulaski.Domain.Execution;

/// <summary>Values match <c>core.job_run.status</c>. A pause ends the run, so <c>paused</c> isn't used (D-035).</summary>
public static class RunStatus
{
    public const string Running = "running";
    public const string Ended = "ended";
}

/// <summary>
/// One stretch of a job's execution on a line (ISA-95 job response, D-003). Starting or resuming a
/// job opens a run; pausing or completing it ends the run. Good and scrap are the totals reported
/// into it, in the job's unit, kept with the counts in <c>ts.production_count</c>.
/// </summary>
public sealed record JobRun(
    Guid Id,
    Guid JobId,
    Guid EquipmentId,
    int RunNo,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    Guid? EndReasonCodeId,
    decimal GoodQty,
    decimal ScrapQty,
    Guid? UomId)
{
    public bool IsOpen => EndedAt is null;

    /// <summary>Everything reported into the run, good and scrap: what the rate cap limits.</summary>
    public decimal TotalQty => GoodQty + ScrapQty;

    public static JobRun Open(Guid id, Guid jobId, Guid lineId, int runNo, DateTimeOffset at, Guid? uomId) =>
        new(id, jobId, lineId, runNo, at, null, RunStatus.Running, null, 0m, 0m, uomId);

    /// <exception cref="DomainRuleException">The run has already ended.</exception>
    public JobRun End(DateTimeOffset at, Guid? reasonCodeId)
    {
        if (!IsOpen)
        {
            throw new DomainRuleException($"Run {RunNo} has already ended.");
        }

        return this with { EndedAt = at < StartedAt ? StartedAt : at, Status = RunStatus.Ended, EndReasonCodeId = reasonCodeId };
    }

    /// <summary>How long the run has lasted at <paramref name="now"/>: to its end, or so far.</summary>
    public TimeSpan Elapsed(DateTimeOffset now) => (EndedAt ?? now) - StartedAt is { } span && span > TimeSpan.Zero ? span : TimeSpan.Zero;

    /// <summary>Moves output already reported as good to scrap, as when a partial batch is rejected (D-036).</summary>
    public JobRun Reclassify(decimal quantity) =>
        this with { GoodQty = Math.Max(0m, GoodQty - quantity), ScrapQty = ScrapQty + quantity };

    /// <exception cref="DomainRuleException">A quantity is negative or both are zero.</exception>
    public JobRun Add(decimal good, decimal scrap)
    {
        if (good < 0 || scrap < 0 || good + scrap == 0)
        {
            throw new DomainRuleException("Report a good or scrap quantity greater than zero.");
        }

        return this with { GoodQty = GoodQty + good, ScrapQty = ScrapQty + scrap };
    }
}
