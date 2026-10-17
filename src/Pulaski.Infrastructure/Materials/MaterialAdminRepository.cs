using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;
using Pulaski.Domain.Materials;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Materials;

/// <summary>Write side for materials and classes (D-033). Every statement runs in the command's transaction.</summary>
internal sealed class MaterialAdminRepository(DbSession session) : IMaterialAdminRepository
{
    private const string Columns = """
        id, code, name, description, material_type, material_class_id, base_uom_id, is_lot_tracked, shelf_life_days,
        origin, external_system, external_key, external_version, is_active, xmin::text::bigint AS version
        """;

    private const string GetForUpdate = $"SELECT {Columns} FROM core.material WHERE id = @Id FOR UPDATE";

    private const string FindByExternal = $"SELECT {Columns} FROM core.material WHERE external_system = @System AND external_key = @Key FOR UPDATE";

    private const string FindByCode = $"SELECT {Columns} FROM core.material WHERE code = @Code FOR UPDATE";

    private const string Insert = """
        INSERT INTO core.material (id, code, name, description, material_type, material_class_id, base_uom_id, is_lot_tracked,
                                   shelf_life_days, origin, external_system, external_key, external_version, is_active, created_at, updated_at)
        VALUES (@Id, @Code, @Name, @Description, @MaterialType, @MaterialClassId, @BaseUomId, @IsLotTracked,
                @ShelfLifeDays, @Origin, @ExternalSystem, @ExternalKey, @ExternalVersion, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string Update = """
        UPDATE core.material
        SET code = @Code, name = @Name, description = @Description, material_type = @MaterialType, material_class_id = @MaterialClassId,
            base_uom_id = @BaseUomId, is_lot_tracked = @IsLotTracked, shelf_life_days = @ShelfLifeDays,
            external_version = @ExternalVersion, is_active = @IsActive, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetBatching = "SELECT batch_size AS size, batch_tolerance_pct AS tolerance_pct, batch_rule AS rule FROM core.material WHERE id = @Id";

