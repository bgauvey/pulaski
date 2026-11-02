using Dapper;
using Npgsql;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
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
        a.for_material_id, a.ratio, a.created_at, a.created_by, j.job_number
        """;

    public const string ActiveOfJob = $"""
        SELECT {Columns} FROM core.lot_allocation a JOIN core.job j ON j.id = a.job_id
        WHERE a.job_id = @Id AND a.status = 'active'
        ORDER BY a.created_at, a.id
        """;

    // The job's components and their approved alternates (#82), from the BOM lines its components were planned from.
    private const string Materials = """
        SELECT material_id FROM core.job_material WHERE job_id = @Id AND direction = 'consume'
        UNION
        SELECT a.material_id FROM core.job_material jm JOIN core.bom_item_alternate a ON a.bom_item_id = jm.bom_item_id
        WHERE jm.job_id = @Id AND jm.direction = 'consume'
        """;

    // On-hand lots of the job's components and alternates at its site (held ones too, so they can be said to be left out), and any lot
    // the job has an allocation of, with what is set aside from each; whether it is already at the job's line or in a container (#82).
    public const string Lots = $"""
        SELECT l.id AS lot_id, l.material_id, l.lot_number, l.is_bulk, loc.code AS location_code, l.status, l.quantity, l.expires_at,
               l.created_at AS received_at, l.container_id IS NOT NULL AS in_container,
               EXISTS (SELECT 1 FROM core.equipment_closure c
                       WHERE c.ancestor_id = (SELECT equipment_id FROM core.job WHERE id = @Id) AND c.descendant_id = l.location_equipment_id) AS near_line,
               coalesce((SELECT sum(a.quantity) FROM core.lot_allocation a WHERE a.lot_id = l.id AND a.status = 'active' AND a.job_id <> @Id), 0) AS allocated_to_others,
               coalesce((SELECT sum(a.quantity) FROM core.lot_allocation a WHERE a.lot_id = l.id AND a.status = 'active' AND a.job_id = @Id), 0) AS allocated_to_job
        FROM core.lot l
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        WHERE l.site_id = @SiteId
          AND l.material_id IN ({Materials})
          AND ((l.status IN ('available','on_hold','quarantine') AND l.quantity > 0)
               OR EXISTS (SELECT 1 FROM core.lot_allocation a WHERE a.lot_id = l.id AND a.job_id = @Id AND a.status = 'active'))
        ORDER BY l.id
        """;

    public const string Alternates = """
        SELECT DISTINCT jm.material_id AS component_id, a.material_id, m.code AS material_code, m.base_uom_id, bu.code AS base_uom_code, a.ratio, a.priority
        FROM core.job_material jm
        JOIN core.bom_item_alternate a ON a.bom_item_id = jm.bom_item_id
        JOIN core.material m ON m.id = a.material_id
        JOIN core.uom bu ON bu.id = m.base_uom_id
        WHERE jm.job_id = @Id AND jm.direction = 'consume' AND a.material_id <> jm.material_id
        ORDER BY jm.material_id, a.priority, m.code
        """;

    public const string Preferences = "SELECT preferences FROM core.allocation_settings WHERE site_id = @SiteId";

    public const string Settings = """
        SELECT s.site_id, s.keep_current, s.hard_reservation, s.preferences, r.refreshed_at, s.xmin::text::bigint AS version
        FROM core.allocation_settings s
        LEFT JOIN core.allocation_refresh r ON r.site_id = s.site_id
        WHERE s.site_id = @SiteId
        """;

    public const string EnsureSettings = "INSERT INTO core.allocation_settings (site_id) VALUES (@SiteId) ON CONFLICT (site_id) DO NOTHING RETURNING true";

    public const string SettingsForUpdate = """
        SELECT s.site_id, s.keep_current, s.hard_reservation, s.preferences, r.refreshed_at, s.xmin::text::bigint AS version
        FROM core.allocation_settings s
        LEFT JOIN core.allocation_refresh r ON r.site_id = s.site_id
        WHERE s.site_id = @SiteId
        FOR UPDATE OF s
        """;

    public const string UpdateSettings = """
        UPDATE core.allocation_settings
        SET keep_current = @KeepCurrent, hard_reservation = @HardReservation, preferences = @Preferences, updated_at = @At
        WHERE site_id = @SiteId
        RETURNING xmin::text::bigint
        """;

    // Sites keeping allocations current: never refreshed, or with a stock, job or order change since (allocation events aside, which
    // refreshing writes itself) that has been quiet long enough, or whose last refresh is older than the longest wait.
    public const string DueForRefresh = """
        SELECT s.site_id
        FROM core.allocation_settings s
        LEFT JOIN core.allocation_refresh r ON r.site_id = s.site_id
        LEFT JOIN LATERAL (
            SELECT max(e.recorded_at) AS changed_at
            FROM evt.event e
            WHERE e.recorded_at > r.refreshed_at
              AND (e.type LIKE 'pulaski.lot.%' OR e.type LIKE 'pulaski.job.%' OR e.type LIKE 'pulaski.work_order.%')
              AND e.type NOT LIKE 'pulaski.lot.allocat%') c ON true
        WHERE s.keep_current
          AND (r.refreshed_at IS NULL
               OR (c.changed_at IS NOT NULL AND (c.changed_at < now() - @Quiet OR r.refreshed_at < now() - @AtMost)))
        ORDER BY s.site_id
        """;

    public const string MarkRefreshed = """
        INSERT INTO core.allocation_refresh (site_id, refreshed_at) VALUES (@SiteId, @At)
        ON CONFLICT (site_id) DO UPDATE SET refreshed_at = excluded.refreshed_at
        """;

    // What to fetch for open jobs' allocations, nearest the store first; stock already at the job's line needs no picking.
    public const string Picking = """
        SELECT j.id AS job_id, j.job_number, e.code AS line_code, coalesce(j.planned_start, j.earliest_start) AS needed_from,
               cm.code AS component_code, m.code AS material_code, m.name AS material_name, l.id AS lot_id,
               coalesce(l.lot_number, 'Balance at ' || loc.code) AS lot_label, loc.code AS location_code, ct.code AS container_code,
               least(a.quantity, l.quantity) AS quantity, u.code AS uom_code
        FROM core.lot_allocation a
        JOIN core.job j ON j.id = a.job_id
        JOIN core.equipment e ON e.id = j.equipment_id
        JOIN core.lot l ON l.id = a.lot_id
        JOIN core.material m ON m.id = a.material_id
        JOIN core.material cm ON cm.id = coalesce(a.for_material_id, a.material_id)
        JOIN core.uom u ON u.id = l.uom_id
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        LEFT JOIN core.container ct ON ct.id = l.container_id
        WHERE a.site_id = @SiteId AND a.status = 'active' AND j.status NOT IN ('completed','closed','cancelled')
          AND (@JobId::uuid IS NULL OR j.id = @JobId)
          AND (@LineId::uuid IS NULL OR EXISTS (SELECT 1 FROM core.equipment_closure c WHERE c.ancestor_id = @LineId AND c.descendant_id = j.equipment_id))
          AND least(a.quantity, l.quantity) > 0
          AND NOT EXISTS (SELECT 1 FROM core.equipment_closure c WHERE c.ancestor_id = j.equipment_id AND c.descendant_id = l.location_equipment_id)
        ORDER BY loc.code NULLS LAST, ct.code NULLS FIRST, lot_label, j.job_number, cm.code
        """;

    public const string PickingSite = """
        SELECT s.code, s.time_zone,
               coalesce((SELECT j.job_number FROM core.job j WHERE j.id = @JobId::uuid), (SELECT e.code FROM core.equipment e WHERE e.id = @LineId::uuid)) AS scope
        FROM core.equipment s
        WHERE s.id = @SiteId AND s.level = 'site'
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

    // What is really set aside: at most what the lot holds, and only of available stock; what that covers of the component, an
    // alternate's at its ratio (#82), and what it takes of the lot's own material.
    public const string Allocated = """
        SELECT a.job_id, coalesce(a.for_material_id, a.material_id) AS material_id,
               sum(CASE WHEN a.ratio IS NULL THEN least(a.quantity, l.quantity) ELSE round(least(a.quantity, l.quantity) / a.ratio, 6) END) AS quantity,
               a.material_id AS lot_material_id, sum(least(a.quantity, l.quantity)) AS lot_quantity
        FROM core.lot_allocation a
        JOIN core.lot l ON l.id = a.lot_id
        WHERE a.site_id = @SiteId AND a.status = 'active' AND l.status = 'available'
          AND (a.material_id = ANY(@Ids) OR a.for_material_id = ANY(@Ids))
        GROUP BY a.job_id, coalesce(a.for_material_id, a.material_id), a.material_id
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
                                         for_material_id, ratio, created_at, created_by, updated_at)
        VALUES (@Id, @SiteId, @JobId, @MaterialId, @LotId, @Quantity, @AllocatedQty, @Status, @Basis, @Note,
                @ForMaterialId, @Ratio, @At, @Actor, @At)
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
        a.ForMaterialId,
        a.Ratio,
        Actor = actor,
        At = at,
    };

    public static JobAllocationSource Source(
        JobRow job,
        IEnumerable<NeedRow> needs,
        IEnumerable<AllocationRow> allocations,
        IEnumerable<LotRow> lots,
        IEnumerable<AlternateRow> alternates,
        string[]? preferences) => new(
        new AllocationJob(job.Id, job.JobNumber, job.SiteId, job.Status, job.PlannedStart),
        [.. needs.Select(n => new AllocationNeedRow(n.MaterialId, n.MaterialCode, n.MaterialName, n.BaseUomId, n.BaseUomCode, n.PlannedQty, n.UomId, n.ConsumedQty))],
        [.. allocations.Select(a => a.ToHeld())],
        [.. lots.Select(l => new AllocationLot(
            l.MaterialId,
            new AllocationCandidate(
                l.LotId,
                l.LotNumber ?? $"Balance at {l.LocationCode}",
                l.Status,
                l.Quantity,
                l.AllocatedToOthers,
                l.AllocatedToJob,
                l.ExpiresAt,
                l.ReceivedAt,
                l.NearLine,
                l.InContainer),
            l.LocationCode))],
        [.. alternates.Select(a => new AllocationAlternate(a.ComponentId, a.MaterialId, a.MaterialCode, a.BaseUomId, a.BaseUomCode, a.Ratio, a.Priority))],
        preferences ?? []);

    internal sealed class AlternateRow
    {
        public Guid ComponentId { get; init; }
        public Guid MaterialId { get; init; }
        public string MaterialCode { get; init; } = "";
        public Guid BaseUomId { get; init; }
        public string BaseUomCode { get; init; } = "";
        public decimal Ratio { get; init; }
        public int Priority { get; init; }
    }

    internal sealed class SettingsRow
    {
        public Guid SiteId { get; init; }
        public bool KeepCurrent { get; init; }
        public bool HardReservation { get; init; }
        public string[] Preferences { get; init; } = [];
        public DateTimeOffset? RefreshedAt { get; init; }
        public long Version { get; init; }

        public AllocationSettings ToSettings() => new(SiteId, KeepCurrent, HardReservation, Preferences, RefreshedAt);
    }

    internal sealed class PickingSiteRow
    {
        public string Code { get; init; } = "";
        public string TimeZone { get; init; } = "UTC";
        public string? Scope { get; init; }
    }

    internal sealed class PickingRow
    {
        public Guid JobId { get; init; }
        public string JobNumber { get; init; } = "";
        public string LineCode { get; init; } = "";
        public DateTimeOffset? NeededFrom { get; init; }
        public string ComponentCode { get; init; } = "";
        public string MaterialCode { get; init; } = "";
        public string MaterialName { get; init; } = "";
        public Guid LotId { get; init; }
        public string LotLabel { get; init; } = "";
        public string? LocationCode { get; init; }
        public string? ContainerCode { get; init; }
        public decimal Quantity { get; init; }
        public string UomCode { get; init; } = "";
    }

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
        public Guid? ForMaterialId { get; init; }
        public decimal? Ratio { get; init; }
        public DateTimeOffset CreatedAt { get; init; }
        public string? CreatedBy { get; init; }
        public string JobNumber { get; init; } = "";

        public LotAllocation ToDomain() => new(Id, SiteId, JobId, MaterialId, LotId, Quantity, AllocatedQty, Status, Basis, Note, ForMaterialId, Ratio);

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
        public bool NearLine { get; init; }
        public bool InContainer { get; init; }
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
            await session.QueryAsync<AllocationSql.LotRow>(AllocationSql.Lots, new { Id = jobId, job.SiteId }, cancellationToken),
            await session.QueryAsync<AllocationSql.AlternateRow>(AllocationSql.Alternates, args, cancellationToken),
            await session.QuerySingleOrDefaultAsync<string[]>(AllocationSql.Preferences, new { job.SiteId }, cancellationToken));
    }

    public async Task<AllocationSettings> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken) =>
        (await session.QuerySingleOrDefaultAsync<AllocationSql.SettingsRow>(AllocationSql.Settings, new { SiteId = siteId }, cancellationToken))?.ToSettings()
        ?? AllocationSettings.Default(siteId);

    public async Task<Versioned<AllocationSettings>> GetSettingsForUpdateAsync(Guid siteId, CancellationToken cancellationToken)
    {
        var created = await session.QuerySingleOrDefaultAsync<bool?>(AllocationSql.EnsureSettings, new { SiteId = siteId }, cancellationToken) == true;
        var row = (await session.QuerySingleOrDefaultAsync<AllocationSql.SettingsRow>(AllocationSql.SettingsForUpdate, new { SiteId = siteId }, cancellationToken))!;
        return new Versioned<AllocationSettings>(row.ToSettings(), created ? 0 : row.Version);
    }

    public Task<long> UpdateSettingsAsync(AllocationSettings settings, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<long>(
            AllocationSql.UpdateSettings,
            new
            {
                settings.SiteId,
                settings.KeepCurrent,
                settings.HardReservation,
                Preferences = settings.Preferences.ToArray(),
                At = at,
            },
            cancellationToken);

    public Task<IReadOnlyList<Guid>> ListSitesDueForRefreshAsync(TimeSpan quiet, TimeSpan atMost, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(AllocationSql.DueForRefresh, new { Quiet = quiet, AtMost = atMost }, cancellationToken);

    public Task MarkRefreshedAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(AllocationSql.MarkRefreshed, new { SiteId = siteId, At = at }, cancellationToken);

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
            await connection.QueryAsync<AllocationSql.LotRow>(new CommandDefinition(AllocationSql.Lots, new { Id = jobId, job.SiteId }, cancellationToken: cancellationToken)),
            await connection.QueryAsync<AllocationSql.AlternateRow>(new CommandDefinition(AllocationSql.Alternates, args, cancellationToken: cancellationToken)),
            await connection.QuerySingleOrDefaultAsync<string[]>(new CommandDefinition(AllocationSql.Preferences, new { job.SiteId }, cancellationToken: cancellationToken)));
    }

    public async Task<(AllocationSettings Settings, long Version)> GetSettingsAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AllocationSql.SettingsRow>(
            new CommandDefinition(AllocationSql.Settings, new { SiteId = siteId }, cancellationToken: cancellationToken));
        return row is null ? (AllocationSettings.Default(siteId), 0) : (row.ToSettings(), row.Version);
    }

    public async Task<PickingListDto> GetPickingListAsync(Guid siteId, Guid? jobId, Guid? lineId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var args = new { SiteId = siteId, JobId = jobId, LineId = lineId };
        var site = await connection.QuerySingleOrDefaultAsync<AllocationSql.PickingSiteRow>(new CommandDefinition(AllocationSql.PickingSite, args, cancellationToken: cancellationToken))
            ?? throw new NotFoundException($"Site {siteId} not found.");
        var rows = await connection.QueryAsync<AllocationSql.PickingRow>(new CommandDefinition(AllocationSql.Picking, args, cancellationToken: cancellationToken));
        return new PickingListDto
        {
            SiteId = siteId,
            SiteCode = site.Code,
            SiteTimeZone = site.TimeZone,
            Scope = site.Scope,
            Lines = [.. rows.Select(r => new PickingLineDto
        {
            JobId = r.JobId,
            JobNumber = r.JobNumber,
            LineCode = r.LineCode,
            NeededFrom = r.NeededFrom,
            ComponentCode = r.ComponentCode,
            MaterialCode = r.MaterialCode,
            MaterialName = r.MaterialName,
            LotId = r.LotId,
            LotLabel = r.LotLabel,
            LocationCode = r.LocationCode,
            ContainerCode = r.ContainerCode,
            Quantity = r.Quantity,
            UomCode = r.UomCode,
        })],
        };
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
        return [.. rows.Select(r => new AllocatedStock(r.JobId, r.MaterialId, r.Quantity, r.LotMaterialId, r.LotQuantity))];
    }

    private sealed class AllocatedRow
    {
        public Guid JobId { get; init; }
        public Guid MaterialId { get; init; }
        public decimal Quantity { get; init; }
        public Guid LotMaterialId { get; init; }
        public decimal LotQuantity { get; init; }
    }
}
