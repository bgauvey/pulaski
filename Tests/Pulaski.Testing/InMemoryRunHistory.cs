using Pulaski.Application.Scheduling;

namespace Pulaski.Testing;

/// <summary>Run history for planning rates (D-045): the run standards come from the scheduling store, the runs from <see cref="Runs"/>.</summary>
public sealed class InMemoryRunHistory(InMemoryScheduling scheduling) : IRunHistoryReadStore
{
    /// <summary>Ended runs by line and material.</summary>
    public Dictionary<(Guid Line, Guid Material), List<RunHistoryRow>> Runs { get; } = [];

    /// <summary>The capability's ideal rate by line and material, when it has one.</summary>
    public Dictionary<(Guid Line, Guid Material), (decimal Rate, Guid UomId)> IdealRates { get; } = [];

    public Task<RunHistoryStandard?> GetStandardAsync(Guid runStandardId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (scheduling.FindRunStandard(runStandardId) is not { } found)
        {
            return Task.FromResult<RunHistoryStandard?>(null);
        }

        var s = found.Value;
        var ideal = IdealRates.TryGetValue((s.EquipmentId, s.MaterialId), out var i) ? i : ((decimal, Guid)?)null;
        return Task.FromResult<RunHistoryStandard?>(new RunHistoryStandard(s, found.Version, "EXT-01", "PRF-1", "m", ideal?.Item1, ideal?.Item2));
    }

    public Task<IReadOnlyList<RunHistoryRow>> ListRunsAsync(
        Guid lineId, Guid materialId, DateTimeOffset windowStart, DateTimeOffset windowEnd, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RunHistoryRow>>(Runs.TryGetValue((lineId, materialId), out var runs)
            ? [.. runs.Where(r => r.EndedAt >= windowStart && r.EndedAt < windowEnd)]
            : []);
}
