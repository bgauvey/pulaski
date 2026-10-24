using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Write side for inventory (D-027). Every statement runs in the command's transaction.</summary>
internal sealed class InventoryRepository(DbSession session) : IInventoryRepository
{
    // A lot made from another carries its documents (D-056); a link it already has stays as it was.
    private const string CarryDocuments = """
        INSERT INTO core.lot_document_link (document_id, lot_id, via, from_lot_id, linked_at)
        SELECT document_id, @To, @Via, @From, @At FROM core.lot_document_link WHERE lot_id = @From
        ON CONFLICT (document_id, lot_id) DO NOTHING
        """;

    // A part split off has the same attributes, unless it has its own already.
    private const string CarryAttributes = """
        INSERT INTO core.lot_attribute_value (lot_id, attribute_id, value_text, value_number, value_date, value_boolean, updated_at, updated_by)
        SELECT @To, attribute_id, value_text, value_number, value_date, value_boolean, @At, @Actor
        FROM core.lot_attribute_value
        WHERE lot_id = @From AND num_nonnulls(value_text, value_number, value_date, value_boolean) > 0
        ON CONFLICT (lot_id, attribute_id) DO NOTHING
        """;

    private const string LotColumns = """
        id, site_id, material_id, lot_number, status, quantity, uom_id, location_equipment_id AS location_id,
        is_bulk, origin, supplier_lot, expires_at, produced_by_job_id, container_id, xmin::text::bigint AS version
        """;

    private const string GetLotForUpdate = $"SELECT {LotColumns} FROM core.lot WHERE id = @Id FOR UPDATE";

    private const string FindLotForUpdate = $"""
        SELECT {LotColumns} FROM core.lot
        WHERE site_id = @SiteId AND material_id = @MaterialId AND lot_number = @LotNumber
        FOR UPDATE
        """;

    private const string FindBulkForUpdate = $"""
        SELECT {LotColumns} FROM core.lot
        WHERE material_id = @MaterialId AND location_equipment_id = @LocationId AND is_bulk
        FOR UPDATE
        """;

    private const string InsertLot = """
        INSERT INTO core.lot (id, site_id, lot_number, material_id, status, quantity, uom_id, location_equipment_id, is_bulk,
                              produced_by_job_id, origin, supplier_lot, expires_at, container_id, created_at, updated_at)
        VALUES (@Id, @SiteId, @LotNumber, @MaterialId, @Status, @Quantity, @UomId, @LocationId, @IsBulk,
                @ProducedByJobId, @Origin, @SupplierLot, @ExpiresAt, @ContainerId, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateLot = """
        UPDATE core.lot
        SET lot_number = @LotNumber, status = @Status, quantity = @Quantity, location_equipment_id = @LocationId,
            supplier_lot = @SupplierLot, expires_at = @ExpiresAt, container_id = @ContainerId, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertEvent = """
        INSERT INTO ts.lot_event (id, occurred_at, lot_id, event_kind, qty_delta, qty_after, uom_id,
                                  from_location_id, to_location_id, related_lot_id, job_id,
                                  from_status, to_status, reason_code_id, comment, actor_id,
                                  reference, reverses_event_id, reverses_occurred_at, run_id, effective_at, container_id)
        VALUES (@Id, @At, @LotId, @Kind, @QtyDelta, @QtyAfter, @UomId,
                @FromLocationId, @ToLocationId, @RelatedLotId, @JobId,
                @FromStatus, @ToStatus, @ReasonCodeId, @Comment, @ActorId,
                @Reference, @ReversesEventId, @ReversesOccurredAt, @RunId, coalesce(@EffectiveAt, @At), @ContainerId)
        """;

    private const string GetLocation = """
        SELECT id, code, site_id, holds_inventory, commingles_lots, single_material, is_active FROM core.equipment WHERE id = @Id FOR SHARE
        """;

