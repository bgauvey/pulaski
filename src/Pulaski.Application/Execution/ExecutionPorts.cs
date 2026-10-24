using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Execution;

/// <summary>Read side for execution (D-035): the floor's line board and a job's runs and reports.</summary>
public interface IExecutionReadStore
{
    /// <summary>The site's lines that run jobs, each with its running job and what is next.</summary>
    Task<IReadOnlyList<LineBoardRowDto>> GetLineBoardAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The job with its runs and reports; null when it doesn't exist.</summary>
    Task<JobExecutionDto?> GetJobAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>The site's tolerances; the defaults when it has no policy row. Null when <paramref name="siteId"/> isn't a site.</summary>
    Task<ExecutionPolicyDto?> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's partial batches waiting for approval (D-036), oldest first.</summary>
    Task<IReadOnlyList<PartialBatchDto>> ListPendingPartialBatchesAsync(Guid siteId, CancellationToken cancellationToken);
}

/// <summary>Write side: runs inside the current <see cref="IUnitOfWork"/>. The line and job are locked first by the scheduling repository (D-017).</summary>
public interface IExecutionRepository
{
    /// <summary>The job's work order, read without a lock, so the caller can lock the order's lines first; null for a job of no order.</summary>
    Task<Guid?> GetJobWorkOrderAsync(Guid jobId, CancellationToken cancellationToken);

    Task<bool> JobNumberExistsAsync(Guid siteId, string jobNumber, CancellationToken cancellationToken);

    /// <summary>The job's open run, locked; null when none is open.</summary>
    Task<JobRun?> GetOpenRunForUpdateAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>A run, locked; null when it doesn't exist.</summary>
    Task<JobRun?> GetRunForUpdateAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>The job's run going on at <paramref name="at"/> (started at or before it, not ended before it), locked; null when none (D-057).</summary>
    Task<JobRun?> GetRunAtForUpdateAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The job number of whatever has an open run on the line, other than <paramref name="exceptJobId"/>; null when the line is free.</summary>
    Task<string?> GetRunningJobOnLineAsync(Guid lineId, Guid exceptJobId, CancellationToken cancellationToken);

    /// <summary>The last job that ran on the line: its type and material, for the changeover check (D-015). Null when nothing has run there.</summary>
    Task<(string JobType, Guid? MaterialId, string? MaterialCode)?> GetLastRunJobAsync(Guid lineId, CancellationToken cancellationToken);

    Task<int> NextRunNoAsync(Guid jobId, CancellationToken cancellationToken);

    Task InsertRunAsync(JobRun run, CancellationToken cancellationToken);

    /// <summary>Writes the end, status, end reason and the good and scrap totals.</summary>
    Task UpdateRunAsync(JobRun run, CancellationToken cancellationToken);

    Task InsertCountAsync(ProductionCountRow count, CancellationToken cancellationToken);

    /// <summary>A report's count rows by its id (D-063); null when there is no such report, or it predates report ids.</summary>
    Task<ProductionReportRecord?> GetReportAsync(Guid reportId, CancellationToken cancellationToken);

    /// <summary>The partial batch in a lot, locked; null when it isn't one.</summary>
    Task<PartialBatch?> GetPartialBatchOfLotForUpdateAsync(Guid lotId, CancellationToken cancellationToken);

    Task<ExecutionPolicy> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The capability's ideal rate for the material on the line in effect at <paramref name="at"/>; null when there is none.</summary>
    Task<(decimal RatePerHour, Guid UomId)?> GetIdealRateAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The job's planned quantity of a component (consume direction), in its unit; null when none is planned.</summary>
    Task<(decimal Quantity, Guid UomId)?> GetPlannedComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken);

    /// <summary>How much of the material the job has consumed so far, in its base unit.</summary>
    Task<decimal> GetConsumedAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken);

    /// <summary>Good plus scrap reported across the job's runs, in the job's unit.</summary>
    Task<decimal> GetMadeAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The process links into the job's operation and the order's jobs on those upstream operations, for readiness.</summary>
    Task<(IReadOnlyList<UpstreamLink> Links, IReadOnlyList<UpstreamJob> Jobs)> GetUpstreamAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The order's jobs on operations that wait for <paramref name="jobId"/>'s operation, for the notes after a start or completion.</summary>
    Task<IReadOnlyList<DownstreamJob>> GetDownstreamJobsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Closes the work order's completed jobs, with a status change each; returns how many.</summary>
    Task<int> CloseCompletedJobsAsync(Guid workOrderId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The site's own policy, locked, with its version; null while it uses the defaults.</summary>
    Task<Versioned<ExecutionPolicy>?> GetPolicyForUpdateAsync(Guid siteId, CancellationToken cancellationToken);

    /// <returns>The policy's new version.</returns>
    Task<long> UpsertPolicyAsync(Guid siteId, ExecutionPolicy policy, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>True when the run already has its one partial batch (D-036).</summary>
    Task<bool> RunHasPartialBatchAsync(Guid runId, CancellationToken cancellationToken);

    Task InsertPartialBatchAsync(PartialBatch batch, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A partial batch, locked; null when it doesn't exist.</summary>
    Task<PartialBatch?> GetPartialBatchForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes the status and the decision.</summary>
    Task UpdatePartialBatchAsync(PartialBatch batch, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>An active reason of the kind and code, the site's own or enterprise-wide; null when there is none.</summary>
    Task<Guid?> FindReasonAsync(string kind, string code, Guid siteId, CancellationToken cancellationToken);
}

/// <summary>One row of <c>ts.production_count</c>: everything processed in a report, or the defective part of it.</summary>
public sealed record ProductionCountRow(
    Guid Id,
    DateTimeOffset OccurredAt,
    Guid EquipmentId,
    Guid RunId,
    Guid JobId,
    string CountKind,
    decimal DeltaQty,
    Guid UomId,
    Guid? ScrapReasonCodeId,
    string ActorId,
    string? Comment,
    DateTimeOffset RecordedAt)
{
    /// <summary>The report it belongs to (D-063): its processed row's id.</summary>
    public Guid? ReportId { get; init; }

    /// <summary>For a correction: the row it offsets.</summary>
    public Guid? CorrectsId { get; init; }

    /// <summary><c>operator</c>, or <c>correction</c> for a row that offsets another.</summary>
    public string Source { get; init; } = "operator";
}

/// <summary>A production report's count rows (D-063), with whether it has been reversed.</summary>
public sealed record ProductionReportRecord(Guid ReportId, Guid JobId, Guid RunId, IReadOnlyList<ProductionCountRow> Rows, bool IsReversed)
{
    public decimal Good => Rows.Where(r => r.CountKind == CountKind.Processed).Sum(r => r.DeltaQty) - Scrap;

    public decimal Scrap => Rows.Where(r => r.CountKind == CountKind.Defective).Sum(r => r.DeltaQty);
}

/// <summary>A job waiting for another, with where it runs.</summary>
public sealed record DownstreamJob(Guid JobId, string JobNumber, string LineCode, string Status);

/// <summary>Values match <c>ts.production_count.count_kind</c>: all output, and the scrapped part of it.</summary>
public static class CountKind
{
    public const string Processed = "processed";
    public const string Defective = "defective";
}
