using Pulaski.Application.Execution;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Execution;

namespace Pulaski.Testing;

/// <summary>In-memory runs, counts and execution settings for the execution and inventory handlers (D-035).</summary>
public sealed class InMemoryExecution : IExecutionRepository, IExecutionReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, JobRun> _runs = [];
    private readonly List<ProductionCountRow> _counts = [];
    private readonly Dictionary<Guid, ExecutionPolicy> _policies = [];

    /// <summary>A job's work order; <see cref="InMemoryStores"/> points it at its scheduling store.</summary>
    public Func<Guid, Guid?> WorkOrderOf { get; set; } = _ => null;

    /// <summary>What a test says each job has planned, has consumed and waits for.</summary>
    public Dictionary<(Guid Job, Guid Material), (decimal Quantity, Guid UomId)> PlannedComponents { get; } = [];

    public Dictionary<(Guid Job, Guid Material), decimal> Consumed { get; } = [];

    public Dictionary<(Guid Line, Guid Material), (decimal RatePerHour, Guid UomId)> IdealRates { get; } = [];

    public Dictionary<Guid, (IReadOnlyList<UpstreamLink> Links, IReadOnlyList<UpstreamJob> Jobs)> Upstream { get; } = [];

    public Dictionary<Guid, IReadOnlyList<DownstreamJob>> Downstream { get; } = [];

    public HashSet<string> JobNumbers { get; } = [];

    /// <summary>The last job that ran on each line, for the changeover check.</summary>
    public Dictionary<Guid, (string JobType, Guid? MaterialId, string? MaterialCode)> LastRunJob { get; } = [];

    public IReadOnlyList<ProductionCountRow> Counts
    {
        get
        {
            lock (_lock)
            {
                return [.. _counts];
            }
        }
    }

    /// <summary>Opens a run for a job, as a test's starting point.</summary>
    public JobRun OpenRun(Guid jobId, Guid lineId, DateTimeOffset startedAt, Guid? uomId = null)
    {
        var run = JobRun.Open(Guid.CreateVersion7(), jobId, lineId, RunsOf(jobId).Count + 1, startedAt, uomId);
        lock (_lock)
        {
            _runs[run.Id] = run;
        }

        return run;
    }

    public IReadOnlyList<JobRun> RunsOf(Guid jobId)
    {
        lock (_lock)
        {
            return [.. _runs.Values.Where(r => r.JobId == jobId).OrderBy(r => r.RunNo)];
        }
    }

    public JobRun? FindRun(Guid runId)
    {
        lock (_lock)
        {
            return _runs.GetValueOrDefault(runId);
        }
    }

    public void SetPolicy(Guid siteId, ExecutionPolicy policy)
    {
        lock (_lock)
        {
            _policies[siteId] = policy;
        }
    }

    public Task<Guid?> GetJobWorkOrderAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(WorkOrderOf(jobId));

    public Task<bool> JobNumberExistsAsync(Guid siteId, string jobNumber, CancellationToken cancellationToken) =>
        Task.FromResult(JobNumbers.Contains(jobNumber));

    public Task<JobRun?> GetOpenRunForUpdateAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(RunsOf(jobId).FirstOrDefault(r => r.IsOpen));

    public Task<JobRun?> GetRunForUpdateAsync(Guid runId, CancellationToken cancellationToken) => Task.FromResult(FindRun(runId));

    public Task<string?> GetRunningJobOnLineAsync(Guid lineId, Guid exceptJobId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_runs.Values.FirstOrDefault(r => r.EquipmentId == lineId && r.IsOpen && r.JobId != exceptJobId)?.JobId.ToString());
        }
    }

    public Task<(string JobType, Guid? MaterialId, string? MaterialCode)?> GetLastRunJobAsync(Guid lineId, CancellationToken cancellationToken) =>
        Task.FromResult(LastRunJob.TryGetValue(lineId, out var last) ? last : ((string, Guid?, string?)?)null);

    public Task<int> NextRunNoAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(RunsOf(jobId).Count + 1);

    public Task InsertRunAsync(JobRun run, CancellationToken cancellationToken) => UpdateRunAsync(run, cancellationToken);

    public Task UpdateRunAsync(JobRun run, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _runs[run.Id] = run;
        }

        return Task.CompletedTask;
    }

    public Task InsertCountAsync(ProductionCountRow count, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _counts.Add(count);
        }

        return Task.CompletedTask;
    }

    public Task<ExecutionPolicy> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_policies.GetValueOrDefault(siteId, ExecutionPolicy.Default));
        }
    }

    public Task UpsertPolicyAsync(Guid siteId, ExecutionPolicy policy, DateTimeOffset at, CancellationToken cancellationToken)
    {
        SetPolicy(siteId, policy);
        return Task.CompletedTask;
    }

    public Task<(decimal RatePerHour, Guid UomId)?> GetIdealRateAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult(IdealRates.TryGetValue((lineId, materialId), out var rate) ? rate : ((decimal, Guid)?)null);

    public Task<(decimal Quantity, Guid UomId)?> GetPlannedComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(PlannedComponents.TryGetValue((jobId, materialId), out var planned) ? planned : ((decimal, Guid)?)null);

    public Task<decimal> GetConsumedAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken) =>
        Task.FromResult(Consumed.GetValueOrDefault((jobId, materialId)));

    public Task<decimal> GetMadeAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult(RunsOf(jobId).Sum(r => r.TotalQty));

    public Task<(IReadOnlyList<UpstreamLink> Links, IReadOnlyList<UpstreamJob> Jobs)> GetUpstreamAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(Upstream.TryGetValue(jobId, out var upstream) ? upstream : ([], []));

    public Task<IReadOnlyList<DownstreamJob>> GetDownstreamJobsAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(Downstream.GetValueOrDefault(jobId) ?? []);

    public Task<int> CloseCompletedJobsAsync(Guid workOrderId, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(0);

    public Task<IReadOnlyList<LineBoardRowDto>> GetLineBoardAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LineBoardRowDto>>([]);

    public Task<JobExecutionDto?> GetJobAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken) => Task.FromResult<JobExecutionDto?>(null);

    Task<ExecutionPolicyDto?> IExecutionReadStore.GetPolicyAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<ExecutionPolicyDto?>(null);
}
