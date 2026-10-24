using Pulaski.Application.Abstractions;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Testing;

/// <summary>
/// In-memory lots, movements and genealogy (D-027), with the locations, materials, jobs and
/// reasons the commands look up. The read side answers only what the handler tests need; the
/// Infrastructure tests cover the real queries.
/// </summary>
public sealed class InMemoryInventory : IInventoryRepository, IInventoryReadStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<Guid, Versioned<Lot>> _lots = [];
    private readonly List<(LotEventRow Row, DateTimeOffset At)> _events = [];
    private readonly Dictionary<Guid, DateTimeOffset> _lotUpdated = [];
    private readonly Dictionary<Guid, Versioned<StockCount>> _counts = [];
    private readonly Dictionary<Guid, StockCountLine> _countLines = [];
    private readonly Dictionary<Guid, Versioned<ReasonCode>> _reasonCodes = [];
    private readonly HashSet<Guid> _sites = [];
    private readonly Dictionary<Guid, long> _materialVersions = [];
    private readonly List<GenealogyEdge> _edges = [];
    private readonly Dictionary<Guid, InventoryLocation> _locations = [];
    private readonly Dictionary<Guid, InventoryMaterial> _materials = [];
    private readonly Dictionary<Guid, InventoryJob> _jobs = [];
    private readonly Dictionary<Guid, InventoryReason> _reasons = [];
    private readonly HashSet<(Guid Job, Guid Material)> _planned = [];
    private long _next = 1;

    public IReadOnlyList<LotEventRow> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events.Select(e => e.Row)];
            }
        }
    }

    public IReadOnlyList<GenealogyEdge> Edges
    {
        get
        {
            lock (_lock)
            {
                return [.. _edges];
            }
        }
    }

    public InventoryLocation AddLocation(
        string code, Guid siteId, bool holdsInventory = true, bool isActive = true, bool comminglesLots = false, bool singleMaterial = false)
    {
        var location = new InventoryLocation
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            SiteId = siteId,
            HoldsInventory = holdsInventory,
            ComminglesLots = comminglesLots,
            SingleMaterial = singleMaterial,
            IsActive = isActive,
        };
        lock (_lock)
        {
            _locations[location.Id] = location;
        }

        return location;
    }

    public InventoryMaterial AddMaterial(
        string code, Guid baseUomId, string baseUomCode, bool isLotTracked = true, bool isActive = true, int? shelfLifeDays = null,
        decimal? batchSize = null, string? batchRule = null)
    {
        var material = new InventoryMaterial
        {
            BatchSize = batchSize,
            BatchRule = batchSize is null ? null : batchRule ?? "near",
            Id = Guid.CreateVersion7(),
            Code = code,
            BaseUomId = baseUomId,
            BaseUomCode = baseUomCode,
            IsLotTracked = isLotTracked,
            IsActive = isActive,
            ShelfLifeDays = shelfLifeDays,
        };
        lock (_lock)
        {
            _materials[material.Id] = material;
        }

        return material;
    }

    public InventoryJob AddJob(
        string number, Guid siteId, Guid? materialId, string status = "planned", string jobType = "production", Guid? uomId = null, decimal? plannedQty = null)
    {
        var job = new InventoryJob
        {
            Id = Guid.CreateVersion7(),
            SiteId = siteId,
            JobNumber = number,
            JobType = jobType,
            Status = status,
            MaterialId = materialId,
            EquipmentId = Guid.CreateVersion7(),
            LineCode = "EXT-01",
            UomId = uomId,
            PlannedQty = plannedQty,
        };
        lock (_lock)
        {
            _jobs[job.Id] = job;
        }

        return job;
    }

    public InventoryReason AddReason(string kind, string code, bool requiresComment = false, Guid? siteId = null, bool isActive = true)
    {
        var reason = new InventoryReason
        {
            Id = Guid.CreateVersion7(),
            SiteId = siteId,
            Kind = kind,
            Code = code,
            Name = code,
            IsSelectable = true,
            RequiresComment = requiresComment,
            IsActive = isActive,
        };
        lock (_lock)
        {
            _reasons[reason.Id] = reason;
        }

        return reason;
    }

    public void PlanComponent(Guid jobId, Guid materialId)
    {
        lock (_lock)
        {
            _planned.Add((jobId, materialId));
        }
    }

    public Versioned<Lot>? Find(Guid id)
    {
        lock (_lock)
        {
            return _lots.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<Lot> Lots
    {
        get
        {
            lock (_lock)
            {
                return [.. _lots.Values.Select(l => l.Value)];
            }
        }
    }

    // ---- Repository ----

    public Task<Versioned<Lot>?> GetLotForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(Find(id));

    public Task<Versioned<Lot>?> FindLotForUpdateAsync(Guid siteId, Guid materialId, string lotNumber, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_lots.Values.FirstOrDefault(l =>
                l.Value.SiteId == siteId && l.Value.MaterialId == materialId && l.Value.LotNumber == lotNumber));
        }
    }

    public Task<Versioned<Lot>?> FindBulkLotForUpdateAsync(Guid materialId, Guid locationId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_lots.Values.FirstOrDefault(l =>
                l.Value.IsBulk && l.Value.MaterialId == materialId && l.Value.LocationId == locationId));
        }
    }

    public Task<long> InsertLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Store(lot, at));

    public Task<long> UpdateLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken) => Task.FromResult(Store(lot, at));

    public Task InsertEventAsync(LotEventRow row, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _events.Add((row, at));
        }

        return Task.CompletedTask;
    }

    public Task<InventoryLocation?> GetLocationAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_locations.GetValueOrDefault(id));
        }
    }

    public Task<InventoryMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.GetValueOrDefault(id));
        }
    }

    public Task<InventoryJob?> GetJobForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_jobs.GetValueOrDefault(id));
        }
    }

    public Task<IReadOnlyList<Guid>> ListLotsProducedByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(LotsOfJob(jobId, LotEventKind.Produced));

    public Task<IReadOnlyList<Guid>> ListLotsConsumedByJobAsync(Guid jobId, CancellationToken cancellationToken) =>
        Task.FromResult(LotsOfJob(jobId, LotEventKind.Consumed));

    /// <summary>Each carry of lot records a split or merge asked for (D-056), in order.</summary>
    public List<(Guid From, Guid To, string Via, bool WithAttributes)> Carried { get; } = [];

    public Task CarryLotRecordsAsync(Guid fromLotId, Guid toLotId, string via, bool withAttributes, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Carried.Add((fromLotId, toLotId, via, withAttributes));
        }

        return Task.CompletedTask;
    }

    /// <summary>Posted stock counts, by lot, as a test sets them.</summary>
    public Dictionary<Guid, DateTimeOffset> CountedAt { get; } = [];

    /// <summary>When each genealogy edge took effect, as the handlers asked (D-057).</summary>
    public Dictionary<Guid, DateTimeOffset> EdgeEffectiveAt { get; } = [];

    public Task<IReadOnlyList<QuantityPoint>> ListQuantityTimelineAsync(Guid lotId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            DateTimeOffset Effective((LotEventRow Row, DateTimeOffset At) e) => e.Row.EffectiveAt ?? e.At;
            var simultaneous = _events.Where(r => r.Row.LotId == lotId && r.Row.ReversesEventId is not null)
                .SelectMany(r => _events.Where(o => o.Row.Id == r.Row.ReversesEventId && Effective(o) == Effective(r)).SelectMany(o => new[] { o.Row.Id, r.Row.Id }))
                .ToHashSet();
            return Task.FromResult<IReadOnlyList<QuantityPoint>>([.. _events
                .Where(e => e.Row.LotId == lotId && e.Row.Movement.QtyDelta is not null && !simultaneous.Contains(e.Row.Id))
                .Select(e => new QuantityPoint(e.Row.EffectiveAt ?? e.At, e.At, e.Row.Movement.QtyDelta!.Value))
                .OrderBy(p => p.EffectiveAt).ThenBy(p => p.OccurredAt)]);
        }
    }

    public Task<DateTimeOffset?> LatestCountOfLotAsync(Guid lotId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(CountedAt.TryGetValue(lotId, out var at) ? at : (DateTimeOffset?)null);
        }
    }

    public Task InsertGenealogyAsync(IReadOnlyList<GenealogyEdge> edges, DateTimeOffset at, CancellationToken cancellationToken, DateTimeOffset? effectiveAt = null)
    {
        lock (_lock)
        {
            foreach (var edge in edges)
            {
                EdgeEffectiveAt.TryAdd(edge.Id, effectiveAt ?? at);
            }
        }

        lock (_lock)
        {
            _edges.AddRange(edges.Where(e => !_edges.Any(x => x.ParentLotId == e.ParentLotId && x.ChildLotId == e.ChildLotId)));
        }

        return Task.CompletedTask;
    }

    public Task<InventoryReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_reasons.GetValueOrDefault(id));
        }
    }

    public Task<bool> PlansComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_planned.Contains((jobId, materialId)));
        }
    }

    // ---- Read store: only what the handler tests use ----

    public Task<IReadOnlyList<MaterialStockDto>> ListStockAsync(StockFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialStockDto>>([]);

    public Task<MaterialStockDetailDto?> GetMaterialStockAsync(Guid materialId, Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<MaterialStockDetailDto?>(null);

    public Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(LotFilter filter, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<LotSummaryDto>>([]);

    public Task<LotDto?> GetLotAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<LotDto?>(null);

    public Task<IReadOnlyList<LotTraceNodeDto>> TraceAsync(Guid lotId, bool forward, CancellationToken cancellationToken) =>
        Task.FromResult(Traces.GetValueOrDefault((lotId, forward)) ?? []);

    public Task<WhereUsedDto?> WhereUsedAsync(Guid materialId, CancellationToken cancellationToken) => Task.FromResult<WhereUsedDto?>(null);

    public Task<IReadOnlyList<InventoryLocationDto>> ListLocationsAsync(Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<InventoryLocationDto>>([]);

    public Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(Guid? siteId, string? search, Guid? materialId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeclarableJobDto>>([]);

    public Task<JobMaterialsDto?> GetJobMaterialsAsync(Guid jobId, CancellationToken cancellationToken) => Task.FromResult<JobMaterialsDto?>(null);

    public Task<IReadOnlyList<ReasonCodeDto>> ListReasonCodesAsync(string kind, Guid? siteId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<ReasonCodeDto>>([.. _reasons.Values
                .Where(r => r.Kind == kind && r.IsActive && (r.SiteId is null || r.SiteId == siteId))
                .Select(r => new ReasonCodeDto { Id = r.Id, Kind = r.Kind, Code = r.Code, Name = r.Name, RequiresComment = r.RequiresComment })]);
        }
    }

    public Task<IReadOnlyList<ComponentDemandRow>> ListComponentDemandAsync(
        Guid siteId, Guid? equipmentId, DateTimeOffset? before, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ComponentDemandRow>>([.. Demand.Where(d => before is null || d.NeededFrom < before)]);

    public Task<IReadOnlyList<MaterialAvailabilityRow>> ListAvailabilityAsync(
        Guid siteId, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialAvailabilityRow>>([.. Availability.Where(a => materialIds.Contains(a.MaterialId))]);

    public Task<JournalPageDto> ListMovementsAsync(JournalFilter filter, CancellationToken cancellationToken)
    {
        LastJournalFilter = filter;
        return Task.FromResult(new JournalPageDto());
    }

    /// <summary>Rows the shortage check reads, set by a test.</summary>
    public List<ComponentDemandRow> Demand { get; } = [];

    public List<MaterialAvailabilityRow> Availability { get; } = [];

    /// <summary>The filter the journal was last asked for.</summary>
    public JournalFilter? LastJournalFilter { get; private set; }

    private long Store(Lot lot, DateTimeOffset at)
    {
        lock (_lock)
        {
            var version = ++_next;
            _lots[lot.Id] = new(lot, version);
            _lotUpdated[lot.Id] = at;
            return version;
        }
    }

    /// <summary>Marks a lot as changed at <paramref name="at"/>, as another command would.</summary>
    public void Touch(Guid lotId, DateTimeOffset at)
    {
        lock (_lock)
        {
            _lotUpdated[lotId] = at;
        }
    }

    public void AddSite(Guid siteId)
    {
        lock (_lock)
        {
            _sites.Add(siteId);
        }
    }

    public Versioned<StockCount>? FindCount(Guid id)
    {
        lock (_lock)
        {
            return _counts.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<StockCountLine> LinesOf(Guid countId)
    {
        lock (_lock)
        {
            return [.. _countLines.Values.Where(l => l.CountId == countId)];
        }
    }

    public Versioned<ReasonCode>? FindReasonCode(Guid id)
    {
        lock (_lock)
        {
            return _reasonCodes.GetValueOrDefault(id);
        }
    }

    public Task<IReadOnlyList<LockedLot>> LockLotsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<LockedLot>>([.. ids.Distinct().Where(_lots.ContainsKey)
                .Select(id => new LockedLot(_lots[id].Value, _lots[id].Version, _lotUpdated.GetValueOrDefault(id)))]);
        }
    }

    public Task<LotEventRecord?> GetLatestEventAsync(Guid lotId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var latest = _events.LastOrDefault(e => e.Row.LotId == lotId);
            return Task.FromResult(latest.Row is null ? null : new LotEventRecord(latest.Row.Id, latest.At, latest.Row.Movement, latest.Row.RelatedLotId, latest.Row.EffectiveAt ?? latest.At));
        }
    }

    public Task<IReadOnlyList<Lot>> ListOnHandLotsAtAsync(Guid locationId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Lot>>([.. _lots.Values.Select(l => l.Value)
                .Where(l => l.LocationId == locationId && LotStatus.IsOnHand(l.Status) && l.Quantity > 0)]);
        }
    }

    public Task<Versioned<StockCount>?> GetStockCountForUpdateAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(FindCount(id));

    public Task<int> CountStockCountsOnAsync(Guid siteId, DateTimeOffset day, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_counts.Values.Count(c => c.Value.SiteId == siteId));
        }
    }

    public Task<long> InsertStockCountAsync(StockCount count, string createdBy, DateTimeOffset at, CancellationToken cancellationToken) =>
        UpdateStockCountAsync(count, createdBy, at, cancellationToken);

    public Task<long> UpdateStockCountAsync(StockCount count, string actor, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _counts[count.Id] = new(count, version);
            return Task.FromResult(version);
        }
    }

    public Task InsertStockCountLinesAsync(IReadOnlyList<StockCountLine> lines, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            foreach (var line in lines)
            {
                _countLines[line.Id] = line;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StockCountLine>> GetStockCountLinesAsync(Guid countId, CancellationToken cancellationToken) => Task.FromResult(LinesOf(countId));

    public Task UpdateStockCountLineAsync(StockCountLine line, CancellationToken cancellationToken) => InsertStockCountLinesAsync([line], cancellationToken);

    public Task<string?> GetReasonKindAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult(FindReasonCode(id)?.Value.Kind);

    public Task<IReadOnlyList<Versioned<ReasonCode>>> LockReasonTreeAsync(string kind, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<Versioned<ReasonCode>>>([.. _reasonCodes.Values.Where(r => r.Value.Kind == kind)]);
        }
    }

    public Task<long> InsertReasonCodeAsync(ReasonCode reason, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (_reasonCodes.Values.Any(r => r.Value.SiteId == reason.SiteId && r.Value.Kind == reason.Kind && r.Value.Code == reason.Code))
            {
                throw new Application.Errors.ConflictException("That code is already used for this kind of reason.");
            }
        }

        return UpdateReasonCodeAsync(reason, cancellationToken);
    }

    public Task<long> UpdateReasonCodeAsync(ReasonCode reason, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var version = ++_next;
            _reasonCodes[reason.Id] = new(reason, version);
            return Task.FromResult(version);
        }
    }

    public Task LockLocationAsync(Guid id, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<string?> OtherMaterialAtAsync(Guid locationId, Guid materialId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var other = _lots.Values.Select(l => l.Value)
                .FirstOrDefault(l => l.LocationId == locationId && l.MaterialId != materialId && l.Quantity > 0 && LotStatus.IsOnHand(l.Status));
            return Task.FromResult(other is null ? null : _materials.GetValueOrDefault(other.MaterialId)?.Code ?? other.MaterialId.ToString());
        }
    }

    public Task<Versioned<InventoryMaterial>?> GetMaterialForUpdateAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.TryGetValue(id, out var m) ? new Versioned<InventoryMaterial>(m, _materialVersions.GetValueOrDefault(id, 1)) : null);
        }
    }

    public Task<long> UpdateShelfLifeAsync(Guid materialId, int? shelfLifeDays, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            _materials[materialId] = _materials[materialId] with { ShelfLifeDays = shelfLifeDays };
            var version = ++_next;
            _materialVersions[materialId] = version;
            return Task.FromResult(version);
        }
    }

    public InventoryMaterial? FindMaterial(Guid id)
    {
        lock (_lock)
        {
            return _materials.GetValueOrDefault(id);
        }
    }

    /// <summary>Balance histories by lot, as a test sets them (D-060).</summary>
    public Dictionary<Guid, BalanceHistory> BalanceHistories { get; } = [];

    /// <summary>Trace results by lot and direction, as a test sets them.</summary>
    public Dictionary<(Guid LotId, bool Forward), IReadOnlyList<LotTraceNodeDto>> Traces { get; } = [];

    public Dictionary<Guid, Versioned<FlowSettings>> Flows { get; } = [];

    public Task<BalanceHistory?> GetBalanceHistoryAsync(Guid lotId, CancellationToken cancellationToken) =>
        Task.FromResult(BalanceHistories.GetValueOrDefault(lotId));

    public Task<Versioned<FlowSettings>?> GetFlowSettingsForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken) =>
        Task.FromResult(Flows.GetValueOrDefault(equipmentId));

    public Task<long> UpdateFlowSettingsAsync(FlowSettings settings, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var version = Flows[settings.EquipmentId].Version + 1;
        Flows[settings.EquipmentId] = new(settings, version);
        return Task.FromResult(version);
    }

    /// <summary>Unit codes the fake resolves, as a test sets them.</summary>
    public Dictionary<string, Guid> UomCodes { get; } = new(StringComparer.Ordinal);

    public Task<Guid?> FindMaterialIdByCodeAsync(string code, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_materials.Values.FirstOrDefault(m => m.Code == code)?.Id);
        }
    }

    public Task<Guid?> FindUomIdByCodeAsync(string code, CancellationToken cancellationToken) =>
        Task.FromResult(UomCodes.TryGetValue(code, out var id) ? id : (Guid?)null);

    public Task<bool> IsSiteAsync(Guid id, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_sites.Contains(id));
        }
    }

    public Task<IReadOnlyList<StockCountSummaryDto>> ListStockCountsAsync(Guid siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StockCountSummaryDto>>([]);

    public Task<StockCountDto?> GetStockCountAsync(Guid id, CancellationToken cancellationToken) => Task.FromResult<StockCountDto?>(null);

    public Task<IReadOnlyList<ReasonCodeAdminDto>> ListAllReasonCodesAsync(Guid? siteId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReasonCodeAdminDto>>([]);

    public Task<IReadOnlyList<StockOnDateDto>> StockOnDateAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<StockOnDateDto>>([]);

    public Task<IReadOnlyList<AgingRowDto>> AgingAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<AgingRowDto>>([]);

    private IReadOnlyList<Guid> LotsOfJob(Guid jobId, string kind)
    {
        lock (_lock)
        {
            return [.. _events.Where(e => e.Row.JobId == jobId && e.Row.Movement.Kind == kind && !IsReversed(e.Row)).Select(e => e.Row.LotId).Distinct()];
        }
    }

    /// <summary>Edges ended by a reversal (D-063).</summary>
    public IReadOnlyList<GenealogyEdge> SupersededEdges
    {
        get
        {
            lock (_lock)
            {
                return [.. _superseded];
            }
        }
    }

    private readonly List<GenealogyEdge> _superseded = [];

    private bool IsReversed(LotEventRow row) => _events.Any(e => e.Row.LotId == row.LotId && e.Row.ReversesEventId == row.Id);

    private LotEventDetail Detail((LotEventRow Row, DateTimeOffset At) e) => new()
    {
        Id = e.Row.Id,
        LotId = e.Row.LotId,
        OccurredAt = e.At,
        EffectiveAt = e.Row.EffectiveAt ?? e.At,
        Movement = e.Row.Movement,
        RelatedLotId = e.Row.RelatedLotId,
        JobId = e.Row.JobId,
        RunId = e.Row.RunId,
        ReportId = e.Row.ReportId,
        ReasonCodeId = e.Row.ReasonCodeId,
        ReversedBy = _events.FirstOrDefault(r => r.Row.LotId == e.Row.LotId && r.Row.ReversesEventId == e.Row.Id).Row?.Id,
    };

    public Task<LotEventDetail?> GetEventAsync(Guid lotId, Guid eventId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var found = _events.FirstOrDefault(e => e.Row.LotId == lotId && e.Row.Id == eventId);
            return Task.FromResult(found.Row is null ? null : Detail(found));
        }
    }

    public Task<LotEventDetail?> GetCounterpartAsync(LotEventDetail movement, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var found = _events.Where(e => e.Row.LotId == movement.RelatedLotId && e.Row.RelatedLotId == movement.LotId && e.Row.Movement.Kind == movement.Movement.Kind
                    && e.Row.Movement.QtyDelta == -movement.Movement.QtyDelta && (e.At - movement.OccurredAt).Duration() <= TimeSpan.FromMinutes(1))
                .OrderBy(e => (e.At - movement.OccurredAt).Duration()).FirstOrDefault();
            return Task.FromResult(found.Row is null ? null : Detail(found));
        }
    }

    public Task<IReadOnlyList<LotEventDetail>> ListEventsAfterAsync(Guid lotId, LotEventDetail after, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var index = _events.FindIndex(e => e.Row.Id == after.Id);
            return Task.FromResult<IReadOnlyList<LotEventDetail>>([.. _events.Skip(index + 1).Where(e => e.Row.LotId == lotId).Select(Detail)]);
        }
    }

    public Task<IReadOnlyList<LotEventDetail>> ListReportEventsAsync(Guid reportId, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<LotEventDetail>>([.. _events.Where(e => e.Row.ReportId == reportId).Select(Detail)]);
        }
    }

    public Task<bool> HasDeclarationAsync(Guid lotId, Guid jobId, string kind, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            return Task.FromResult(_events.Any(e => e.Row.LotId == lotId && e.Row.JobId == jobId && e.Row.Movement.Kind == kind && !IsReversed(e.Row)));
        }
    }

    public Task SupersedeGenealogyAsync(Guid? parentLotId, Guid? childLotId, string basis, Guid? jobId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            var ended = _edges.Where(e => e.Basis == basis && (parentLotId is null || e.ParentLotId == parentLotId)
                && (childLotId is null || e.ChildLotId == childLotId) && (jobId is null || e.JobId == jobId)).ToList();
            _edges.RemoveAll(ended.Contains);
            _superseded.AddRange(ended);
        }

        return Task.CompletedTask;
    }
}
