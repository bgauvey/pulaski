using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Domain.Inventory;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Inventory;

/// <summary>Feed point automation's SQL inside the unit of work (#33, D-079).</summary>
internal sealed class FeedRepository(DbSession session) : IFeedRepository
{
    private const string FeedPointsOfLine = """
        SELECT f.id, f.code,
               EXISTS (SELECT 1 FROM core.equipment_tag t WHERE t.equipment_id = f.id AND t.tag_role = 'consumed_count' AND t.is_active) AS has_counter
        FROM core.equipment_closure c
        JOIN core.equipment f ON f.id = c.descendant_id
        WHERE c.ancestor_id = @LineId AND c.depth > 0 AND f.equipment_role = 'feed_point' AND f.holds_inventory AND f.is_active
        ORDER BY f.code
        """;

    // Standing movements only: a reversed one and its reversal count as neither having happened (D-063).
    internal const string FeedEvents = $"""
        SELECT e.lot_id, l.material_id, e.effective_at AS at, e.occurred_at AS recorded_at, e.event_kind AS kind, e.qty_delta, e.qty_after,
               e.from_location_id, e.to_location_id
        FROM ts.lot_event e
        JOIN core.lot l ON l.id = e.lot_id
        WHERE (e.to_location_id = ANY(@Ids) OR e.from_location_id = ANY(@Ids)) AND e.effective_at <= @Until
          AND e.reverses_event_id IS NULL AND {LotEventSql.NotReversed}
        ORDER BY e.effective_at, e.occurred_at, e.id
        """;

    private const string AutoComponents = """
        SELECT material_id, consumption_method AS method, planned_qty, uom_id
        FROM core.job_material
        WHERE job_id = @JobId AND direction = 'consume' AND consumption_method IN ('backflush','time_window')
        ORDER BY id
        """;

    private const string PreviousReport = """
        SELECT occurred_at FROM ts.production_count
        WHERE run_id = @RunId AND count_kind = 'processed' AND source = 'operator' AND report_id IS DISTINCT FROM @ReportId AND occurred_at <= @Before
          AND NOT EXISTS (SELECT 1 FROM ts.production_count r WHERE r.corrects_id = production_count.id)
        ORDER BY occurred_at DESC
        LIMIT 1
        """;

    private const string LotsAt = """
        SELECT id FROM core.lot
        WHERE location_equipment_id = ANY(@Ids) AND material_id = @MaterialId AND status = 'available' AND quantity > 0
          AND (expires_at IS NULL OR expires_at > now())
        ORDER BY expires_at NULLS LAST, created_at, id
        """;

    private const string IsPosted = """
        SELECT EXISTS (SELECT 1 FROM ts.lot_event WHERE source_ref = @Ref) OR EXISTS (SELECT 1 FROM core.feed_shortfall WHERE source_ref = @Ref)
        """;

    private const string InsertShortfall = """
        INSERT INTO core.feed_shortfall (id, site_id, equipment_id, job_id, run_id, material_id, lot_id, quantity, uom_id, source, source_ref,
                                         occurred_at, reason, created_at, updated_at)
        VALUES (@Id, @SiteId, @EquipmentId, @JobId, @RunId, @MaterialId, @LotId, @Quantity, @UomId, @Source, @SourceRef,
                @OccurredAt, @Reason, @At, @At)
        ON CONFLICT (source_ref, equipment_id, material_id) DO NOTHING
        """;

    private const string UnpostedCounts = """
        SELECT c.id, c.occurred_at, c.equipment_id AS feed_point_id, c.delta_qty AS quantity
        FROM ts.production_count c
        JOIN core.equipment f ON f.id = c.equipment_id AND f.equipment_role = 'feed_point'
        WHERE c.equipment_id = ANY(@Ids) AND c.count_kind = 'consumed' AND c.source = 'machine' AND c.occurred_at >= @Since
          AND NOT EXISTS (SELECT 1 FROM ts.lot_event e WHERE e.source_ref = c.id)
          AND NOT EXISTS (SELECT 1 FROM core.feed_shortfall s WHERE s.source_ref = c.id)
        ORDER BY c.occurred_at, c.id
        LIMIT @Take
        """;