    private const string GetMaterial = """
        SELECT m.id, m.code, m.base_uom_id, u.code AS base_uom_code, m.is_lot_tracked, m.is_active, m.shelf_life_days,
               m.batch_size, m.batch_tolerance_pct, m.batch_rule
        FROM core.material m
        JOIN core.uom u ON u.id = m.base_uom_id
        WHERE m.id = @Id
        FOR SHARE OF m
        """;

    private const string GetMaterialForUpdate = """
        SELECT m.id, m.code, m.base_uom_id, u.code AS base_uom_code, m.is_lot_tracked, m.is_active, m.shelf_life_days,
               m.xmin::text::bigint AS version
        FROM core.material m
        JOIN core.uom u ON u.id = m.base_uom_id
        WHERE m.id = @Id
        FOR UPDATE OF m
        """;

    private const string UpdateShelfLife = """
        UPDATE core.material SET shelf_life_days = @Days, updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint
        """;

    private const string LockLocation = "SELECT 1 FROM core.equipment WHERE id = @Id FOR UPDATE";

    private const string OtherMaterialAt = """
        SELECT m.code
        FROM core.lot l
        JOIN core.material m ON m.id = l.material_id
        WHERE l.location_equipment_id = @LocationId AND l.material_id <> @MaterialId
          AND l.quantity > 0 AND l.status IN ('available','on_hold','quarantine')
        ORDER BY m.code
        LIMIT 1
        """;

    private const string GetJobForUpdate = """
        SELECT j.id, j.site_id, j.job_number, j.job_type, j.status, j.material_id,
               j.equipment_id, e.code AS line_code, j.planned_qty, j.uom_id
        FROM core.job j
        JOIN core.equipment e ON e.id = j.equipment_id
        WHERE j.id = @Id
        FOR UPDATE OF j
        """;

    private const string LotsOfJob = """
        SELECT DISTINCT lot_id FROM ts.lot_event WHERE job_id = @JobId AND event_kind = @Kind
        """;

    private const string InsertGenealogy = """
        INSERT INTO core.lot_genealogy (id, parent_lot_id, child_lot_id, link_basis, job_id, derived_at, effective_at)
        VALUES (@Id, @ParentLotId, @ChildLotId, @Basis, @JobId, @At, coalesce(@EffectiveAt, @At))
        ON CONFLICT (parent_lot_id, child_lot_id, link_basis) WHERE superseded_at IS NULL DO NOTHING
        """;

    private const string GetReason = """
        SELECT id, site_id, reason_kind AS kind, code, name, is_selectable, requires_comment, is_active
        FROM core.reason_code WHERE id = @Id
        """;

    private const string LockLots = $"""
        SELECT {LotColumns}, updated_at FROM core.lot WHERE id = ANY(@Ids) ORDER BY id FOR UPDATE
        """;

    private const string LatestEvent = """
        SELECT id, occurred_at, event_kind AS kind, qty_delta, qty_after, from_location_id, to_location_id,
               from_status, to_status, related_lot_id, effective_at
        FROM ts.lot_event
        WHERE lot_id = @LotId
        ORDER BY occurred_at DESC, id DESC
        LIMIT 1
        """;

    private const string QuantityTimeline = """
        SELECT effective_at AS effectiveat, occurred_at AS occurredat, qty_delta AS delta
        FROM ts.lot_event
        WHERE lot_id = @LotId AND qty_delta IS NOT NULL
        ORDER BY effective_at, occurred_at, id
        """;

    private const string LatestCountOfLot = """
        SELECT c.posted_at
        FROM core.stock_count c JOIN core.stock_count_line x ON x.stock_count_id = c.id
        WHERE x.lot_id = @LotId AND c.status = 'posted'
        ORDER BY c.posted_at DESC
        LIMIT 1
        """;

    private const string OnHandAt = $"""
        SELECT {LotColumns} FROM core.lot
        WHERE location_equipment_id = @LocationId AND status IN ('available','on_hold','quarantine') AND quantity > 0
        ORDER BY lot_number
        """;

    private const string CountColumns = """
        id, site_id, count_number AS number, location_equipment_id AS location_id, status, note, xmin::text::bigint AS version
        """;