    private const string UpdateBatching = """
        UPDATE core.material SET batch_size = @Size, batch_tolerance_pct = @TolerancePct, batch_rule = @Rule, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string OpenOrders = """
        SELECT order_number, planned_qty AS quantity, uom_id FROM core.work_order
        WHERE material_id = @Id AND status NOT IN ('completed','closed','cancelled')
        ORDER BY order_number
        """;

    private const string FindUom = "SELECT id, is_active FROM core.uom WHERE code = @Code FOR SHARE";

    private const string UomActive = "SELECT EXISTS (SELECT 1 FROM core.uom WHERE id = @Id AND is_active)";

    private const string ClassColumns = "id, code, name, is_active, xmin::text::bigint AS version";

    private const string GetClass = $"SELECT {ClassColumns} FROM core.material_class WHERE id = @Id FOR SHARE";

    private const string FindClassByCode = $"SELECT {ClassColumns} FROM core.material_class WHERE code = @Code FOR SHARE";

    private const string GetClassForUpdate = $"SELECT {ClassColumns} FROM core.material_class WHERE id = @Id FOR UPDATE";

    private const string InsertClass = """
        INSERT INTO core.material_class (id, code, name, is_active, created_at, updated_at)
        VALUES (@Id, @Code, @Name, @IsActive, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateClass = """
        UPDATE core.material_class SET name = @Name, is_active = @IsActive, updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<Material>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<MaterialRow>(GetForUpdate, new { Id = id }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<Material>?> FindByExternalKeyForUpdateAsync(string externalSystem, string externalKey, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<MaterialRow>(FindByExternal, new { System = externalSystem, Key = externalKey }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<Material>?> FindByCodeForUpdateAsync(string code, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<MaterialRow>(FindByCode, new { Code = code }, cancellationToken))?.ToVersioned();

    public async Task<MaterialClass?> FindClassByCodeAsync(string code, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ClassRow>(FindClassByCode, new { Code = code }, cancellationToken))?.ToDomain();

    public Task<long> InsertAsync(Material material, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Args(material, at), cancellationToken);

    public Task<long> UpdateAsync(Material material, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Update, Args(material, at), cancellationToken);

    public async Task<MaterialUse> GetUseAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<UseRow>(MaterialSql.Use, new { Id = id }, cancellationToken);
        return new MaterialUse(row!.InUse, row.HasStockOnHand);
    }

    public async Task<IReadOnlyList<string>> DescribeUseAsync(Guid id, CancellationToken cancellationToken)
    {
        var u = (await session.QuerySingleOrDefaultAsync<MaterialUsageDto>(MaterialSql.Usage, new { Id = id }, cancellationToken))!;
        var quantity = u.QuantityOnHand.ToString("#,0.######", CultureInfo.InvariantCulture);
        return [.. new[]
        {
            u.LotsOnHand > 0 ? $"{quantity} on hand in {Count(u.LotsOnHand, "lot")}" : null,
            u.OpenWorkOrders > 0 ? Count(u.OpenWorkOrders, "open work order") : null,
            u.OpenJobs > 0 ? Count(u.OpenJobs, "open job") : null,
            u.UsedInReleasedBoms > 0 ? $"a component in {Count(u.UsedInReleasedBoms, "released BOM")}" : null,
            u.Capabilities > 0 ? $"a capability on {Count(u.Capabilities, "line")}" : null,
        }.OfType<string>()];
    }

    public async Task<(decimal? Size, decimal TolerancePct, string? Rule)> GetBatchingAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<BatchingRow>(GetBatching, new { Id = id }, cancellationToken) is { } r ? (r.Size, r.TolerancePct, r.Rule) : (null, 2m, null);

    public Task<long> UpdateBatchingAsync(Guid id, decimal? size, decimal tolerancePct, string? rule, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateBatching, new { Id = id, Size = size, TolerancePct = tolerancePct, Rule = rule, At = at }, cancellationToken);

    public async Task<IReadOnlyList<(string OrderNumber, decimal Quantity, Guid UomId)>> ListOpenOrdersAsync(Guid id, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<OrderRow>(OpenOrders, new { Id = id }, cancellationToken)).Select(o => (o.OrderNumber, o.Quantity, o.UomId))];

    public async Task<(Guid Id, bool IsActive)?> FindUomAsync(string code, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<UomRow>(FindUom, new { Code = code }, cancellationToken) is { } r ? (r.Id, r.IsActive) : null;

    public Task<bool> IsUomActiveAsync(Guid id, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(UomActive, new { Id = id }, cancellationToken);

    public async Task<MaterialClass?> GetClassAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<ClassRow>(GetClass, new { Id = id }, cancellationToken))?.ToDomain();

    public async Task<Versioned<MaterialClass>?> GetClassForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ClassRow>(GetClassForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(r.ToDomain(), r.Version)
            : null;

    public Task<long> InsertClassAsync(MaterialClass materialClass, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertClass, new { materialClass.Id, materialClass.Code, materialClass.Name, materialClass.IsActive, At = at }, cancellationToken);

    public Task<long> UpdateClassAsync(MaterialClass materialClass, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateClass, new { materialClass.Id, materialClass.Name, materialClass.IsActive, At = at }, cancellationToken);

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";

    private static object Args(Material m, DateTimeOffset at) => new
    {
        m.Id,
        m.Code,
        m.Name,
        m.Description,
        m.MaterialType,
        m.MaterialClassId,
        m.BaseUomId,
        m.IsLotTracked,
        m.ShelfLifeDays,
        m.Origin,
        m.ExternalSystem,
        m.ExternalKey,
        m.ExternalVersion,
        m.IsActive,
        At = at,
    };

    private sealed class MaterialRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string MaterialType { get; init; } = "";
        public Guid? MaterialClassId { get; init; }
        public Guid BaseUomId { get; init; }
        public bool IsLotTracked { get; init; }
        public int? ShelfLifeDays { get; init; }
        public string Origin { get; init; } = "";
        public string? ExternalSystem { get; init; }
        public string? ExternalKey { get; init; }
        public string? ExternalVersion { get; init; }
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public Versioned<Material> ToVersioned() => new(
            new Material(Id, Code, Name, Description, MaterialType, MaterialClassId, BaseUomId, IsLotTracked, ShelfLifeDays,
                Origin, ExternalSystem, ExternalKey, ExternalVersion, IsActive),
            Version);
    }

    private sealed class BatchingRow
    {
        public decimal? Size { get; init; }
        public decimal TolerancePct { get; init; }
        public string? Rule { get; init; }
    }

    private sealed class OrderRow
    {
        public string OrderNumber { get; init; } = "";
        public decimal Quantity { get; init; }
        public Guid UomId { get; init; }
    }

    private sealed class UseRow
    {
        public bool InUse { get; init; }
        public bool HasStockOnHand { get; init; }
    }

    private sealed class UomRow
    {
        public Guid Id { get; init; }
        public bool IsActive { get; init; }
    }

    private sealed class ClassRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool IsActive { get; init; }
        public long Version { get; init; }

        public MaterialClass ToDomain() => new(Id, Code, Name, IsActive);
    }
}
