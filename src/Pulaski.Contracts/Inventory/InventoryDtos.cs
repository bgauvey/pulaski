namespace Pulaski.Contracts.Inventory;

/// <summary>
/// One material's stock (D-027): quantities in its base unit over its on-hand lots (available, on
/// hold, quarantine), at one site or all of them.
/// </summary>
public sealed record MaterialStockDto
{
    public required Guid MaterialId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string MaterialType { get; init; }
    public Guid? MaterialClassId { get; init; }
    public string? MaterialClassCode { get; init; }
    public string? MaterialClassName { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }
    public bool IsLotTracked { get; init; }

    /// <summary>Available plus held.</summary>
    public decimal OnHand { get; init; }
    public decimal Available { get; init; }

    /// <summary>On hold or in quarantine.</summary>
    public decimal Held { get; init; }
    public int LotCount { get; init; }
    public int LocationCount { get; init; }

    /// <summary>The earliest expiry among the on-hand lots.</summary>
    public DateTimeOffset? NextExpiry { get; init; }

    /// <summary>A new lot expires this many days after it arrives (D-032).</summary>
    public int? ShelfLifeDays { get; init; }

    /// <summary>The material's concurrency token, to change its shelf life.</summary>
    public long MaterialVersion { get; init; }
}

/// <summary>A lot as listed: what, how much, where and in what state.</summary>
public sealed record LotSummaryDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    /// <summary>Null for a balance (D-031): show its location instead.</summary>
    public string? LotNumber { get; init; }

    /// <summary>The one balance of a material that isn't lot-tracked at its location; show the location, not the number.</summary>
    public bool IsBulk { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }

    /// <summary><c>available</c>, <c>on_hold</c>, <c>quarantine</c>, <c>consumed</c> or <c>scrapped</c>.</summary>
    public required string Status { get; init; }

    /// <summary>In the material's base unit.</summary>
    public decimal Quantity { get; init; }
    public required Guid UomId { get; init; }
    public required string UomCode { get; init; }
    public Guid? LocationId { get; init; }
    public string? LocationCode { get; init; }
    public string? LocationName { get; init; }
    public required string Origin { get; init; }
    public string? SupplierLot { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public Guid? ProducedByJobId { get; init; }
    public string? ProducedByJobNumber { get; init; }

    /// <summary>The container it is in (D-059); null when loose.</summary>
    public Guid? ContainerId { get; init; }
    public string? ContainerCode { get; init; }

    /// <summary>What its label's barcode carries: <c>PUL1L</c> + this (D-059).</summary>
    public long LabelNo { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Concurrency token; send it back to write up, write off, move or change status.</summary>
    public long Version { get; init; }
}

/// <summary>A material's balance at one location.</summary>
public sealed record LocationStockDto
{
    public required Guid LocationId { get; init; }
    public required string LocationCode { get; init; }
    public required string LocationName { get; init; }
    public int LotCount { get; init; }
    public decimal OnHand { get; init; }
    public decimal Available { get; init; }
}

/// <summary>A material with its on-hand lots and its balance per location.</summary>
public sealed record MaterialStockDetailDto
{
    public required MaterialStockDto Material { get; init; }
    public IReadOnlyList<LotSummaryDto> Lots { get; init; } = [];
    public IReadOnlyList<LocationStockDto> Locations { get; init; } = [];
}

/// <summary>One movement of a lot (<c>ts.lot_event</c>), newest first in a lot's history.</summary>
public sealed record LotEventDto
{
    public required Guid Id { get; init; }

    /// <summary>When it was recorded.</summary>
    public DateTimeOffset OccurredAt { get; init; }

    /// <summary>When it counts in stock (D-057): <see cref="OccurredAt"/>, or earlier when it was back-dated.</summary>
    public DateTimeOffset EffectiveAt { get; init; }

    public bool IsBackdated => EffectiveAt < OccurredAt;

    /// <summary><c>received</c>, <c>produced</c>, <c>adjusted</c>, <c>scrapped</c>, <c>consumed</c>, <c>moved</c> or <c>status_changed</c>.</summary>
    public required string Kind { get; init; }
    public decimal? QtyDelta { get; init; }
    public decimal? QtyAfter { get; init; }
    public string? UomCode { get; init; }
    public string? FromLocationCode { get; init; }
    public string? ToLocationCode { get; init; }
    public string? FromStatus { get; init; }
    public string? ToStatus { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }
    public string? ReasonCode { get; init; }
    public string? ReasonName { get; init; }
    public string? Comment { get; init; }
    public string? ActorId { get; init; }
    public Guid? RelatedLotId { get; init; }
    public string? RelatedLotNumber { get; init; }

    /// <summary>Where the related lot is, to name it when it is a balance.</summary>
    public string? RelatedLotLocationCode { get; init; }

    /// <summary>A shipment or count number (D-030).</summary>
    public string? Reference { get; init; }

    /// <summary>For a reversal, the movement it undoes.</summary>
    public Guid? ReversesEventId { get; init; }

    /// <summary>True once a reversal has undone it (D-063).</summary>
    public bool IsReversed { get; init; }

    /// <summary>For production, the report that made it (D-063): reversed with the report, on the job's page.</summary>
    public Guid? ReportId { get; init; }

    /// <summary>The container it was packed into, unpacked from, or moved with (D-059).</summary>
    public Guid? ContainerId { get; init; }
    public string? ContainerCode { get; init; }
}

/// <summary>A lot one genealogy step away: a parent it was made from, or a child made from it.</summary>
public sealed record LotLinkDto
{
    public required Guid LotId { get; init; }
    /// <summary>Null for a balance (D-031): show its location instead.</summary>
    public string? LotNumber { get; init; }
    public bool IsBulk { get; init; }
    public string? LocationCode { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required string Status { get; init; }
    public decimal Quantity { get; init; }
    public required string UomCode { get; init; }

    /// <summary><c>declared</c> today; <c>time_window</c>, <c>split</c> and <c>merge</c> come with execution.</summary>
    public required string LinkBasis { get; init; }
    public string? JobNumber { get; init; }

    /// <summary>When the link took effect (D-057), and when it was recorded; they differ for a back-dated movement.</summary>
    public DateTimeOffset EffectiveAt { get; init; }
    public DateTimeOffset DerivedAt { get; init; }
}

/// <summary>A lot with its history and its direct parents and children.</summary>
public sealed record LotDto
{
    public required LotSummaryDto Lot { get; init; }
    public bool MaterialIsLotTracked { get; init; }
    public string? SiteTimeZone { get; init; }
    public IReadOnlyList<LotEventDto> History { get; init; } = [];
    public IReadOnlyList<LotLinkDto> Parents { get; init; } = [];
    public IReadOnlyList<LotLinkDto> Children { get; init; } = [];

    /// <summary>Stock of it set aside for jobs (D-064).</summary>
    public IReadOnlyList<LotAllocationDto> Allocations { get; init; } = [];
}

/// <summary>A lot found by a trace, <see cref="Depth"/> steps from the start, reached from <see cref="ViaLotId"/>.</summary>
public sealed record LotTraceNodeDto
{
    public required Guid LotId { get; init; }
    public Guid? ViaLotId { get; init; }
    public Guid? ProducedByJobId { get; init; }

    /// <summary>
    /// For a link through a balance whose lots mix (D-060): <c>exact</c>, <c>estimated</c> or <c>possible</c>, with why; null for
    /// an ordinary declared link.
    /// </summary>
    public string? Precision { get; init; }
    public string? PrecisionNote { get; init; }
    public int Depth { get; init; }
    /// <summary>Null for a balance (D-031): show its location instead.</summary>
    public string? LotNumber { get; init; }
    public bool IsBulk { get; init; }
    public string? LocationCode { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required string Status { get; init; }
    public decimal Quantity { get; init; }
    public required string UomCode { get; init; }
}

/// <summary>A BOM version that lists the material as a component.</summary>
public sealed record BomUseDto
{
    public required Guid BomId { get; init; }
    public required Guid VersionId { get; init; }
    public int VersionNumber { get; init; }

    /// <summary><c>released</c> or <c>draft</c>.</summary>
    public required string VersionStatus { get; init; }
    public bool BomIsActive { get; init; }

    /// <summary>The material the BOM makes.</summary>
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public string? ProcessCode { get; init; }
    public int LineNo { get; init; }
    public decimal QtyPer { get; init; }
    public string? UomCode { get; init; }
    public decimal BaseQty { get; init; }
    public required string BaseUomCode { get; init; }
    public int? OperationNumber { get; init; }
    public string? ConsumptionMethod { get; init; }
}

/// <summary>An open job planned to consume the material, with what it has consumed so far.</summary>
public sealed record JobUseDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobStatus { get; init; }
    public string? WorkOrderNumber { get; init; }
    public required string LineCode { get; init; }

    /// <summary>What the job makes.</summary>
    public string? MaterialCode { get; init; }
    public decimal PlannedQty { get; init; }
    public required string UomCode { get; init; }

    /// <summary>In the component's base unit.</summary>
    public decimal ConsumedQty { get; init; }
}

/// <summary>Where a material is used: in BOMs, and by open jobs.</summary>
public sealed record WhereUsedDto
{
    public required Guid MaterialId { get; init; }
    public required string BaseUomCode { get; init; }
    public IReadOnlyList<BomUseDto> Boms { get; init; } = [];
    public IReadOnlyList<JobUseDto> Jobs { get; init; } = [];
}

/// <summary>A location that holds inventory (equipment with <c>holds_inventory</c>).</summary>
public sealed record InventoryLocationDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string EquipmentRole { get; init; }
    public string? ParentCode { get; init; }
    public bool IsActive { get; init; }

    /// <summary>Lots here mix into one balance per material, like a silo (D-031).</summary>
    public bool ComminglesLots { get; init; }

    /// <summary>Takes one material at a time (D-032).</summary>
    public bool SingleMaterial { get; init; }

    /// <summary>How a balance here flows (D-060): <c>mixed</c> or <c>fifo</c>; and the least time material stays, in minutes.</summary>
    public string FlowModel { get; init; } = "mixed";
    public int? MinResidenceMinutes { get; init; }

    /// <summary>The equipment's version, to change its flow settings.</summary>
    public long Version { get; init; }
    public int LotCount { get; init; }
    public int MaterialCount { get; init; }
}

/// <summary>An open production job that consumption and production can be declared against.</summary>
public sealed record DeclarableJobDto
{
    public required Guid Id { get; init; }
    public required string JobNumber { get; init; }
    public required string Status { get; init; }
    public required Guid SiteId { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public decimal PlannedQty { get; init; }
    public string? UomCode { get; init; }
    public required string LineCode { get; init; }
    public string? WorkOrderNumber { get; init; }

    /// <summary>True when the job plans to consume the material the list was asked about.</summary>
    public bool PlansComponent { get; init; }
}

/// <summary>A component of a job: what was planned, and what has been consumed.</summary>
public sealed record JobComponentUseDto
{
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }

    /// <summary>False for a material consumed without being planned (a substitution).</summary>
    public bool IsPlanned { get; init; }
    public decimal? PlannedQty { get; init; }
    public string? PlannedUomCode { get; init; }

    /// <summary>In the component's base unit.</summary>
    public decimal ConsumedQty { get; init; }
    public required string BaseUomCode { get; init; }
}

/// <summary>A job's components against what it consumed, and the lots it produced.</summary>
public sealed record JobMaterialsDto
{
    public required DeclarableJobDto Job { get; init; }
    public IReadOnlyList<JobComponentUseDto> Components { get; init; } = [];
    public IReadOnlyList<LotSummaryDto> Produced { get; init; } = [];

    /// <summary>Everything the job declared produced, in its material's base unit.</summary>
    public decimal ProducedQty { get; init; }
    public string? ProducedUomCode { get; init; }
}

/// <summary>A reason that can be chosen for a write-up, write-off or hold.</summary>
public sealed record ReasonCodeDto
{
    public required Guid Id { get; init; }

    /// <summary><c>inventory_adjustment</c> or <c>hold</c> for inventory.</summary>
    public required string Kind { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool RequiresComment { get; init; }

    /// <summary>For a utilization reason (D-050): <c>runtime</c>, <c>downtime</c> or <c>planned</c>.</summary>
    public string? UtilizationClass { get; init; }

    /// <summary>The names of the groups it is filed under, top first, joined by " › " (D-062); null at the top.</summary>
    public string? GroupPath { get; init; }
}

/// <summary>Something worth knowing about a movement that didn't stop it.</summary>
public sealed record InventoryWarningDto(string Code, string Message);

/// <summary>The lot a movement changed, as it is now.</summary>
public sealed record LotTransactionResponse
{
    public required Guid LotId { get; init; }
    /// <summary>Null for a balance (D-031): show its location instead.</summary>
    public string? LotNumber { get; init; }
    public long Version { get; init; }
    public decimal Quantity { get; init; }
    public required string Status { get; init; }
    public IReadOnlyList<InventoryWarningDto> Warnings { get; init; } = [];
}

/// <summary>One job's remaining need of a component, in the order jobs are needed (D-029).</summary>
public sealed record ShortageLineDto
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobStatus { get; init; }
    public required string LineCode { get; init; }
    public string? WorkOrderNumber { get; init; }

    /// <summary>When the job needs it, by <see cref="TimeSource"/>; null when it has no time.</summary>
    public DateTimeOffset? NeededFrom { get; init; }

    /// <summary>
    /// Where <see cref="NeededFrom"/> comes from (D-058): <c>live</c> (the schedule's projection now), <c>published</c> (the published
    /// start), <c>window</c> (hold-until, or the window of a job planned to a department) or <c>none</c>.
    /// </summary>
    public string TimeSource { get; init; } = "published";

    /// <summary>The published start, shown beside a live time.</summary>
    public DateTimeOffset? PublishedStart { get; init; }

    /// <summary>Planned less consumed, in the component's base unit.</summary>
    public decimal Remaining { get; init; }

    /// <summary>What this job and every earlier one need in total.</summary>
    public decimal Cumulative { get; init; }

    /// <summary>Of this job's need: what available stock covers, what supply due by then covers (D-058), and what neither does.</summary>
    public decimal FromStock { get; init; }
    public decimal FromIncoming { get; init; }
    public decimal Uncovered { get; init; }
}

/// <summary>A component's open demand against its available stock, quantities in its base unit.</summary>
public sealed record MaterialShortageDto
{
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required string BaseUomCode { get; init; }
    public decimal Available { get; init; }

    /// <summary>On hold or in quarantine: not counted, shown because releasing it could cover a shortfall.</summary>
    public decimal Held { get; init; }
    public decimal Demand { get; init; }

    /// <summary>Open expected receipts (D-058), in the base unit: not stock, counted only for jobs needing it after it is due.</summary>
    public decimal Incoming { get; init; }

    /// <summary>The next expected receipt's time; before now when one is overdue.</summary>
    public DateTimeOffset? NextIncomingAt { get; init; }
    public decimal Shortfall { get; init; }
    public Guid? FirstShortJobId { get; init; }
    public string? FirstShortJobNumber { get; init; }
    public DateTimeOffset? FirstShortAt { get; init; }
    public IReadOnlyList<ShortageLineDto> Lines { get; init; } = [];
}

/// <summary>The shortage check for a site (D-029): short materials first.</summary>
public sealed record ShortageReportDto
{
    public required Guid SiteId { get; init; }
    public DateTimeOffset CalculatedAt { get; init; }

    /// <summary>True when jobs were timed by the live schedule, false by their published times (D-058).</summary>
    public bool LiveTiming { get; init; }
    public IReadOnlyList<MaterialShortageDto> Materials { get; init; } = [];

    /// <summary>Planned components left out because their unit doesn't convert to the component's base unit.</summary>
    public IReadOnlyList<InventoryWarningDto> Problems { get; init; } = [];
}

/// <summary>One lot movement in the journal (D-029): the movement, and the lot it happened to.</summary>
public sealed record JournalEntryDto
{
    public required LotEventDto Movement { get; init; }
    public required Guid LotId { get; init; }
    /// <summary>Null for a balance (D-031): show its location instead.</summary>
    public string? LotNumber { get; init; }
    public bool IsBulk { get; init; }
    public string? LocationCode { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
}

/// <summary>A page of the journal, newest first; pass the last entry's time and id back to continue.</summary>
public sealed record JournalPageDto
{
    public IReadOnlyList<JournalEntryDto> Entries { get; init; } = [];
    public bool HasMore { get; init; }
}

/// <summary>A stock count as listed (D-030).</summary>
public sealed record StockCountSummaryDto
{
    public required Guid Id { get; init; }
    public required string CountNumber { get; init; }
    public required Guid LocationId { get; init; }
    public required string LocationCode { get; init; }

    /// <summary><c>open</c>, <c>posted</c> or <c>cancelled</c>.</summary>
    public required string Status { get; init; }
    public int LineCount { get; init; }
    public int CountedLines { get; init; }
    public string? Note { get; init; }
    public required string CreatedBy { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? PostedAt { get; init; }
    public string? PostedBy { get; init; }

    /// <summary>Concurrency token; send it back to record, post or cancel.</summary>
    public long Version { get; init; }
}

/// <summary>One lot in a count, with what it holds now.</summary>
public sealed record StockCountLineDto
{
    public required Guid Id { get; init; }
    public required Guid LotId { get; init; }
    /// <summary>Null for a balance (D-031): show its location instead.</summary>
    public string? LotNumber { get; init; }
    public bool IsBulk { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required string UomCode { get; init; }
    public decimal ExpectedQty { get; init; }
    public decimal? CountedQty { get; init; }
    public DateTimeOffset? CountedAt { get; init; }
    public string? CountedBy { get; init; }
    public decimal CurrentQty { get; init; }
    public required string CurrentStatus { get; init; }

    /// <summary>The lot changed after it was counted: count it again before posting.</summary>
    public bool ChangedSinceCounted { get; init; }
}

public sealed record StockCountDto
{
    public required StockCountSummaryDto Count { get; init; }
    public IReadOnlyList<StockCountLineDto> Lines { get; init; } = [];
}

public sealed record PostStockCountResponse(long Version, int Adjusted);

/// <summary>A reason as the reason-code screen edits it (D-030).</summary>
public sealed record ReasonCodeAdminDto
{
    public required Guid Id { get; init; }
    public Guid? SiteId { get; init; }
    public string? SiteCode { get; init; }
    public required string Kind { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool RequiresComment { get; init; }
    public int SortOrder { get; init; }
    public bool IsActive { get; init; }

    /// <summary>False for a group, which only holds other reasons and is never chosen (D-062).</summary>
    public bool IsSelectable { get; init; }

    /// <summary>For a utilization reason (D-050): <c>runtime</c>, <c>downtime</c> or <c>planned</c>.</summary>
    public string? UtilizationClass { get; init; }

    /// <summary>The group it is filed under; null at the top (D-062).</summary>
    public Guid? ParentId { get; init; }

    /// <summary>Levels above it: 0 at the top.</summary>
    public int Depth { get; init; }

    /// <summary>The names of the groups above it, top first, joined by " › "; null at the top.</summary>
    public string? GroupPath { get; init; }
    public long Version { get; init; }
}

/// <summary>A material's on-hand quantity at the end of a past day (D-030).</summary>
public sealed record StockOnDateDto
{
    public required Guid MaterialId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public string? MaterialClassName { get; init; }
    public required string UomCode { get; init; }
    public decimal Quantity { get; init; }
    public int LotCount { get; init; }
}

/// <summary>A material's on-hand stock by how long ago each lot was received or made.</summary>
public sealed record AgingRowDto
{
    public required Guid MaterialId { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public required string UomCode { get; init; }
    public decimal UpTo30Days { get; init; }
    public decimal UpTo90Days { get; init; }
    public decimal UpTo180Days { get; init; }
    public decimal Over180Days { get; init; }
    public decimal Total { get; init; }
    public DateTimeOffset? OldestAt { get; init; }
}

/// <summary>Supply on its way (D-058). Quantities in the entered unit.</summary>
public sealed record ExpectedReceiptDto
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public decimal Quantity { get; init; }
    public required Guid UomId { get; init; }
    public required string UomCode { get; init; }
    public DateTimeOffset ExpectedAt { get; init; }
    public string? Reference { get; init; }
    public string? Supplier { get; init; }
    public string? Note { get; init; }

    /// <summary><c>open</c>, <c>received</c> or <c>cancelled</c>.</summary>
    public required string Status { get; init; }
    public Guid? ReceivedLotId { get; init; }
    public string? ReceivedLotNumber { get; init; }
    public string? ExternalSystem { get; init; }
    public string? ExternalKey { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public string CreatedBy { get; init; } = "";
    public long Version { get; init; }
}

/// <summary>Sets how a balance at a location flows (D-060).</summary>
public sealed record SetFlowRequest
{
    public long Version { get; init; }
    public string FlowModel { get; init; } = "mixed";
    public int? MinResidenceMinutes { get; init; }
}

/// <summary>Where a balance's withdrawals came from (D-060), worked out from its declared movements by its location's flow model.</summary>
public sealed record BalanceAllocationDto
{
    public required Guid LotId { get; init; }
    public required string FlowModel { get; init; }
    public int? MinResidenceMinutes { get; init; }

    /// <summary>What the estimate rests on, said plainly: the declared movements, not measurements.</summary>
    public required string Basis { get; init; }
    public IReadOnlyList<WithdrawalAllocationDto> Withdrawals { get; init; } = [];
}

public sealed record WithdrawalAllocationDto
{
    public required Guid EventId { get; init; }
    public DateTimeOffset At { get; init; }
    public required string Kind { get; init; }
    public decimal Quantity { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }

    /// <summary><c>exact</c>, <c>estimated</c> or <c>possible</c>.</summary>
    public required string Precision { get; init; }
    public required string Reason { get; init; }
    public IReadOnlyList<AllocationSourceDto> Sources { get; init; } = [];
}

public sealed record AllocationSourceDto
{
    /// <summary>The lot it came from; null for stock that had no lot of its own (received or made straight into the balance, or written up).</summary>
    public Guid? LotId { get; init; }
    public string? LotNumber { get; init; }
    public decimal Quantity { get; init; }
    public required string Precision { get; init; }
}