    private const string GetCountForUpdate = $"SELECT {CountColumns} FROM core.stock_count WHERE id = @Id FOR UPDATE";

    private const string CountsOnDay = """
        SELECT count(*)::int FROM core.stock_count
        WHERE site_id = @SiteId AND created_at >= @From AND created_at < @Until
        """;

    private const string InsertCount = """
        INSERT INTO core.stock_count (id, site_id, count_number, location_equipment_id, status, note, created_by, created_at, updated_at)
        VALUES (@Id, @SiteId, @Number, @LocationId, @Status, @Note, @Actor, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateCount = """
        UPDATE core.stock_count
        SET status = @Status, note = @Note, updated_at = @At,
            posted_at = CASE WHEN @Status = 'posted' THEN @At END,
            posted_by = CASE WHEN @Status = 'posted' THEN @Actor END
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string InsertCountLine = """
        INSERT INTO core.stock_count_line (id, stock_count_id, lot_id, expected_qty)
        VALUES (@Id, @CountId, @LotId, @ExpectedQty)
        """;

    private const string CountLines = """
        SELECT id, stock_count_id AS count_id, lot_id, expected_qty, counted_qty, counted_at, counted_by
        FROM core.stock_count_line
        WHERE stock_count_id = @CountId
        """;

    private const string UpdateCountLine = """
        UPDATE core.stock_count_line SET counted_qty = @CountedQty, counted_at = @CountedAt, counted_by = @CountedBy WHERE id = @Id
        """;

    private const string GetReasonCodeForUpdate = """
        SELECT id, site_id, reason_kind AS kind, code, name, requires_comment, sort_order, is_active, utilization_class, xmin::text::bigint AS version
        FROM core.reason_code WHERE id = @Id FOR UPDATE
        """;

    private const string InsertReasonCode = """
        INSERT INTO core.reason_code (id, site_id, reason_kind, code, name, requires_comment, sort_order, is_active, utilization_class)
        VALUES (@Id, @SiteId, @Kind, @Code, @Name, @RequiresComment, @SortOrder, @IsActive, @UtilizationClass)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateReasonCode = """
        UPDATE core.reason_code SET name = @Name, requires_comment = @RequiresComment, sort_order = @SortOrder, is_active = @IsActive,
            utilization_class = @UtilizationClass
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string GetFlow = """
        SELECT id AS equipment_id, site_id, code, holds_inventory, flow_model, min_residence, xmin::text::bigint AS version
        FROM core.equipment WHERE id = @Id FOR UPDATE
        """;

    private const string UpdateFlow = """
        UPDATE core.equipment SET flow_model = @FlowModel, min_residence = @MinResidence, updated_at = @At
        WHERE id = @EquipmentId
        RETURNING xmin::text::bigint
        """;

    private const string MaterialByCode = "SELECT id FROM core.material WHERE code = @Code";

    private const string UomByCode = "SELECT id FROM core.uom WHERE code = @Code";

    private const string IsSite = "SELECT EXISTS (SELECT 1 FROM core.equipment WHERE id = @Id AND level = 'site' FOR SHARE)";

    private const string PlansComponent = """
        SELECT EXISTS (SELECT 1 FROM core.job_material WHERE job_id = @JobId AND material_id = @MaterialId AND direction = 'consume')
        """;

    public async Task<Versioned<Lot>?> GetLotForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<LotRow>(GetLotForUpdate, new { Id = id }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<Lot>?> FindLotForUpdateAsync(Guid siteId, Guid materialId, string lotNumber, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<LotRow>(
            FindLotForUpdate, new { SiteId = siteId, MaterialId = materialId, LotNumber = lotNumber }, cancellationToken))?.ToVersioned();

    public async Task<Versioned<Lot>?> FindBulkLotForUpdateAsync(Guid materialId, Guid locationId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<LotRow>(
            FindBulkForUpdate, new { MaterialId = materialId, LocationId = locationId }, cancellationToken))?.ToVersioned();

    public Task<long> InsertLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            InsertLot,
            new
            {
                lot.Id,
                lot.SiteId,
                lot.LotNumber,
                lot.MaterialId,
                lot.Status,
                lot.Quantity,
                lot.UomId,
                lot.LocationId,
                lot.IsBulk,
                lot.ProducedByJobId,
                lot.Origin,
                lot.SupplierLot,
                lot.ExpiresAt,
                lot.ContainerId,
                At = at,
            },
            cancellationToken);

