using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Quality;

namespace Pulaski.Application.Quality;

/// <summary>An open run with what its sampling plans need (#136).</summary>
public sealed record SamplingRun(
    Guid RunId, Guid JobId, string JobNumber, Guid LineId, string LineCode, Guid MaterialId, DateTimeOffset StartedAt, string? UomCode, string? SiteTimeZone);

/// <summary>A released spec item with a plan, for a material.</summary>
public sealed record SamplingItem(Guid MaterialId, Guid CharacteristicId, string Code, string Name, SamplingPlan Plan, int SortOrder);

/// <summary>What due samples are worked out from (#136): open production runs, their materials' released plans, output and results.</summary>
public interface ISamplingReadStore
{
    /// <summary>The open production runs at <paramref name="siteId"/>, or of <paramref name="jobId"/>.</summary>
    Task<IReadOnlyList<SamplingRun>> OpenRunsAsync(Guid? siteId, Guid? jobId, CancellationToken cancellationToken);

    /// <summary>The items with a plan in each material's released specification.</summary>
    Task<IReadOnlyList<SamplingItem>> PlansAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken);

    /// <summary>Each run's reported output (processed, with corrections), in time order.</summary>
    Task<IReadOnlyList<(Guid RunId, DateTimeOffset At, decimal Quantity)>> OutputAsync(IReadOnlyCollection<Guid> runIds, DateTimeOffset since, CancellationToken cancellationToken);

    /// <summary>The results recorded against each run, not excluded: which characteristic, when taken.</summary>
    Task<IReadOnlyList<(Guid RunId, Guid CharacteristicId, DateTimeOffset At)>> SamplesAsync(
        IReadOnlyCollection<Guid> runIds, DateTimeOffset since, CancellationToken cancellationToken);
}

/// <summary>The samples running jobs owe (#136, D-121), at a site or for one job, overdue first; worked out now, nothing stored.</summary>
public sealed record GetDueSamplesQuery(Guid? SiteId, Guid? JobId) : IQuery<IReadOnlyList<DueSampleDto>>;

internal sealed class GetDueSamplesHandler(ISamplingReadStore store, TimeProvider time) : IQueryHandler<GetDueSamplesQuery, IReadOnlyList<DueSampleDto>>
{
    public async Task<IReadOnlyList<DueSampleDto>> HandleAsync(GetDueSamplesQuery query, CancellationToken cancellationToken)
    {
        var runs = await store.OpenRunsAsync(query.SiteId, query.JobId, cancellationToken);
        if (runs.Count == 0)
        {
            return [];
        }

        var plans = (await store.PlansAsync([.. runs.Select(r => r.MaterialId).Distinct()], cancellationToken)).ToLookup(p => p.MaterialId);
        var planned = runs.Where(r => plans[r.MaterialId].Any()).ToList();
        if (planned.Count == 0)
        {
            return [];
        }

        // Results up to the longest grace early still count for a run's first due sample.
        var since = planned.Min(r => r.StartedAt) - TimeSpan.FromMinutes(SamplingPlan.MaxGrace);
        var runIds = planned.Select(r => r.RunId).ToList();
        var output = (await store.OutputAsync(runIds, since, cancellationToken)).ToLookup(o => o.RunId, o => (o.At, o.Quantity));
        var samples = (await store.SamplesAsync(runIds, since, cancellationToken)).ToLookup(s => (s.RunId, s.CharacteristicId), s => s.At);
        var now = time.GetUtcNow();
        return [.. planned
            .SelectMany(run => plans[run.MaterialId].OrderBy(p => p.SortOrder).Select(item =>
            {
                var due = SamplingSchedule.Evaluate(item.Plan, run.StartedAt, null, now, [.. output[run.RunId]], [.. samples[(run.RunId, item.CharacteristicId)]]);
                return new DueSampleDto
                {
                    RunId = run.RunId,
                    JobId = run.JobId,
                    JobNumber = run.JobNumber,
                    LineId = run.LineId,
                    LineCode = run.LineCode,
                    CharacteristicId = item.CharacteristicId,
                    CharacteristicCode = item.Code,
                    CharacteristicName = item.Name,
                    Status = due.Status,
                    DueAt = due.DueAt,
                    Missed = due.Missed,
                    NextAt = due.NextAt,
                    NextAfterUnits = due.NextAfterUnits,
                    UomCode = run.UomCode,
                    Taken = due.Taken,
                    AtStart = item.Plan.AtStart,
                    EveryHours = item.Plan.EveryHours,
                    EveryUnits = item.Plan.EveryUnits,
                    SiteTimeZone = run.SiteTimeZone,
                };
            }))
            .OrderBy(d => SampleStatus.Rank(d.Status)).ThenBy(d => d.DueAt ?? d.NextAt).ThenBy(d => d.LineCode, StringComparer.Ordinal)];
    }
}
