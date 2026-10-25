using Dapper;
using Pulaski.Application.Abstractions;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain.WorkOrders;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.WorkOrders;

/// <summary>Write side for work orders. Every statement runs in the command's transaction.</summary>
internal sealed class WorkOrderRepository(DbSession session) : IWorkOrderRepository
{
    private const string Columns = """
        id, site_id, order_number, material_id, planned_qty, uom_id, target_equipment_id,
        planned_start, planned_end, due_at, priority, status, origin, process_version_id, notes, bom_version_id,
        external_system, external_key, external_version, erp_qty, erp_uom_id, batch_adjustment, quantity_held,
        erp_pending_change::text AS pending_change, imported_at, xmin::text::bigint AS version
        """;

    private const string GetForUpdate = $"SELECT {Columns} FROM core.work_order WHERE id = @Id FOR UPDATE";

    private const string GetForShare = $"SELECT {Columns} FROM core.work_order WHERE id = @Id FOR SHARE";

    private const string OpenJobs = """
        SELECT EXISTS (
            SELECT 1 FROM core.job
            WHERE work_order_id = @Id AND status IN ('planned','dispatched','firmed','running','paused'))
        """;

    private const string Insert = """
        INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id,
                                     target_equipment_id, planned_start, planned_end, due_at, priority,
                                     status, origin, process_version_id, notes, bom_version_id,
                                     external_system, external_key, external_version, erp_qty, erp_uom_id, batch_adjustment, quantity_held,
                                     erp_change_pending, erp_pending_change, imported_at, created_at, updated_at)
        VALUES (@Id, @SiteId, @OrderNumber, @MaterialId, @PlannedQty, @UomId,
                @TargetEquipmentId, @PlannedStart, @PlannedEnd, @DueAt, @Priority,
                @Status, @Origin, @ProcessVersionId, @Notes, @BomVersionId,
                @ExternalSystem, @ExternalKey, @ExternalVersion, @ErpQuantity, @ErpUomId, @BatchAdjustment, @QuantityHeld,
                @PendingChange IS NOT NULL, @PendingChange::jsonb, @ImportedAt, @At, @At)
        RETURNING xmin::text::bigint
        """;