    // The feed point's line is its nearest parent that runs jobs; what ran there is the run going on at the time.
    private const string CounterContext = """
        SELECT f.site_id, f.code AS feed_point_code, line.id AS line_id, r.id AS run_id, r.job_id,
               (SELECT t.uom_id FROM core.equipment_tag t WHERE t.equipment_id = f.id AND t.tag_role = 'consumed_count' AND t.is_active LIMIT 1) AS counter_uom_id
        FROM core.equipment f
        LEFT JOIN LATERAL (
            SELECT a.id FROM core.equipment_closure c JOIN core.equipment a ON a.id = c.ancestor_id
            WHERE c.descendant_id = f.id AND c.depth > 0 AND a.can_run_job
            ORDER BY c.depth LIMIT 1) line ON true
        LEFT JOIN LATERAL (
            SELECT r.id, r.job_id FROM core.job_run r
            WHERE r.equipment_id = line.id AND r.started_at <= @At AND (r.ended_at IS NULL OR r.ended_at > @At)
            ORDER BY r.started_at DESC LIMIT 1) r ON true
        WHERE f.id = @Id
        """;

    private const string ShortfallColumns = """
        id, site_id, equipment_id, job_id, run_id, material_id, lot_id, quantity, uom_id, source, source_ref, occurred_at, reason, status,
        resolved_lot_id, resolved_by, resolved_at, resolution_note, xmin::text::bigint AS version
        """;

    private const string ShortfallForUpdate = $"SELECT {ShortfallColumns} FROM core.feed_shortfall WHERE id = @Id FOR UPDATE";

    private const string OpenShortfallsOf = $"SELECT {ShortfallColumns} FROM core.feed_shortfall WHERE source_ref = @Ref AND status = 'open' ORDER BY id FOR UPDATE";

    private const string UpdateShortfall = """
        UPDATE core.feed_shortfall
        SET status = @Status, resolved_lot_id = @ResolvedLotId, resolved_by = @ResolvedBy, resolved_at = @ResolvedAt, resolution_note = @ResolutionNote, updated_at = @At
        WHERE id = @Id
        RETURNING xmin::text::bigint
        """;

    private const string AutoConsumptions = $"""
        SELECT e.lot_id, e.id AS event_id FROM ts.lot_event e
        WHERE e.source_ref = @Ref AND e.event_kind = 'consumed' AND e.auto_source IS NOT NULL
          AND e.reverses_event_id IS NULL AND {LotEventSql.NotReversed}
        ORDER BY e.occurred_at, e.id
        """;

