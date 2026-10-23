using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Testing;

/// <summary>
/// Scheduling read store and repository. Plan sources added with <see cref="Add"/> are returned
/// as-is; once jobs are stored on a line, its plan's jobs come from them, so commands see each
/// other's writes. Locks are no-ops.
/// </summary>
public sealed class InMemoryScheduling : ISchedulingReadStore, ISchedulingRepository
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, LinePlanSource> _plans = [];
    private readonly Dictionary<Guid, SchedulingLine> _lines = [];
    private readonly Dictionary<Guid, Versioned<Job>> _jobs = [];
    private readonly Dictionary<Guid, Versioned<RunStandard>> _runStandards = [];
    private readonly Dictionary<Guid, Versioned<string>> _policies = [];
    private readonly List<(Guid JobId, string? From, string To)> _statusChanges = [];
    private readonly HashSet<(Guid LineId, Guid MaterialId)> _capabilities = [];
    private readonly Dictionary<Guid, Guid> _parents = [];
    private readonly Dictionary<Guid, decimal> _goodQty = [];
    private readonly HashSet<Guid> _endedRuns = [];

    /// <summary>Jobs whose open run a cancel has ended.</summary>
    public IReadOnlyCollection<Guid> EndedRuns
    {
        get
        {
            lock (_lock)
            {
                return [.. _endedRuns];
            }
        }
    }

    /// <summary>The line or group, and whether it can run the material (on a group: some line under it, D-022); null when unknown.</summary>
    public (SchedulingLine Line, bool CanRun)? Eligibility(Guid equipmentId, Guid materialId)
    {
        lock (_lock)
        {
            if (!_lines.TryGetValue(equipmentId, out var line))
            {
                return null;
            }

            var canRun = line.IsGroup
                ? _capabilities.Any(c => c.MaterialId == materialId && Under(equipmentId, c.LineId) && _lines[c.LineId].CanRunJob)
                : _capabilities.Contains((equipmentId, materialId));
            return (line, canRun);
        }
    }

    /// <summary>Lets the line run the material (D-020).</summary>
    public void AddCapability(Guid lineId, Guid materialId)
    {
        lock (_lock)
        {
            _capabilities.Add((lineId, materialId));
        }
    }

    /// <summary>Good quantity reported against a job so far.</summary>
    public void SetGoodQty(Guid jobId, decimal goodQty)
    {
        lock (_lock)
        {
            _goodQty[jobId] = goodQty;
        }
    }

    public bool HasOpenJobs(Guid workOrderId)
    {
        lock (_lock)
        {
            return _jobs.Values.Any(j => j.Value.WorkOrderId == workOrderId && j.Value.IsOpen);
        }
    }

    public IReadOnlyList<(Guid JobId, string? From, string To)> StatusChanges
    {
        get
        {
            lock (_lock)
            {
                return [.. _statusChanges];
            }
        }
    }

    /// <summary>Plan inputs for a line, for the timeline query.</summary>
    public LinePlanSource Add(LinePlanSource source)
    {
        lock (_lock)
        {
            _plans[source.Line.Id] = source;
        }

        return source;
    }

    /// <summary>A line commands can lock, with an empty plan (no rates, rules or calendar) unless <paramref name="plan"/> fills one in.</summary>
    public SchedulingLine AddLine(
        Guid siteId,
        bool schedulable = true,
        string changeoverMode = ChangeoverMode.Auto,
        Func<LinePlanSource, LinePlanSource>? plan = null,
        bool canRunJob = true)
    {
        var line = new SchedulingLine(
            Guid.CreateVersion7(), siteId, "LINE-" + Guid.NewGuid().ToString("N")[..4], schedulable, changeoverMode, canRunJob);
        var source = new LinePlanSource
        {
            Line = new SchedulingLineRow
            {
                Id = line.Id,
                Code = line.Code,
                Name = line.Code,
                IsSchedulable = schedulable,
                CanRunJob = canRunJob,
                ChangeoverMode = changeoverMode,
            },
        };

        lock (_lock)
        {
            _lines[line.Id] = line;
            _plans[line.Id] = plan?.Invoke(source) ?? source;
        }

        return line;
    }

    /// <summary>A group (e.g. a department) that plans for <paramref name="lines"/> and runs nothing itself (D-022).</summary>
    public SchedulingLine AddGroup(Guid siteId, params SchedulingLine[] lines)
    {
        var group = AddLine(siteId, canRunJob: false);
        lock (_lock)
        {
            foreach (var line in lines)
            {
                _parents[line.Id] = group.Id;
            }
        }

        return group;
    }

    public Job AddJob(Job job, long version = 1)
    {
        lock (_lock)
        {
            _jobs[job.Id] = new Versioned<Job>(job, version);
        }

        return job;
    }

    public Versioned<Job>? FindJob(Guid id)
    {
        lock (_lock)
        {
            return _jobs.GetValueOrDefault(id);
        }
    }

    /// <summary>Open jobs on the line in rank order.</summary>
    public IReadOnlyList<Job> JobsOn(Guid lineId)
    {
        lock (_lock)
        {
            return [.. OpenJobs(lineId, excluding: null)];
        }
    }

    public RunStandard AddRunStandard(RunStandard standard, long version = 1)
    {
        lock (_lock)
        {
            _runStandards[standard.Id] = new Versioned<RunStandard>(standard, version);
        }

        return standard;
    }

    public Versioned<RunStandard>? FindRunStandard(Guid id)
    {
        lock (_lock)
        {
            return _runStandards.GetValueOrDefault(id);
        }
    }

    public Versioned<string>? FindPolicy(Guid siteId)
    {
        lock (_lock)
        {
            return _policies.GetValueOrDefault(siteId);
        }
    }

    public Task<LinePlanSource?> GetLinePlanAsync(
        Guid lineId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_plans.TryGetValue(lineId, out var source))
            {
                return Task.FromResult<LinePlanSource?>(null);
            }

            if (source.Line.IsGroup)
            {
                source = source with { Runners = RunnersUnder(lineId) };
            }

            var stored = _jobs.Values.Where(j => j.Value.EquipmentId == lineId).ToList();
            if (stored.Count == 0)
            {
                return Task.FromResult<LinePlanSource?>(source);
            }

            var jobs = OpenJobs(lineId, excluding: null)
                .OrderBy(j => j.Status == JobStatus.Running ? 0 : 1)
                .Select(j => ToPlanRow(_jobs[j.Id]))
                .ToList();
            return Task.FromResult<LinePlanSource?>(source with { Jobs = jobs });
        }
    }

    public Task<IReadOnlyList<RunStandardDto>> ListRunStandardsAsync(Guid lineId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<RunStandardDto>>(
                [.. _runStandards.Values.Where(r => r.Value.EquipmentId == lineId).Select(ToDto)]);
        }
    }

    public Task<SchedulingPolicyDto?> GetPolicyAsync(Guid siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_lines.Values.Any(l => l.SiteId == siteId) && !_policies.ContainsKey(siteId))
            {
                return Task.FromResult<SchedulingPolicyDto?>(null);
            }

            var policy = _policies.GetValueOrDefault(siteId);
            return Task.FromResult<SchedulingPolicyDto?>(new SchedulingPolicyDto
            {
                SiteId = siteId,
                ChangeoverMode = policy?.Value ?? ChangeoverMode.Auto,
                IsDefault = policy is null,
                Version = policy?.Version ?? 0,
            });
        }
    }

    public Task<IReadOnlyList<SchedulingLine>> LockLinesAsync(IReadOnlyCollection<Guid> lineIds, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<SchedulingLine>>(
                [.. lineIds.Where(_lines.ContainsKey).Select(id => _lines[id])]);
        }
    }

    public Task<Guid?> GetJobLineAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(FindJob(jobId)?.Value.EquipmentId);

    public Task<Versioned<Job>?> GetJobForUpdateAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(FindJob(jobId));

    public Task<IReadOnlyList<Guid>> GetWorkOrderLinesAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Guid>>(
                [.. WorkOrderJobs(workOrderId).Select(j => j.Value.EquipmentId).Distinct()]);
        }
    }

    public Task<IReadOnlyList<Versioned<Job>>> GetWorkOrderJobsForUpdateAsync(Guid workOrderId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Versioned<Job>>>([.. WorkOrderJobs(workOrderId)]);
        }
    }

    public Task<PlacementCheckRow?> GetPlacementCheckAsync(
        Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_lines.TryGetValue(lineId, out var line)
                ? new PlacementCheckRow { LineSiteId = line.SiteId, IsEligible = _capabilities.Contains((lineId, materialId)) }
                : null);
        }
    }

    public Task<bool> IsEligibleInGroupAsync(Guid groupId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_capabilities.Any(c => c.MaterialId == materialId && Under(groupId, c.LineId) && _lines[c.LineId].CanRunJob));
        }
    }

    public Task<bool> IsUnderAsync(Guid groupId, Guid lineId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(Under(groupId, lineId));
        }
    }

    private bool Under(Guid groupId, Guid lineId)
    {
        for (var at = lineId; _parents.TryGetValue(at, out var parent); at = parent)
        {
            if (parent == groupId)
            {
                return true;
            }
        }

        return false;
    }

    private List<RunnerRateRow> RunnersUnder(Guid groupId) =>
        [.. _capabilities
            .Where(c => Under(groupId, c.LineId) && _lines[c.LineId].CanRunJob)
            .Select(c =>
            {
                var rate = _plans[c.LineId].Rates.FirstOrDefault(r => r.MaterialId == c.MaterialId);
                return new RunnerRateRow
                {
                    LineId = c.LineId,
                    LineCode = _lines[c.LineId].Code,
                    MaterialId = c.MaterialId,
                    UomId = rate?.UomId,
                    RatePerHour = rate?.RatePerHour,
                    RateSource = rate?.RateSource,
                };
            })
            .OrderBy(r => r.LineCode, StringComparer.Ordinal)];

    public Task<bool> IsEligibleAsync(Guid lineId, Guid materialId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_capabilities.Contains((lineId, materialId)));
        }
    }

    public Task<decimal> GetGoodQtyAsync(Guid jobId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_goodQty.GetValueOrDefault(jobId));
        }
    }

    public Task EndOpenRunsAsync(Guid jobId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _endedRuns.Add(jobId);
        }

        return Task.CompletedTask;
    }

    public Task<RankGap?> FindGapAsync(Guid lineId, Guid? beforeJobId, Guid? excludingJobId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var open = OpenJobs(lineId, excludingJobId);
            if (beforeJobId is null)
            {
                return Task.FromResult<RankGap?>(new RankGap(open.Count == 0 ? null : open[^1].SequenceRank, null));
            }

            var index = open.FindIndex(j => j.Id == beforeJobId);
            return Task.FromResult(index < 0
                ? null
                : new RankGap(index == 0 ? null : open[index - 1].SequenceRank, open[index].SequenceRank));
        }
    }

    public Task RenumberAsync(Guid lineId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var rank = 1m;
            foreach (var job in OpenJobs(lineId, excluding: null))
            {
                var current = _jobs[job.Id];
                _jobs[job.Id] = new Versioned<Job>(job with { SequenceRank = rank++ }, current.Version + 1);
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> InsertJobAsync(Job job, DateTimeOffset at, CancellationToken cancellationToken)
    {
        AddJob(job);
        return Task.FromResult(1L);
    }

    public Task<long> UpdateJobAsync(Job job, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _jobs[job.Id].Version + 1;
            _jobs[job.Id] = new Versioned<Job>(job, version);
            return Task.FromResult(version);
        }
    }

    public Task AppendStatusChangeAsync(Guid jobId, string? fromStatus, string toStatus, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _statusChanges.Add((jobId, fromStatus, toStatus));
        }

        return Task.CompletedTask;
    }

    public Task<Versioned<RunStandard>?> GetRunStandardForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(FindRunStandard(id));

    public Task<long> UpdateRunStandardModificationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _runStandards[standard.Id].Version + 1;
            _runStandards[standard.Id] = new Versioned<RunStandard>(standard, version);
            return Task.FromResult(version);
        }
    }

    public Task<long> UpdateRunStandardCalculationAsync(RunStandard standard, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpdateRunStandardModificationAsync(standard, at, cancellationToken);

    public Task<Versioned<string>?> GetPolicyForUpdateAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult(FindPolicy(siteId));

    public Task<long> InsertPolicyAsync(Guid siteId, string changeoverMode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _policies[siteId] = new Versioned<string>(changeoverMode, 1);
        }

        return Task.FromResult(1L);
    }

    public Task<long> UpdatePolicyAsync(Guid siteId, string changeoverMode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = _policies[siteId].Version + 1;
            _policies[siteId] = new Versioned<string>(changeoverMode, version);
            return Task.FromResult(version);
        }
    }

    /// <summary>Caller holds <see cref="_lock"/>.</summary>
    private IEnumerable<Versioned<Job>> WorkOrderJobs(Guid workOrderId) =>
        _jobs.Values.Where(j => j.Value.WorkOrderId == workOrderId && j.Value.IsOpen);

    /// <summary>Caller holds <see cref="_lock"/>.</summary>
    private List<Job> OpenJobs(Guid lineId, Guid? excluding) =>
        [.. _jobs.Values
            .Select(v => v.Value)
            .Where(j => j.EquipmentId == lineId && j.IsOpen && j.Id != excluding)
            .OrderBy(j => j.SequenceRank)
            .ThenBy(j => j.JobNumber, StringComparer.Ordinal)];

    /// <summary>Caller holds <see cref="_lock"/>.</summary>
    private PlanJobRow ToPlanRow(Versioned<Job> row)
    {
        var j = row.Value;
        return new PlanJobRow
        {
            JobId = j.Id,
            JobNumber = j.JobNumber,
            JobType = j.JobType,
            Status = j.Status,
            MaterialId = j.MaterialId,
            MaterialCode = j.MaterialId is { } m ? "M-" + m.ToString("N")[^4..] : null,
            PlannedQty = j.PlannedQty,
            UomId = j.UomId,
            EarliestStart = j.EarliestStart,
            PlannedDuration = j.PlannedDuration,
            PlannedStart = j.PlannedStart,
            PlannedEnd = j.PlannedEnd,
            IsLocked = j.IsLocked,
            GoodQty = _goodQty.GetValueOrDefault(j.Id),
            Version = row.Version,
            WorkOrderId = j.WorkOrderId,
            LatestEnd = j.LatestEnd,
            GroupEquipmentId = j.GroupEquipmentId,
        };
    }

    private static RunStandardDto ToDto(Versioned<RunStandard> row)
    {
        var s = row.Value;
        return new RunStandardDto
        {
            Id = s.Id,
            EquipmentId = s.EquipmentId,
            MaterialId = s.MaterialId,
            MaterialCode = "MAT",
            UomId = s.UomId,
            UomCode = "m",
            CalculatedRatePerHour = s.CalculatedRatePerHour,
            CalculationBasis = s.CalculationBasis,
            CalculatedAt = s.CalculatedAt,
            ModifiedRatePerHour = s.ModifiedRatePerHour,
            ModifiedAt = s.ModifiedAt,
            ModifiedBy = s.ModifiedBy,
            ModifiedNote = s.ModifiedNote,
            RateSource = s.RateSource,
            EffectiveRatePerHour = s.EffectiveRatePerHour,
            Version = row.Version,
        };
    }
}