    private const string UpdateFromErp = """
        UPDATE core.work_order
        SET planned_qty = @PlannedQty, uom_id = @UomId, planned_start = @PlannedStart, due_at = @DueAt, priority = @Priority,
            external_version = @ExternalVersion, erp_qty = @ErpQuantity, erp_uom_id = @ErpUomId, batch_adjustment = @BatchAdjustment,
            quantity_held = @QuantityHeld, erp_change_pending = @PendingChange IS NOT NULL, erp_pending_change = @PendingChange::jsonb,
            imported_at = @ImportedAt, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string ByExternalKey = "SELECT id FROM core.work_order WHERE external_system = @System AND external_key = @Key";

    private const string NumberExists = "SELECT EXISTS (SELECT 1 FROM core.work_order WHERE site_id = @SiteId AND order_number = @Number)";

    private const string ErpBatchRule = """
        SELECT coalesce((SELECT erp_batch_rule FROM core.material WHERE id = @MaterialId),
                        (SELECT erp_batch_rule FROM core.scheduling_policy WHERE site_id = @SiteId),
                        'reject')
        """;

    private const string SiteByCode = "SELECT id FROM core.equipment WHERE level = 'site' AND lower(code) = lower(@Code) ORDER BY id LIMIT 2";

    // The named process; else the material's default; else every active process at the site with a released version covering it.
    private const string Processes = """
        WITH released AS (
            SELECT p.id, p.code, p.site_id
            FROM core.process p
            WHERE p.is_active AND p.site_id = @SiteId
              AND EXISTS (SELECT 1 FROM core.process_version v WHERE v.process_id = p.id AND v.status = 'released'))
        SELECT r.id, r.code FROM released r
        WHERE CASE
            WHEN @Code::text IS NOT NULL THEN lower(r.code) = lower(@Code)
            WHEN (SELECT default_process_id FROM core.material WHERE id = @MaterialId) IS NOT NULL
                THEN r.id = (SELECT default_process_id FROM core.material WHERE id = @MaterialId)
            ELSE true END
        ORDER BY r.code
        """;

    private const string UpdateStatus = """
        UPDATE core.work_order
        SET status = @Status,
            updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    public async Task<Versioned<WorkOrder>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<WorkOrderRow>(GetForUpdate, new { Id = id }, cancellationToken);
        return row?.ToVersioned();
    }

    public Task<bool> HasOpenJobsAsync(Guid id, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(OpenJobs, new { Id = id }, cancellationToken);

    public async Task<Versioned<WorkOrder>?> GetForShareAsync(Guid id, CancellationToken cancellationToken)
    {
        var row = await session.QuerySingleOrDefaultAsync<WorkOrderRow>(GetForShare, new { Id = id }, cancellationToken);
        return row?.ToVersioned();
    }

    public Task<long> InsertAsync(WorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(Insert, Parameters(workOrder, at), cancellationToken);

    public Task<long> UpdateFromErpAsync(WorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateFromErp, Parameters(workOrder, at), cancellationToken);

    public Task<Guid?> FindByExternalKeyAsync(string externalSystem, string externalKey, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>(ByExternalKey, new { System = externalSystem, Key = externalKey }, cancellationToken);

    public Task<bool> NumberExistsAsync(Guid siteId, string orderNumber, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(NumberExists, new { SiteId = siteId, Number = orderNumber }, cancellationToken);

    public Task<string> GetErpBatchRuleAsync(Guid siteId, Guid materialId, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<string>(ErpBatchRule, new { SiteId = siteId, MaterialId = materialId }, cancellationToken);

    public Task SetSiteErpBatchRuleAsync(Guid siteId, string rule, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            """
            INSERT INTO core.scheduling_policy (site_id, erp_batch_rule, updated_at) VALUES (@SiteId, @Rule, @At)
            ON CONFLICT (site_id) DO UPDATE SET erp_batch_rule = excluded.erp_batch_rule, updated_at = excluded.updated_at
            """,
            new { SiteId = siteId, Rule = rule, At = at }, cancellationToken);

    public Task<long?> SetMaterialErpBatchRuleAsync(Guid materialId, string? rule, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<long?>(
            "UPDATE core.material SET erp_batch_rule = @Rule, updated_at = @At WHERE id = @Id RETURNING xmin::text::bigint",
            new { Id = materialId, Rule = rule, At = at }, cancellationToken);

    public Task<Guid?> FindMaterialAsync(string code, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM core.material WHERE lower(code) = lower(@Code) AND is_active", new { Code = code }, cancellationToken);

    public Task<Guid?> FindUomAsync(string code, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<Guid?>("SELECT id FROM core.uom WHERE code = @Code AND is_active", new { Code = code }, cancellationToken);

    public Task<string?> GetUomCodeAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<string?>("SELECT code FROM core.uom WHERE id = @Id", new { Id = id }, cancellationToken);

    public async Task<Guid?> FindSiteAsync(string code, CancellationToken cancellationToken) =>
        await session.QueryAsync<Guid>(SiteByCode, new { Code = code }, cancellationToken) is [var only] ? only : null;

    public async Task<IReadOnlyList<(Guid Id, string Code)>> FindProcessesAsync(Guid siteId, Guid materialId, string? processCode, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ProcessRow>(Processes, new { SiteId = siteId, MaterialId = materialId, Code = processCode }, cancellationToken))
            .Select(r => (r.Id, r.Code))];

    private static DynamicParameters Parameters(WorkOrder w, DateTimeOffset at)
    {
        var parameters = new DynamicParameters(new
        {
            w.Id,
            w.SiteId,
            w.OrderNumber,
            w.MaterialId,
            w.PlannedQty,
            w.UomId,
            w.TargetEquipmentId,
            w.PlannedStart,
            w.PlannedEnd,
            w.DueAt,
            w.Priority,
            w.Status,
            w.Origin,
            w.ProcessVersionId,
            w.Notes,
            w.BomVersionId,
            w.ExternalSystem,
            w.ExternalKey,
            w.ExternalVersion,
            w.ErpQuantity,
            w.ErpUomId,
            w.BatchAdjustment,
            w.QuantityHeld,
            w.ImportedAt,
            PendingChange = w.PendingChange is null ? null : System.Text.Json.JsonSerializer.Serialize(w.PendingChange),
        });
        parameters.Add("At", at);
        return parameters;
    }

    private sealed class ProcessRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
    }

    public Task<long> UpdateStatusAsync(Guid id, string status, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateStatus, new { Id = id, Status = status, At = at }, cancellationToken);

    /// <summary>Column shape for Dapper; mapped to the domain record so SQL never shapes the domain.</summary>
    private sealed class WorkOrderRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public string OrderNumber { get; init; } = "";
        public Guid MaterialId { get; init; }
        public decimal PlannedQty { get; init; }
        public Guid UomId { get; init; }
        public Guid? TargetEquipmentId { get; init; }
        public DateTimeOffset? PlannedStart { get; init; }
        public DateTimeOffset? PlannedEnd { get; init; }
        public DateTimeOffset? DueAt { get; init; }
        public int Priority { get; init; }
        public string Status { get; init; } = "";
        public string Origin { get; init; } = "";
        public Guid? ProcessVersionId { get; init; }
        public string? Notes { get; init; }
        public Guid? BomVersionId { get; init; }
        public string? ExternalSystem { get; init; }
        public string? ExternalKey { get; init; }
        public string? ExternalVersion { get; init; }
        public decimal? ErpQty { get; init; }
        public Guid? ErpUomId { get; init; }
        public string? BatchAdjustment { get; init; }
        public bool QuantityHeld { get; init; }
        public string? PendingChange { get; init; }
        public DateTimeOffset? ImportedAt { get; init; }
        public long Version { get; init; }

        public Versioned<WorkOrder> ToVersioned() => new(
            new WorkOrder(
                Id, SiteId, OrderNumber, MaterialId, PlannedQty, UomId, TargetEquipmentId,
                PlannedStart, PlannedEnd, DueAt, Priority, Status, Origin, ProcessVersionId, Notes, BomVersionId)
            {
                ExternalSystem = ExternalSystem,
                ExternalKey = ExternalKey,
                ExternalVersion = ExternalVersion,
                ErpQuantity = ErpQty,
                ErpUomId = ErpUomId,
                BatchAdjustment = BatchAdjustment,
                QuantityHeld = QuantityHeld,
                PendingChange = PendingChange is null ? null : System.Text.Json.JsonSerializer.Deserialize<ErpOrderChange>(PendingChange),
                ImportedAt = ImportedAt,
            },
            Version);
    }
}
