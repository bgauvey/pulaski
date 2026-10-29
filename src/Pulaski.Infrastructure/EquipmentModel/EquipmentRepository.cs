using Dapper;
using Pulaski.Application.Abstractions;
using Pulaski.Application.EquipmentModel;
using Pulaski.Domain.EquipmentModel;
using Pulaski.Infrastructure.Persistence;

using Pulaski.Infrastructure.MachineData;

namespace Pulaski.Infrastructure.EquipmentModel;

/// <summary>Write side for equipment. Every statement runs in the command's transaction.</summary>
internal sealed class EquipmentRepository(DbSession session) : IEquipmentRepository
{
    private const string Columns = """
        id, parent_id, site_id, code, name, description, level, equipment_role,
        tracks_state, is_schedulable, can_run_job, holds_inventory, commingles_lots, single_material, time_zone, is_active, xmin::text::bigint AS version
        """;

    private const string GetForShare = $"SELECT {Columns} FROM core.equipment WHERE id = @Id FOR SHARE";

    private const string GetForUpdate = $"SELECT {Columns} FROM core.equipment WHERE id = @Id FOR UPDATE";

    private const string Insert = """
        INSERT INTO core.equipment (id, parent_id, site_id, code, name, description, level, equipment_role,
                                    tracks_state, is_schedulable, can_run_job, holds_inventory, commingles_lots, single_material, time_zone, is_active,
                                    created_at, updated_at)
        VALUES (@Id, @ParentId, @SiteId, @Code, @Name, @Description, @Level, @EquipmentRole,
                @TracksState, @IsSchedulable, @CanRunJob, @HoldsInventory, @ComminglesLots, @SingleMaterial, @TimeZone, @IsActive,
                @At, @At)
        RETURNING xmin::text::bigint
        """;

    // The new row's ancestors are its parent's ancestors one level further away, plus itself.
    private const string InsertClosure = """
        INSERT INTO core.equipment_closure (ancestor_id, descendant_id, depth)
        SELECT c.ancestor_id, @Id, c.depth + 1
        FROM core.equipment_closure c
        WHERE c.descendant_id = @ParentId
        UNION ALL
        SELECT @Id, @Id, 0
        """;

    private const string Update = """
        UPDATE core.equipment
        SET name = @Name,
            description = @Description,
            tracks_state = @TracksState,
            is_schedulable = @IsSchedulable,
            can_run_job = @CanRunJob,
            holds_inventory = @HoldsInventory,
            commingles_lots = @ComminglesLots,
            single_material = @SingleMaterial,
            is_active = @IsActive,
            updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string SeparateLots = """
        SELECT EXISTS (
            SELECT 1 FROM core.lot
            WHERE location_equipment_id = @Id AND NOT is_bulk AND quantity > 0
              AND status IN ('available','on_hold','quarantine'))
        """;

    private const string OnHandAt = "l.location_equipment_id = @Id AND l.quantity > 0 AND l.status IN ('available','on_hold','quarantine')";

    private const string HoldsStock = $"SELECT EXISTS (SELECT 1 FROM core.lot l WHERE {OnHandAt})";

    private const string MaterialsHeld = $"SELECT count(DISTINCT l.material_id)::int FROM core.lot l WHERE {OnHandAt}";

    private const string InSubtree = """
        SELECT EXISTS (
            SELECT 1 FROM core.equipment_closure
            WHERE ancestor_id = @AncestorId AND descendant_id = @DescendantId)
        """;

    public async Task<Versioned<Equipment>?> GetAsync(Guid id, RowLock rowLock, CancellationToken cancellationToken)
    {
        var sql = rowLock == RowLock.Update ? GetForUpdate : GetForShare;
        var row = await session.QuerySingleOrDefaultAsync<EquipmentRow>(sql, new { Id = id }, cancellationToken);
        return row?.ToVersioned();
    }

    public async Task<long> InsertAsync(Equipment equipment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(Insert, WithTime(equipment, at), cancellationToken);
        await session.ExecuteAsync(InsertClosure, new { equipment.Id, equipment.ParentId }, cancellationToken);
        await OeeRollupMarks.SiteOfEquipmentChangedAsync(session, equipment.Id, cancellationToken);
        return version;
    }

    public async Task<long> UpdateAsync(Equipment equipment, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = await session.ExecuteScalarAsync<long>(Update, WithTime(equipment, at), cancellationToken);
        await OeeRollupMarks.SiteOfEquipmentChangedAsync(session, equipment.Id, cancellationToken);
        return version;
    }

    public Task<bool> HoldsStockAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(HoldsStock, new { Id = equipmentId }, cancellationToken);

    public Task<int> CountMaterialsHeldAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<int>(MaterialsHeld, new { Id = equipmentId }, cancellationToken);

    public Task<bool> HoldsSeparateLotsAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(SeparateLots, new { Id = equipmentId }, cancellationToken);

    public Task<bool> IsInSubtreeAsync(Guid ancestorId, Guid descendantId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(
            InSubtree, new { AncestorId = ancestorId, DescendantId = descendantId }, cancellationToken);

    private static DynamicParameters WithTime(Equipment equipment, DateTimeOffset at)
    {
        var parameters = new DynamicParameters(equipment);
        parameters.Add("At", at);
        return parameters;
    }

    /// <summary>Column shape for Dapper; mapped to the domain record so SQL never shapes the domain.</summary>
    private sealed class EquipmentRow
    {
        public Guid Id { get; init; }
        public Guid? ParentId { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string Level { get; init; } = "";
        public string EquipmentRole { get; init; } = "";
        public bool TracksState { get; init; }
        public bool IsSchedulable { get; init; }
        public bool CanRunJob { get; init; }
        public bool HoldsInventory { get; init; }
        public bool ComminglesLots { get; init; }
        public bool SingleMaterial { get; init; }
        public string? TimeZone { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public Versioned<Equipment> ToVersioned() => new(
            new Equipment(
                Id, ParentId, SiteId, Code, Name, Description, Level, EquipmentRole,
                TracksState, IsSchedulable, HoldsInventory, TimeZone, IsActive, CanRunJob, ComminglesLots, SingleMaterial),
            Version);
    }
}
