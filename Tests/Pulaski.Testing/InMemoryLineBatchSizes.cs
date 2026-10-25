using Pulaski.Application.Materials;

namespace Pulaski.Testing;

/// <summary>Lines' own batch sizes (D-065), as a test sets them by line and material.</summary>
public sealed class InMemoryLineBatchSizes : ILineBatchSizes
{
    public Dictionary<(Guid EquipmentId, Guid MaterialId), decimal> Sizes { get; } = [];

    public Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(Guid materialId, IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyDictionary<Guid, decimal>>(equipmentIds
            .Where(id => Sizes.ContainsKey((id, materialId)))
            .ToDictionary(id => id, id => Sizes[(id, materialId)]));
}