    public Task<long> UpdateLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            UpdateLot, new { lot.Id, lot.LotNumber, lot.Status, lot.Quantity, lot.LocationId, lot.SupplierLot, lot.ExpiresAt, lot.ContainerId, At = at }, cancellationToken);

    public Task InsertEventAsync(LotEventRow row, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            InsertEvent,
            new
            {
                row.Id,
                At = at,
                row.LotId,
                row.Movement.Kind,
                row.Movement.QtyDelta,
                row.Movement.QtyAfter,
                row.UomId,
                row.Movement.FromLocationId,
                row.Movement.ToLocationId,
                row.RelatedLotId,
                row.JobId,
                row.Movement.FromStatus,
                row.Movement.ToStatus,
                row.ReasonCodeId,
                row.Comment,
                row.ActorId,
                row.Reference,
                row.ReversesEventId,
                row.EffectiveAt,
                row.ContainerId,
                row.ReversesOccurredAt,
                row.RunId,
            },
            cancellationToken);

    public Task<InventoryLocation?> GetLocationAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<InventoryLocation>(GetLocation, new { Id = id }, cancellationToken);

    public Task<InventoryMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<InventoryMaterial>(GetMaterial, new { Id = id }, cancellationToken);

    public Task<InventoryJob?> GetJobForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<InventoryJob>(GetJobForUpdate, new { Id = id }, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListLotsProducedByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LotsOfJob, new { JobId = jobId, Kind = LotEventKind.Produced }, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListLotsConsumedByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LotsOfJob, new { JobId = jobId, Kind = LotEventKind.Consumed }, cancellationToken);

    public async Task InsertGenealogyAsync(IReadOnlyList<GenealogyEdge> edges, DateTimeOffset at, CancellationToken cancellationToken, DateTimeOffset? effectiveAt = null)
    {
        foreach (var edge in edges)
        {
            await session.ExecuteAsync(
                InsertGenealogy, new { edge.Id, edge.ParentLotId, edge.ChildLotId, edge.JobId, edge.Basis, At = at, EffectiveAt = effectiveAt }, cancellationToken);
        }
    }

    public Task<IReadOnlyList<QuantityPoint>> ListQuantityTimelineAsync(Guid lotId, CancellationToken cancellationToken) =>
        session.QueryAsync<QuantityPoint>(QuantityTimeline, new { LotId = lotId }, cancellationToken);

    public Task<DateTimeOffset?> LatestCountOfLotAsync(Guid lotId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<DateTimeOffset?>(LatestCountOfLot, new { LotId = lotId }, cancellationToken);

    public async Task CarryLotRecordsAsync(Guid fromLotId, Guid toLotId, string via, bool withAttributes, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var args = new { From = fromLotId, To = toLotId, Via = via, Actor = actor, At = at };
        await session.ExecuteAsync(CarryDocuments, args, cancellationToken);
        if (withAttributes)
        {
            await session.ExecuteAsync(CarryAttributes, args, cancellationToken);
        }
    }

    public Task<InventoryReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<InventoryReason>(GetReason, new { Id = id }, cancellationToken);

    public async Task<IReadOnlyList<LockedLot>> LockLotsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LotRow>(LockLots, new { Ids = ids.ToArray() }, cancellationToken))
            .Select(r => new LockedLot(r.ToVersioned().Value, r.Version, r.UpdatedAt))];

    public async Task<LotEventRecord?> GetLatestEventAsync(Guid lotId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<EventRow>(LatestEvent, new { LotId = lotId }, cancellationToken) is { } row
            ? new LotEventRecord(
                row.Id,
                row.OccurredAt,
                new LotMovement(row.Kind, row.QtyDelta, row.QtyAfter ?? 0m, row.FromLocationId, row.ToLocationId, row.FromStatus, row.ToStatus),
                row.RelatedLotId,
                row.EffectiveAt)
            : null;

    public async Task<IReadOnlyList<Lot>> ListOnHandLotsAtAsync(Guid locationId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<LotRow>(OnHandAt, new { LocationId = locationId }, cancellationToken)).Select(r => r.ToVersioned().Value)];

    public async Task<Versioned<StockCount>?> GetStockCountForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<CountRow>(GetCountForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(new StockCount(r.Id, r.SiteId, r.Number, r.LocationId, r.Status, r.Note), r.Version)
            : null;

    public Task<int> CountStockCountsOnAsync(Guid siteId, DateTimeOffset day, CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(day.UtcDateTime.Date, TimeSpan.Zero);
        return session.ExecuteScalarAsync<int>(CountsOnDay, new { SiteId = siteId, From = from, Until = from.AddDays(1) }, cancellationToken);
    }

    public Task<long> InsertStockCountAsync(StockCount count, string createdBy, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(
            InsertCount, new { count.Id, count.SiteId, count.Number, count.LocationId, count.Status, count.Note, Actor = createdBy, At = at }, cancellationToken);

    public Task<long> UpdateStockCountAsync(StockCount count, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateCount, new { count.Id, count.Status, count.Note, Actor = actor, At = at }, cancellationToken);

    public async Task InsertStockCountLinesAsync(IReadOnlyList<StockCountLine> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            await session.ExecuteAsync(InsertCountLine, new { line.Id, line.CountId, line.LotId, line.ExpectedQty }, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<StockCountLine>> GetStockCountLinesAsync(Guid countId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<CountLineRow>(CountLines, new { CountId = countId }, cancellationToken))
            .Select(r => new StockCountLine(r.Id, r.CountId, r.LotId, r.ExpectedQty, r.CountedQty, r.CountedAt, r.CountedBy))];

    public Task UpdateStockCountLineAsync(StockCountLine line, CancellationToken cancellationToken) =>
        session.ExecuteAsync(UpdateCountLine, new { line.Id, line.CountedQty, line.CountedAt, line.CountedBy }, cancellationToken);

    public async Task<Versioned<ReasonCode>?> GetReasonCodeForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ReasonRow>(GetReasonCodeForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(new ReasonCode(r.Id, r.SiteId, r.Kind, r.Code, r.Name, r.RequiresComment, r.SortOrder, r.IsActive, r.UtilizationClass), r.Version)
            : null;

    public Task<long> InsertReasonCodeAsync(ReasonCode reason, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(InsertReasonCode, reason, cancellationToken);

    public Task<long> UpdateReasonCodeAsync(ReasonCode reason, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateReasonCode, reason, cancellationToken);

    public Task LockLocationAsync(Guid id, CancellationToken cancellationToken) =>
        session.ExecuteAsync(LockLocation, new { Id = id }, cancellationToken);

    public Task<string?> OtherMaterialAtAsync(Guid locationId, Guid materialId, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<string>(OtherMaterialAt, new { LocationId = locationId, MaterialId = materialId }, cancellationToken);

    public async Task<Versioned<InventoryMaterial>?> GetMaterialForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<MaterialRow>(GetMaterialForUpdate, new { Id = id }, cancellationToken) is { } r
            ? new(new InventoryMaterial
            {
                Id = r.Id,
                Code = r.Code,
                BaseUomId = r.BaseUomId,
                BaseUomCode = r.BaseUomCode,
                IsLotTracked = r.IsLotTracked,
                IsActive = r.IsActive,
                ShelfLifeDays = r.ShelfLifeDays,
            }, r.Version)
            : null;

    public Task<long> UpdateShelfLifeAsync(Guid materialId, int? shelfLifeDays, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateShelfLife, new { Id = materialId, Days = shelfLifeDays, At = at }, cancellationToken);

    public async Task<Versioned<FlowSettings>?> GetFlowSettingsForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<FlowRow>(GetFlow, new { Id = equipmentId }, cancellationToken) is { } r
            ? new Versioned<FlowSettings>(new FlowSettings(r.EquipmentId, r.SiteId, r.Code, r.HoldsInventory, r.FlowModel, r.MinResidence), r.Version)
            : null;

    public Task<long> UpdateFlowSettingsAsync(FlowSettings settings, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateFlow, new { settings.EquipmentId, settings.FlowModel, settings.MinResidence, At = at }, cancellationToken);

    private sealed class FlowRow
    {
        public Guid EquipmentId { get; init; }
        public Guid? SiteId { get; init; }
        public string Code { get; init; } = "";
        public bool HoldsInventory { get; init; }
        public string FlowModel { get; init; } = "mixed";
        public TimeSpan? MinResidence { get; init; }
        public long Version { get; init; }
    }

    public Task<Guid?> FindMaterialIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(MaterialByCode, new { Code = code }, cancellationToken);

    public Task<Guid?> FindUomIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(UomByCode, new { Code = code }, cancellationToken);

    public Task<bool> IsSiteAsync(Guid id, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsSite, new { Id = id }, cancellationToken);

    public Task<bool> PlansComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(PlansComponent, new { JobId = jobId, MaterialId = materialId }, cancellationToken);

    private sealed class LotRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid MaterialId { get; init; }
        public string? LotNumber { get; init; }
        public string Status { get; init; } = "";
        public decimal Quantity { get; init; }
        public Guid UomId { get; init; }
        public Guid? LocationId { get; init; }
        public bool IsBulk { get; init; }
        public string Origin { get; init; } = "";
        public string? SupplierLot { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public Guid? ProducedByJobId { get; init; }
        public Guid? ContainerId { get; init; }
        public long Version { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }

        public Versioned<Lot> ToVersioned() => new(
            new Lot(Id, SiteId, MaterialId, LotNumber, Status, Quantity, UomId, LocationId, IsBulk, Origin, SupplierLot, ExpiresAt, ProducedByJobId)
            {
                ContainerId = ContainerId,
            },
            Version);
    }

    private sealed class MaterialRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public Guid BaseUomId { get; init; }
        public string BaseUomCode { get; init; } = "";
        public bool IsLotTracked { get; init; }
        public bool IsActive { get; init; }
        public int? ShelfLifeDays { get; init; }
        public long Version { get; init; }
    }

    private sealed class EventRow
    {
        public DateTimeOffset? EffectiveAt { get; init; }

        public Guid Id { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public string Kind { get; init; } = "";
        public decimal? QtyDelta { get; init; }
        public decimal? QtyAfter { get; init; }
        public Guid? FromLocationId { get; init; }
        public Guid? ToLocationId { get; init; }
        public string? FromStatus { get; init; }
        public string? ToStatus { get; init; }
        public Guid? RelatedLotId { get; init; }
    }

    private sealed class CountRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string Number { get; init; } = "";
        public Guid LocationId { get; init; }
        public string Status { get; init; } = "";
        public string? Note { get; init; }
        public long Version { get; init; }
    }

    private sealed class CountLineRow
    {
        public Guid Id { get; init; }
        public Guid CountId { get; init; }
        public Guid LotId { get; init; }
        public decimal ExpectedQty { get; init; }
        public decimal? CountedQty { get; init; }
        public DateTimeOffset? CountedAt { get; init; }
        public string? CountedBy { get; init; }
    }

    private sealed class ReasonRow
    {
        public Guid Id { get; init; }
        public Guid? SiteId { get; init; }
        public string Kind { get; init; } = "";
        public string Code { get; init; } = "";
        public string Name { get; init; } = "";
        public bool RequiresComment { get; init; }
        public int SortOrder { get; init; }
        public bool IsActive { get; init; }
        public string? UtilizationClass { get; init; }
        public long Version { get; init; }
    }
}
