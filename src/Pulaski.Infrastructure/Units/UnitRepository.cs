using Pulaski.Application.Abstractions;
using Pulaski.Application.Units;
using Pulaski.Domain.Units;
using Pulaski.Infrastructure.Persistence;

using Pulaski.Infrastructure.MachineData;

namespace Pulaski.Infrastructure.Units;

/// <summary>Write side for units and material conversions (D-026). Every statement runs in the command's transaction.</summary>
internal sealed class UnitRepository(DbSession session) : IUnitRepository
{
    private const string GetForUpdate = """
        SELECT id, code, name, dimension, to_base_factor, is_active, xmin::text::bigint AS version
        FROM core.uom
        WHERE id = @Id
        FOR UPDATE
        """;

    private const string Insert = """
        INSERT INTO core.uom (id, code, name, dimension, to_base_factor, is_active, created_at, updated_at)
        VALUES (@Id, @Code, @Name, @Dimension, @ToBaseFactor, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.uom SET name = @Name, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string LockMaterial = "SELECT true FROM core.material WHERE id = @Id FOR UPDATE";

    private const string SetConversion = """
        INSERT INTO core.material_uom_conversion (material_id, from_uom_id, to_uom_id, factor, is_active, updated_at)
        VALUES (@MaterialId, @FromUomId, @ToUomId, @Factor, true, @At)
        ON CONFLICT (material_id, from_uom_id, to_uom_id)
        DO UPDATE SET factor = excluded.factor, is_active = true, updated_at = excluded.updated_at
        """;

    private const string Deactivate = """
        UPDATE core.material_uom_conversion SET is_active = false, updated_at = @At
        WHERE material_id = @MaterialId AND from_uom_id = @FromUomId AND to_uom_id = @ToUomId AND is_active
        """;

    public async Task<IReadOnlyList<Unit>> GetUnitsAsync(CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<UnitRow>(UnitSql.Units, null, cancellationToken)).Select(r => r.ToDomain())];

    public Task<IReadOnlyList<MaterialConversionRow>> GetConversionsAsync(IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken) =>
        session.QueryAsync<MaterialConversionRow>(UnitSql.Conversions, new { MaterialIds = materialIds.ToArray() }, cancellationToken);

    public async Task<Versioned<UnitOfMeasure>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<UomRow>(GetForUpdate, new { Id = id }, cancellationToken);
        return row is null ? null : new(new UnitOfMeasure(row.Id, row.Code, row.Name, row.Dimension, row.ToBaseFactor, row.IsActive), row.Version);
    }

    public Task<long> InsertAsync(UnitOfMeasure unit, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            Insert, new { unit.Id, unit.Code, unit.Name, unit.Dimension, unit.ToBaseFactor, unit.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateAsync(UnitOfMeasure unit, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, new { unit.Id, unit.Name, unit.IsActive, At = at }, cancellationToken);

    public async Task<bool> LockMaterialAsync(Guid materialId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<bool?>(LockMaterial, new { Id = materialId }, cancellationToken) == true;

    // A conversion decides how reported output weighs against ideal rates, so every site's OEE rollups roll again (#66).
    public async Task SetConversionAsync(Guid materialId, MaterialConversion conversion, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await session.ExecuteAsync(
            SetConversion,
            new { MaterialId = materialId, conversion.FromUomId, conversion.ToUomId, conversion.Factor, At = at },
            cancellationToken);
        await OeeRollupMarks.SiteChangedAsync(session, null, cancellationToken);
    }

    public async Task<bool> DeactivateConversionAsync(
        Guid materialId, Guid fromUomId, Guid toUomId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var changed = await session.ExecuteAsync(
            Deactivate, new { MaterialId = materialId, FromUomId = fromUomId, ToUomId = toUomId, At = at }, cancellationToken) > 0;
        if (changed)
        {
            await OeeRollupMarks.SiteChangedAsync(session, null, cancellationToken);
        }

        return changed;
    }

    private sealed class UomRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string Dimension { get; init; } = "";
        public decimal? ToBaseFactor { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }
    }
}
