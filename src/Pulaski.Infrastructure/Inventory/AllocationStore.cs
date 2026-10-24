using Dapper;
using Npgsql;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>SQL for lot allocations (D-064), shared by the write side (in the command's transaction) and the read side.</summary>
internal static class AllocationSql
{
    public const string Job = """
        SELECT j.id, j.job_number, j.site_id, j.status, coalesce(j.planned_start, j.earliest_start) AS planned_start
        FROM core.job j
        WHERE j.id = @Id AND j.job_type = 'production'
        """;

    public const string Needs = $"""
        SELECT jm.material_id, m.code AS material_code, m.name AS material_name, m.base_uom_id, bu.code AS base_uom_code,
               jm.planned_qty, jm.uom_id,
               coalesce((SELECT -sum(e.qty_delta) FROM ts.lot_event e JOIN core.lot l ON l.id = e.lot_id
                         WHERE e.job_id = jm.job_id AND e.event_kind = 'consumed' AND l.material_id = jm.material_id
                           AND {LotEventSql.NotReversed}), 0) AS consumed_qty
        FROM core.job_material jm
        JOIN core.material m ON m.id = jm.material_id
        JOIN core.uom bu ON bu.id = m.base_uom_id
        WHERE jm.job_id = @Id AND jm.direction = 'consume'
        ORDER BY m.code
        """;

    public const string Columns = """
        a.id, a.site_id, a.job_id, a.material_id, a.lot_id, a.quantity, a.allocated_qty, a.status, a.basis, a.note,
        a.created_at, a.created_by, j.job_number
        """;

    public const string ActiveOfJob = $"""
        SELECT {Columns} FROM core.lot_allocation a JOIN core.job j ON j.id = a.job_id
        WHERE a.job_id = @Id AND a.status = 'active'
        ORDER BY a.created_at, a.id
        """;

    // On-hand lots of the job's components at its site (held ones too, so they can be said to be left out), and any lot the job has
    // an allocation of, with what is set aside from each.
    public const string Lots = """
        SELECT l.id AS lot_id, l.material_id, l.lot_number, l.is_bulk, loc.code AS location_code, l.status, l.quantity, l.expires_at,
               l.created_at AS received_at,
               coalesce((SELECT sum(a.quantity) FROM core.lot_allocation a WHERE a.lot_id = l.id AND a.status = 'active' AND a.job_id <> @Id), 0) AS allocated_to_others,
               coalesce((SELECT sum(a.quantity) FROM core.lot_allocation a WHERE a.lot_id = l.id AND a.status = 'active' AND a.job_id = @Id), 0) AS allocated_to_job
        FROM core.lot l
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        WHERE l.site_id = @SiteId
          AND l.material_id IN (SELECT material_id FROM core.job_material WHERE job_id = @Id AND direction = 'consume')
          AND ((l.status IN ('available','on_hold','quarantine') AND l.quantity > 0)
               OR EXISTS (SELECT 1 FROM core.lot_allocation a WHERE a.lot_id = l.id AND a.job_id = @Id AND a.status = 'active'))
        ORDER BY l.id
        """;

    public const string OfLot = $"""
        SELECT {Columns}, coalesce(l.lot_number, 'Balance at ' || loc.code) AS lot_label, loc.code AS location_code, u.code AS uom_code
        FROM core.lot_allocation a
        JOIN core.job j ON j.id = a.job_id
        JOIN core.lot l ON l.id = a.lot_id
        JOIN core.uom u ON u.id = l.uom_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        WHERE a.lot_id = @Id AND a.status = 'active'
        ORDER BY a.created_at, a.id
        """;

    // What is really set aside: at most what the lot holds, and only of available stock.
    public const string Allocated = """
        SELECT a.job_id, a.material_id, sum(least(a.quantity, l.quantity)) AS quantity
        FROM core.lot_allocation a
        JOIN core.lot l ON l.id = a.lot_id
        WHERE a.site_id = @SiteId AND a.status = 'active' AND l.status = 'available' AND a.material_id = ANY(@Ids)
        GROUP BY a.job_id, a.material_id
        """;

    public const string LockOfLots = $"""
        SELECT {Columns} FROM core.lot_allocation a JOIN core.job j ON j.id = a.job_id
        WHERE a.lot_id = ANY(@Ids) AND a.status = 'active'
        ORDER BY a.id
        FOR UPDATE OF a
        """;

    public const string Get = $"SELECT {Columns} FROM core.lot_allocation a JOIN core.job j ON j.id = a.job_id WHERE a.id = @Id";

    public const string OpenJobs = """
        SELECT j.id FROM core.job j
        WHERE j.site_id = @SiteId AND j.job_type = 'production' AND j.status NOT IN ('completed','closed','cancelled')
          AND EXISTS (SELECT 1 FROM core.job_material jm WHERE jm.job_id = j.id AND jm.direction = 'consume')
        ORDER BY j.id
        """;

    public const string Insert = """
        INSERT INTO core.lot_allocation (id, site_id, job_id, material_id, lot_id, quantity, allocated_qty, status, basis, note,
                                         created_at, created_by, updated_at)
        VALUES (@Id, @SiteId, @JobId, @MaterialId, @LotId, @Quantity, @AllocatedQty, @Status, @Basis, @Note, @At, @Actor, @At)
        """;

    public const string Update = """
        UPDATE core.lot_allocation
        SET quantity = @Quantity, allocated_qty = @AllocatedQty, status = @Status, note = @Note, updated_at = @At,
            ended_at = CASE WHEN @Status = 'active' THEN NULL ELSE coalesce(ended_at, @At) END,
            ended_by = CASE WHEN @Status = 'active' THEN NULL ELSE coalesce(ended_by, @Actor) END
        WHERE id = @Id
        """;

    public static object Args(LotAllocation a, string actor, DateTimeOffset at) => new
    {
        a.Id,
        a.SiteId,
        a.JobId,
        a.MaterialId,
        a.LotId,
        a.Quantity,
        a.AllocatedQty,
        a.Status,
        a.Basis,
        a.Note,
        Actor = actor,
        At = at,
    };

    public static JobAllocationSource Source(JobRow job, IEnumerable<NeedRow> needs, IEnumerable<AllocationRow> allocations, IEnumerable<LotRow> lots) => new(
        new AllocationJob(job.Id, job.JobNumber, job.SiteId, job.Status, job.PlannedStart),
        [.. needs.Select(n => new AllocationNeedRow(n.MaterialId, n.MaterialCode, n.MaterialName, n.BaseUomId, n.BaseUomCode, n.PlannedQty, n.UomId, n.ConsumedQty))],
        [.. allocations.Select(a => a.ToHeld())],
        [.. lots.Select(l => new AllocationLot(
            l.MaterialId,
            new AllocationCandidate(l.LotId, l.LotNumber ?? $"Balance at {l.LocationCode}", l.Status, l.Quantity, l.AllocatedToOthers, l.AllocatedToJob, l.ExpiresAt, l.ReceivedAt),
            l.LocationCode))]);

    internal sealed class JobRow
    {
        public Guid Id { get; init; }
        public string JobNumber { get; init; } = "";
        public Guid SiteId { get; init; }
        public string Status { get; init; } = "";
        public DateTimeOffset? PlannedStart { get; init; }
    }

    internal sealed class NeedRow
    {
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = "";
        public string MaterialName { get; init; } = "";
        public Guid BaseUomId { get; init; }
        public string BaseUomCode { get; init; } = "";
        public decimal PlannedQty { get; init; }
        public Guid UomId { get; init; }
        public decimal ConsumedQty { get; init; }
    }

    internal class AllocationRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid JobId { get; init; }
        public Guid MaterialId { get; init; }
        public Guid LotId { get; init; }
        public decimal Quantity { get; init; }
        public decimal AllocatedQty { get; init; }
        public string Status { get; init; } = "";
        public string Basis { get; init; } = "";
        public string? Note { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public string JobNumber { get; init; } = "";

        public LotAllocation ToDomain() => new(Id, SiteId, JobId, MaterialId, LotId, Quantity, AllocatedQty, Status, Basis, Note);

        public HeldAllocation ToHeld() => new(ToDomain(), JobNumber) { CreatedAt = CreatedAt, CreatedBy = CreatedBy };
    }

    internal sealed class OfLotRow : AllocationRow
    {
        public string LotLabel { get; init; } = "";
        public string? LocationCode { get; init; }
        public string UomCode { get; init; } = "";
    }

    internal sealed class LotRow
    {
        public Guid LotId { get; init; }
        public Guid MaterialId { get; init; }
        public string? LotNumber { get; init; }
        public bool IsBulk { get; init; }
        public string? LocationCode { get; init; }
        public string Status { get; init; } = "";
        public decimal Quantity { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset ReceivedAt { get; init; }
        public decimal AllocatedToOthers { get; init; }
        public decimal AllocatedToJob { get; init; }
    }
}

/// <summary>Write side of allocations (D-064), in the command's transaction.</summary>
internal sealed class AllocationRepository(DbSession session) : IAllocationRepository
{
    public async Task<JobAllocationSource?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        var args = new { Id = jobId };
        if (await session.QuerySingleOrDefaultAsync<AllocationSql.JobRow>(AllocationSql.Job, args, cancellationToken) is not { } job)
        {
            return null;
        }

        return AllocationSql.Source(
            job,
            await session.QueryAsync<AllocationSql.NeedRow>(AllocationSql.Needs, args, cancellationToken),
            await session.QueryAsync<AllocationSql.AllocationRow>(AllocationSql.ActiveOfJob, args, cancellationToken),
            await session.QueryAsync<AllocationSql.LotRow>(AllocationSql.Lots, new { Id = jobId, job.SiteId }, cancellationToken));
    }

    public async Task<IReadOnlyList<HeldAllocation>> LockActiveOfLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<AllocationSql.AllocationRow>(AllocationSql.LockOfLots, new { Ids = lotIds.ToArray() }, cancellationToken)).Select(r => r.ToHeld())];

    public async Task<LotAllocation?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<AllocationSql.AllocationRow>(AllocationSql.Get, new { Id = id }, cancellationToken))?.ToDomain();

    public Task<IReadOnlyList<Guid>> ListOpenJobsAsync(Guid siteId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(AllocationSql.OpenJobs, new { SiteId = siteId }, cancellationToken);

    public Task InsertAsync(LotAllocation allocation, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(AllocationSql.Insert, AllocationSql.Args(allocation, actor, at), cancellationToken);

    public Task UpdateAsync(LotAllocation allocation, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(AllocationSql.Update, AllocationSql.Args(allocation, actor, at), cancellationToken);
}

/// <summary>Read side of allocations (D-064), on a connection of its own.</summary>
internal sealed class AllocationQueries(NpgsqlDataSource dataSource) : IAllocationReadStore
{
    public async Task<JobAllocationSource?> LoadAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { Id = jobId };
        if (await connection.QuerySingleOrDefaultAsync<AllocationSql.JobRow>(new CommandDefinition(AllocationSql.Job, args, cancellationToken: cancellationToken)) is not { } job)
        {
            return null;
        }

        return AllocationSql.Source(
            job,
            await connection.QueryAsync<AllocationSql.NeedRow>(new CommandDefinition(AllocationSql.Needs, args, cancellationToken: cancellationToken)),
            await connection.QueryAsync<AllocationSql.AllocationRow>(new CommandDefinition(AllocationSql.ActiveOfJob, args, cancellationToken: cancellationToken)),
            await connection.QueryAsync<AllocationSql.LotRow>(new CommandDefinition(AllocationSql.Lots, new { Id = jobId, job.SiteId }, cancellationToken: cancellationToken)));
    }

    public async Task<IReadOnlyList<LotAllocationDto>> ListOfLotAsync(Guid lotId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AllocationSql.OfLotRow>(new CommandDefinition(AllocationSql.OfLot, new { Id = lotId }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new LotAllocationDto
        {
            Id = r.Id,
            JobId = r.JobId,
            JobNumber = r.JobNumber,
            MaterialId = r.MaterialId,
            LotId = r.LotId,
            LotLabel = r.LotLabel,
            LocationCode = r.LocationCode,
            Quantity = r.Quantity,
            AllocatedQty = r.AllocatedQty,
            UomCode = r.UomCode,
            Basis = r.Basis,
            Note = r.Note,
            CreatedAt = r.CreatedAt,
            CreatedBy = r.CreatedBy,
        })];
    }

    public async Task<IReadOnlyList<AllocatedStock>> ListAllocatedAsync(Guid siteId, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<AllocatedRow>(
            new CommandDefinition(AllocationSql.Allocated, new { SiteId = siteId, Ids = materialIds.ToArray() }, cancellationToken: cancellationToken));
        return [.. rows.Select(r => new AllocatedStock(r.JobId, r.MaterialId, r.Quantity))];
    }

    private sealed class AllocatedRow
    {
        public Guid JobId { get; init; }
        public Guid MaterialId { get; init; }
        public decimal Quantity { get; init; }
    }
}