    public async Task<IReadOnlyList<FeedPointRow>> ListFeedPointsOfLineAsync(Guid lineId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<PointRow>(FeedPointsOfLine, new { LineId = lineId }, cancellationToken)).Select(r => new FeedPointRow(r.Id, r.Code, r.HasCounter))];

    public async Task<IReadOnlyList<FeedEvent>> ListFeedEventsAsync(IReadOnlyCollection<Guid> feedPointIds, DateTimeOffset until, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<FeedEventRow>(FeedEvents, new { Ids = feedPointIds.ToArray(), Until = until }, cancellationToken)).Select(r => r.ToDomain())];

    public async Task<IReadOnlyList<AutoComponent>> ListAutoComponentsAsync(Guid jobId, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ComponentRow>(AutoComponents, new { JobId = jobId }, cancellationToken)).Select(r => new AutoComponent(r.MaterialId, r.Method, r.PlannedQty, r.UomId))];

    public Task<DateTimeOffset?> GetPreviousReportAtAsync(Guid runId, Guid reportId, DateTimeOffset before, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<DateTimeOffset?>(PreviousReport, new { RunId = runId, ReportId = reportId, Before = before }, cancellationToken);

    public Task<IReadOnlyList<Guid>> ListLotsAtAsync(IReadOnlyCollection<Guid> locationIds, Guid materialId, CancellationToken cancellationToken) =>
        session.QueryAsync<Guid>(LotsAt, new { Ids = locationIds.ToArray(), MaterialId = materialId }, cancellationToken);

    public Task<bool> IsPostedAsync(Guid sourceRef, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<bool>(IsPosted, new { Ref = sourceRef }, cancellationToken);

    public Task InsertShortfallAsync(FeedShortfall shortfall, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(InsertShortfall, new
        {
            shortfall.Id,
            shortfall.SiteId,
            shortfall.EquipmentId,
            shortfall.JobId,
            shortfall.RunId,
            shortfall.MaterialId,
            shortfall.LotId,
            shortfall.Quantity,
            shortfall.UomId,
            shortfall.Source,
            shortfall.SourceRef,
            shortfall.OccurredAt,
            shortfall.Reason,
            At = at,
        }, cancellationToken);

    public async Task<IReadOnlyList<CounterRow>> ListUnpostedCountsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset since, int take, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<CountRow>(UnpostedCounts, new { Ids = equipmentIds.ToArray(), Since = since, Take = take }, cancellationToken))
            .Select(r => new CounterRow(r.Id, r.OccurredAt, r.FeedPointId, r.Quantity))];

    public async Task<CounterContext?> GetCounterContextAsync(Guid feedPointId, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ContextRow>(CounterContext, new { Id = feedPointId, At = at }, cancellationToken) is { } r
            ? new CounterContext(r.SiteId, r.FeedPointCode, r.LineId, r.RunId, r.JobId, r.CounterUomId)
            : null;

    public async Task<Versioned<FeedShortfall>?> GetShortfallForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<ShortfallRow>(ShortfallForUpdate, new { Id = id }, cancellationToken) is { } r ? r.ToVersioned() : null;

    public Task<long> UpdateShortfallAsync(FeedShortfall shortfall, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteScalarAsync<long>(UpdateShortfall, new
        {
            shortfall.Id,
            shortfall.Status,
            shortfall.ResolvedLotId,
            shortfall.ResolvedBy,
            shortfall.ResolvedAt,
            shortfall.ResolutionNote,
            At = at,
        }, cancellationToken);

    public async Task<IReadOnlyList<Versioned<FeedShortfall>>> LockOpenShortfallsOfAsync(Guid sourceRef, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ShortfallRow>(OpenShortfallsOf, new { Ref = sourceRef }, cancellationToken)).Select(r => r.ToVersioned())];

    public async Task<IReadOnlyList<(Guid LotId, Guid EventId)>> ListAutoConsumptionsAsync(Guid sourceRef, CancellationToken cancellationToken) =>
        [.. (await session.QueryAsync<ConsumptionRow>(AutoConsumptions, new { Ref = sourceRef }, cancellationToken)).Select(r => (r.LotId, r.EventId))];

    private sealed class PointRow
    {
        public Guid Id { get; init; }
        public string Code { get; init; } = "";
        public bool HasCounter { get; init; }
    }

    internal sealed class FeedEventRow
    {
        public Guid LotId { get; init; }
        public Guid MaterialId { get; init; }
        public DateTimeOffset At { get; init; }
        public DateTimeOffset RecordedAt { get; init; }
        public string Kind { get; init; } = "";
        public decimal? QtyDelta { get; init; }
        public decimal QtyAfter { get; init; }
        public Guid? FromLocationId { get; init; }
        public Guid? ToLocationId { get; init; }

        public FeedEvent ToDomain() => new(LotId, MaterialId, At, RecordedAt, Kind, QtyDelta, QtyAfter, FromLocationId, ToLocationId);
    }

    private sealed class ComponentRow
    {
        public Guid MaterialId { get; init; }
        public string Method { get; init; } = "";
        public decimal PlannedQty { get; init; }
        public Guid UomId { get; init; }
    }

    private sealed class CountRow
    {
        public Guid Id { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public Guid FeedPointId { get; init; }
        public decimal Quantity { get; init; }
    }

    private sealed class ContextRow
    {
        public Guid SiteId { get; init; }
        public string FeedPointCode { get; init; } = "";
        public Guid? LineId { get; init; }
        public Guid? RunId { get; init; }
        public Guid? JobId { get; init; }
        public Guid? CounterUomId { get; init; }
    }

    private sealed class ConsumptionRow
    {
        public Guid LotId { get; init; }
        public Guid EventId { get; init; }
    }

    private sealed class ShortfallRow
    {
        public Guid Id { get; init; }
        public Guid SiteId { get; init; }
        public Guid EquipmentId { get; init; }
        public Guid? JobId { get; init; }
        public Guid? RunId { get; init; }
        public Guid? MaterialId { get; init; }
        public Guid? LotId { get; init; }
        public decimal Quantity { get; init; }
        public Guid? UomId { get; init; }
        public string Source { get; init; } = "";
        public Guid SourceRef { get; init; }
        public DateTimeOffset OccurredAt { get; init; }
        public string Reason { get; init; } = "";
        public string Status { get; init; } = "";
        public Guid? ResolvedLotId { get; init; }
        public string? ResolvedBy { get; init; }
        public DateTimeOffset? ResolvedAt { get; init; }
        public string? ResolutionNote { get; init; }
        public long Version { get; init; }

        public Versioned<FeedShortfall> ToVersioned() => new(
            new FeedShortfall(Id, SiteId, EquipmentId, JobId, RunId, MaterialId, LotId, Quantity, UomId, Source, SourceRef, OccurredAt, Reason, Status,
                ResolvedLotId, ResolvedBy, ResolvedAt, ResolutionNote),
            Version);
    }
}
