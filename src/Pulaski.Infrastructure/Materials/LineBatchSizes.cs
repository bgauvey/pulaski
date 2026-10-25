using Pulaski.Application.Materials;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Materials;

/// <summary>Lines' own batch sizes (D-065), from the capability in effect, in the command's transaction.</summary>
internal sealed class LineBatchSizes(DbSession session) : ILineBatchSizes
{
    private const string Sizes = """
        SELECT c.equipment_id, c.batch_size
        FROM core.equipment_capability c
        WHERE c.material_id = @MaterialId AND c.equipment_id = ANY(@Ids) AND c.batch_size IS NOT NULL
          AND c.effective_from <= @At AND (c.effective_to IS NULL OR c.effective_to > @At)
        """;

    public async Task<IReadOnlyDictionary<Guid, decimal>> GetAsync(
        Guid materialId, IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0)
        {
            return new Dictionary<Guid, decimal>();
        }

        var rows = await session.QueryAsync<Row>(Sizes, new { MaterialId = materialId, Ids = equipmentIds.ToArray(), At = at }, cancellationToken);
        return rows.ToDictionary(r => r.EquipmentId, r => r.BatchSize);
    }

    private sealed class Row
    {
        public Guid EquipmentId { get; init; }
        public decimal BatchSize { get; init; }
    }
}
