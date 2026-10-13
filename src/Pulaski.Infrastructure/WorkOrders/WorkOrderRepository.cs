using Dapper;
using Pulaski.Application.Abstractions;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain.WorkOrders;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.WorkOrders;

/// <summary>Write side for work orders. Every statement runs in the command's transaction.</summary>
internal sealed class WorkOrderRepository(DbSession session) : IWorkOrderRepository
{
    private const string GetForUpdate = """
        SELECT id, site_id, order_number, material_id, planned_qty, uom_id, target_equipment_id,
               planned_start, planned_end, due_at, priority, status, origin, xmin::text::bigint AS version
        FROM core.work_order
        WHERE id = @Id
        FOR UPDATE
        """;

    private const string Insert = """
        INSERT INTO core.work_order (id, site_id, order_number, material_id, planned_qty, uom_id,
                                     target_equipment_id, planned_start, planned_end, due_at, priority,
                                     status, origin, created_at, updated_at)
        VALUES (@Id, @SiteId, @OrderNumber, @MaterialId, @PlannedQty, @UomId,
                @TargetEquipmentId, @PlannedStart, @PlannedEnd, @DueAt, @Priority,
                @Status, @Origin, @At, @At)
        RETURNING xmin::text::bigint
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

    public Task<long> InsertAsync(WorkOrder workOrder, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var parameters = new DynamicParameters(workOrder);
        parameters.Add("At", at);
        return session.ExecuteScalarAsync<long>(Insert, parameters, cancellationToken);
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
        public long Version { get; init; }

        public Versioned<WorkOrder> ToVersioned() => new(
            new WorkOrder(
                Id, SiteId, OrderNumber, MaterialId, PlannedQty, UomId, TargetEquipmentId,
                PlannedStart, PlannedEnd, DueAt, Priority, Status, Origin),
            Version);
    }
}
