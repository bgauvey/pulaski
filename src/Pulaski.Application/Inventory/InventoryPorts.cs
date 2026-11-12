using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Filters for stock by material. Null fields don't filter.</summary>
public sealed record StockFilter(
    Guid? SiteId = null,
    string? Search = null,
    Guid? MaterialClassId = null,
    Guid? LocationId = null,
    bool InStockOnly = false);

/// <summary>
/// Filters for finding lots. Without a <see cref="Status"/> only lots with stock on hand are
/// found; ask for <c>consumed</c> or <c>scrapped</c> to see the used-up ones.
/// </summary>
public sealed record LotFilter(
    Guid? SiteId = null,
    string? Search = null,
    Guid? MaterialId = null,
    Guid? LocationId = null,
    string? Status = null,
    DateTimeOffset? ExpiresBefore = null,
    int Offset = 0,
    int Limit = 500,
    LotAttributeMatch? Attribute = null,
    Guid? ContainerId = null);

/// <summary>
/// Lots whose value of an attribute matches (D-056): text containing <see cref="Text"/> (a choice: equal to it), a number or date in
/// the inclusive range, or the yes/no given. Only lots with a value of the attribute match.
/// </summary>
public sealed record LotAttributeMatch(
    Guid AttributeId,
    string? Text = null,
    bool TextContains = false,
    decimal? MinNumber = null,
    decimal? MaxNumber = null,
    DateOnly? MinDate = null,
    DateOnly? MaxDate = null,
    bool? Boolean = null);

/// <summary>Read side for inventory (D-027): DTOs straight from SQL, no locks.</summary>
public interface IInventoryReadStore
{
    /// <summary>Active materials with their stock, by class name then code.</summary>
    Task<IReadOnlyList<MaterialStockDto>> ListStockAsync(StockFilter filter, CancellationToken cancellationToken);

    /// <summary>One material's stock, on-hand lots and balance per location; null when the material doesn't exist.</summary>
    Task<MaterialStockDetailDto?> GetMaterialStockAsync(Guid materialId, Guid? siteId, CancellationToken cancellationToken);

    /// <summary>Lots matching the filter, a page of <see cref="LotFilter.Limit"/> from <see cref="LotFilter.Offset"/>, by material code then lot number.</summary>
    Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(LotFilter filter, CancellationToken cancellationToken);

    Task<LotDto?> GetLotAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every lot downstream (forward) or upstream of the lot, the lot itself first, nearest first.</summary>
    Task<IReadOnlyList<LotTraceNodeDto>> TraceAsync(Guid lotId, bool forward, CancellationToken cancellationToken);

    /// <summary>
    /// A balance's quantity movements in the order they count (D-057), with its location's flow settings (D-060); null when the lot
    /// doesn't exist or isn't a balance.
    /// </summary>
    Task<BalanceHistory?> GetBalanceHistoryAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>Every current genealogy link into the lots (all their parents), for pairing them (#81).</summary>
    Task<IReadOnlyList<GenealogyLink>> ListLinksToAsync(IReadOnlyCollection<Guid> childLotIds, CancellationToken cancellationToken);

    /// <summary>The jobs' standing consumption and production (neither reversed nor reversals), in the order they count (#81).</summary>
    Task<IReadOnlyList<JobDeclaration>> ListJobDeclarationsAsync(IReadOnlyCollection<Guid> jobIds, CancellationToken cancellationToken);

    /// <summary>Released and draft BOMs listing the material, and open jobs planned to consume it; null when it doesn't exist.</summary>
    Task<WhereUsedDto?> WhereUsedAsync(Guid materialId, CancellationToken cancellationToken);

    Task<IReadOnlyList<InventoryLocationDto>> ListLocationsAsync(Guid? siteId, CancellationToken cancellationToken);

    /// <summary>
    /// Open production jobs, at most 200. With <paramref name="materialId"/> the jobs planned to
    /// consume it come first, marked <see cref="DeclarableJobDto.PlansComponent"/>.
    /// </summary>
    Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(
        Guid? siteId, string? search, Guid? materialId, CancellationToken cancellationToken);

    Task<JobMaterialsDto?> GetJobMaterialsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Active, selectable reasons of the kind, enterprise-wide or for the site.</summary>
    Task<IReadOnlyList<ReasonCodeDto>> ListReasonCodesAsync(string kind, Guid? siteId, CancellationToken cancellationToken);

    /// <summary>
    /// The planned components of the site's open production jobs (D-029), one row per job,
    /// component and planned unit, with what the job has consumed of the component. With
    /// <paramref name="equipmentId"/>, only jobs on it or below it; with <paramref name="before"/>,
    /// only jobs needed before then.
    /// </summary>
    Task<IReadOnlyList<ComponentDemandRow>> ListComponentDemandAsync(
        Guid siteId, Guid? equipmentId, DateTimeOffset? before, CancellationToken cancellationToken);

    /// <summary>Available and held stock of the materials in the site, in their base units.</summary>
    Task<IReadOnlyList<MaterialAvailabilityRow>> ListAvailabilityAsync(
        Guid siteId, IReadOnlyCollection<Guid> materialIds, CancellationToken cancellationToken);

    /// <summary>A page of the site's lot movements, newest first (D-029).</summary>
    Task<JournalPageDto> ListMovementsAsync(JournalFilter filter, CancellationToken cancellationToken);

    /// <summary>The site's stock counts, newest first (D-030).</summary>
    Task<IReadOnlyList<StockCountSummaryDto>> ListStockCountsAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>A count with its lines and what each lot holds now; null when it doesn't exist.</summary>
    Task<StockCountDto?> GetStockCountAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every reason, active or not, enterprise-wide and for the site (or every site when null).</summary>
    Task<IReadOnlyList<ReasonCodeAdminDto>> ListAllReasonCodesAsync(Guid? siteId, CancellationToken cancellationToken);

    /// <summary>
    /// The site's lots that existed at <paramref name="at"/> (a movement counting by then), each as it is now with its movements counting after
    /// it (#77): enough to work out its quantity, location and status then.
    /// </summary>
    Task<IReadOnlyList<PastLotRow>> ListPastLotsAsync(Guid siteId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>One lot as it is now with every movement, to work out its state at a time (#77); null when it doesn't exist.</summary>
    Task<LotPastSource?> GetLotPastAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>Codes of locations and containers, by id, for the ids given.</summary>
    Task<IReadOnlyDictionary<Guid, string>> ListCodesAsync(IReadOnlyCollection<Guid> locationOrContainerIds, CancellationToken cancellationToken);

    /// <summary>Each material's on-hand stock by the age of its lots at <paramref name="now"/>.</summary>
    Task<IReadOnlyList<AgingRowDto>> AgingAsync(Guid siteId, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Filters for the movement journal. <see cref="From"/> and <see cref="To"/> bound the time;
/// <see cref="BeforeAt"/> and <see cref="BeforeId"/>, the last entry already read, continue a page.
/// </summary>
public sealed record JournalFilter(
    Guid SiteId,
    DateTimeOffset From,
    DateTimeOffset To,
    string? Kind = null,
    Guid? MaterialId = null,
    Guid? LocationId = null,
    Guid? JobId = null,
    string? Actor = null,
    string? Search = null,
    DateTimeOffset? BeforeAt = null,
    Guid? BeforeId = null,
    int Limit = 200);

/// <summary>A job's planned quantity of one component in one unit, and what it has consumed of it (base unit).</summary>
public sealed record ComponentDemandRow
{
    public required Guid JobId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobStatus { get; init; }
    public required string LineCode { get; init; }

    /// <summary>The line or department the job is on; <see cref="OnGroup"/> for a department, whose jobs aren't projected (D-022).</summary>
    public Guid LineId { get; init; }
    public bool OnGroup { get; init; }
    public string? WorkOrderNumber { get; init; }

    /// <summary>The published start, else the hold-until (or a department job's window start).</summary>
    public DateTimeOffset? NeededFrom { get; init; }
    public DateTimeOffset? PlannedStart { get; init; }
    public required Guid MaterialId { get; init; }
    public required string MaterialCode { get; init; }
    public required string MaterialName { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }

    /// <summary>How many days supply of the material takes to come (#83); null when not set.</summary>
    public int? LeadTimeDays { get; init; }
    public decimal PlannedQty { get; init; }
    public required Guid UomId { get; init; }
    public required string UomCode { get; init; }

    /// <summary>Everything the job has consumed of the component, the same on each of its rows.</summary>
    public decimal ConsumedQty { get; init; }
}

public sealed record MaterialAvailabilityRow
{
    public required Guid MaterialId { get; init; }
    public decimal Available { get; init; }
    public decimal Held { get; init; }
}

/// <summary>
/// Write side: runs inside the current <see cref="IUnitOfWork"/>. Commands lock the job (for
/// consumption and production) and then the lots, in id order.
/// </summary>
public interface IInventoryRepository
{
    Task<Versioned<Lot>?> GetLotForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<Versioned<Lot>?> FindLotForUpdateAsync(Guid siteId, Guid materialId, string lotNumber, CancellationToken cancellationToken);

    /// <summary>The bulk balance of a material that isn't lot-tracked at a location, locked.</summary>
    /// <summary>A mixing container's balance of the material (#93), locked; null when it has none.</summary>
    Task<Versioned<Lot>?> FindContainerBulkLotForUpdateAsync(Guid materialId, Guid containerId, CancellationToken cancellationToken);

    Task<Versioned<Lot>?> FindBulkLotForUpdateAsync(Guid materialId, Guid locationId, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the number, status, quantity, location, supplier lot and expiry.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateLotAsync(Lot lot, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The lots, locked in id order (D-030), each with its version and when it last changed; missing ids are left out.</summary>
    Task<IReadOnlyList<LockedLot>> LockLotsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The lot's latest movement; the caller holds the lot's lock, so it stays the latest.</summary>
    Task<LotEventRecord?> GetLatestEventAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>The on-hand lots with stock at a location, for a count's snapshot.</summary>
    Task<IReadOnlyList<Lot>> ListOnHandLotsAtAsync(Guid locationId, CancellationToken cancellationToken);

    Task<Versioned<StockCount>?> GetStockCountForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>How many counts the site started on <paramref name="day"/>'s UTC date, for numbering.</summary>
    Task<int> CountStockCountsOnAsync(Guid siteId, DateTimeOffset day, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertStockCountAsync(StockCount count, string createdBy, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Writes the status and note, and who posted it when it is posted.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateStockCountAsync(StockCount count, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    Task InsertStockCountLinesAsync(IReadOnlyList<StockCountLine> lines, CancellationToken cancellationToken);

    Task<IReadOnlyList<StockCountLine>> GetStockCountLinesAsync(Guid countId, CancellationToken cancellationToken);

    /// <summary>Writes a line's counted quantity, when and by whom.</summary>
    Task UpdateStockCountLineAsync(StockCountLine line, CancellationToken cancellationToken);

    /// <summary>One movement of a lot (D-063), without locking; null when the lot has no such movement.</summary>
    Task<LotEventDetail?> GetEventAsync(Guid lotId, Guid eventId, CancellationToken cancellationToken);

    /// <summary>
    /// The other half of a two-lot movement (split, merge, transfer): the row on the other lot that names this one, of the same kind and
    /// the opposite quantity, recorded with it (the nearest in time, within a minute).
    /// </summary>
    Task<LotEventDetail?> GetCounterpartAsync(LotEventDetail movement, CancellationToken cancellationToken);

    /// <summary>A lot's movements recorded after <paramref name="after"/>, oldest first: what has happened to it since.</summary>
    Task<IReadOnlyList<LotEventDetail>> ListEventsAfterAsync(Guid lotId, LotEventDetail after, CancellationToken cancellationToken);

    /// <summary>
    /// The lot's movements that count after <paramref name="after"/> and still stand (#79): not reversed and not reversals themselves, in
    /// effective order. What a reversal must not contradict, and what a chain undo takes back.
    /// </summary>
    Task<IReadOnlyList<LotEventDetail>> ListStandingAfterAsync(Guid lotId, LotEventDetail after, CancellationToken cancellationToken);

    /// <summary>The lot movements a production report recorded (D-063), oldest first.</summary>
    Task<IReadOnlyList<LotEventDetail>> ListReportEventsAsync(Guid reportId, CancellationToken cancellationToken);

    /// <summary>Whether the lot still has a declaration of <paramref name="kind"/> against the job that hasn't been reversed.</summary>
    Task<bool> HasDeclarationAsync(Guid lotId, Guid jobId, string kind, CancellationToken cancellationToken);

    /// <summary>
    /// Ends current genealogy edges (D-063) of a basis: between the two lots, or (with one of them null) every edge of the job to or from
    /// the other. They stay as history with <c>superseded_at</c>.
    /// </summary>
    Task SupersedeGenealogyAsync(Guid? parentLotId, Guid? childLotId, string basis, Guid? jobId, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A reason's kind, without locking; null when there is no such reason. The kind never changes.</summary>
    Task<string?> GetReasonKindAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Locks every reason of a kind, in id order, and returns them (D-062): tree changes of a kind serialize.</summary>
    Task<IReadOnlyList<Versioned<ReasonCode>>> LockReasonTreeAsync(string kind, CancellationToken cancellationToken);

    /// <returns>The new row version.</returns>
    Task<long> InsertReasonCodeAsync(ReasonCode reason, CancellationToken cancellationToken);

    /// <summary>Where the reason is referred to (#86), as table names, from <c>core.reason_code_uses</c>; empty when nowhere.</summary>
    Task<IReadOnlyList<string>> ReasonCodeUsesAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Writes the name, comment flag, order, active flag, class and group (parent); the code, kind and group flag never change.</summary>
    /// <returns>The new row version.</returns>
    Task<long> UpdateReasonCodeAsync(ReasonCode reason, CancellationToken cancellationToken);

    /// <summary>Locks the location row, so arrivals at a one-material location serialize (D-032).</summary>
    Task LockLocationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The code of a material other than <paramref name="materialId"/> with stock on hand at the location; null when there is none.</summary>
    Task<string?> OtherMaterialAtAsync(Guid locationId, Guid materialId, CancellationToken cancellationToken);

    /// <summary>The material locked <c>FOR UPDATE</c>, with its version, to change its shelf life.</summary>
    Task<Versioned<InventoryMaterial>?> GetMaterialForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <returns>The material's new row version.</returns>
    Task<long> UpdateShelfLifeAsync(Guid materialId, int? shelfLifeDays, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The site row read <c>FOR SHARE</c>: true when it exists and is a site.</summary>
    Task<bool> IsSiteAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The location's flow settings, locked; null when it doesn't exist (D-060).</summary>
    Task<Versioned<FlowSettings>?> GetFlowSettingsForUpdateAsync(Guid equipmentId, CancellationToken cancellationToken);

    Task<long> UpdateFlowSettingsAsync(FlowSettings settings, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The material with this code; null when none.</summary>
    Task<Guid?> FindMaterialIdByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>The unit with this code; null when none.</summary>
    Task<Guid?> FindUomIdByCodeAsync(string code, CancellationToken cancellationToken);

    /// <summary>The site's equipment with this code (unique within a site); null when none (#72).</summary>
    Task<Guid?> FindLocationIdByCodeAsync(Guid siteId, string code, CancellationToken cancellationToken);

    Task InsertEventAsync(LotEventRow row, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>A location read <c>FOR SHARE</c>; null when the equipment doesn't exist.</summary>
    Task<InventoryLocation?> GetLocationAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>A material read <c>FOR SHARE</c>, with its base unit's code.</summary>
    Task<InventoryMaterial?> GetMaterialAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>The job locked <c>FOR UPDATE</c>, so declarations on one job serialize (D-027).</summary>
    Task<InventoryJob?> GetJobForUpdateAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Locks the jobs <c>FOR UPDATE</c> in id order, in SQL (#79: a chain undo takes its jobs before its lots, D-027).</summary>
    Task LockJobsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken);

    /// <summary>The lots the job has produced (<c>core.lot.produced_by_job_id</c>).</summary>
    Task<IReadOnlyList<Guid>> ListLotsProducedByJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>The lots the job has consumed, each once.</summary>
    Task<IReadOnlyList<Guid>> ListLotsConsumedByJobAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>Adds declared edges, taking effect at <paramref name="effectiveAt"/> (else <paramref name="at"/>); an edge already there is left alone.</summary>
    Task InsertGenealogyAsync(IReadOnlyList<GenealogyEdge> edges, DateTimeOffset at, CancellationToken cancellationToken, DateTimeOffset? effectiveAt = null);

    /// <summary>The lot's quantity movements in the order they count: by effective time, then record time (D-057).</summary>
    Task<IReadOnlyList<QuantityPoint>> ListQuantityTimelineAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>Where the container is now; null when it doesn't exist (#79, putting a lot back into it).</summary>
    Task<Guid?> GetContainerLocationAsync(Guid containerId, CancellationToken cancellationToken);

    /// <summary>The lot's movements as its state at a past time is worked out from (#77), in the command's transaction.</summary>
    Task<IReadOnlyList<PastEvent>> ListPastEventsAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>When the lot was counted in the latest posted stock count that counted it (its line's counted time, #77); null when none did.</summary>
    Task<DateTimeOffset?> LatestCountOfLotAsync(Guid lotId, CancellationToken cancellationToken);

    /// <summary>
    /// Carries a lot's documents to a lot made from it (D-056), linked <paramref name="via"/> <c>split</c> or <c>merge</c>; with
    /// <paramref name="withAttributes"/> (a split: the same material) its attribute values too. What the other lot has stays.
    /// </summary>
    Task CarryLotRecordsAsync(Guid fromLotId, Guid toLotId, string via, bool withAttributes, string actor, DateTimeOffset at, CancellationToken cancellationToken);

    Task<InventoryReason?> GetReasonAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>True when the job has a planned component (<c>consume</c>) of the material.</summary>
    Task<bool> PlansComponentAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken);

    /// <summary>The code of the job's planned component this material is an approved alternate for on its BOM line (#82); null when none.</summary>
    Task<string?> AlternateForAsync(Guid jobId, Guid materialId, CancellationToken cancellationToken);
}

/// <summary>A row of <c>ts.lot_event</c>: the lot's movement plus who, why and for which job.</summary>
public sealed record LotEventRow(
    Guid Id,
    Guid LotId,
    LotMovement Movement,
    Guid UomId,
    string ActorId,
    Guid? JobId = null,
    Guid? ReasonCodeId = null,
    string? Comment = null,
    Guid? RelatedLotId = null,
    string? Reference = null,
    Guid? ReversesEventId = null,
    DateTimeOffset? ReversesOccurredAt = null,
    Guid? RunId = null,
    DateTimeOffset? EffectiveAt = null,
    Guid? ContainerId = null,
    Guid? ReportId = null,
    string? Details = null,
    string? AutoSource = null,
    Guid? SourceRef = null);

/// <summary>
/// One movement of a lot as reversing it needs it (D-063): what it did, what it belonged to, and the reversal of it, if any.
/// </summary>
public sealed record LotEventDetail
{
    public required Guid Id { get; init; }
    public required Guid LotId { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public DateTimeOffset EffectiveAt { get; init; }
    public required LotMovement Movement { get; init; }
    public Guid? RelatedLotId { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }
    public Guid? RunId { get; init; }
    public Guid? ReportId { get; init; }
    public Guid? ReasonCodeId { get; init; }

    /// <summary>The reversal that undid it; null while it stands.</summary>
    public Guid? ReversedBy { get; init; }

    /// <summary>The movement it reverses, when it is a reversal (whatever its kind, #79).</summary>
    public Guid? ReversesEventId { get; init; }

    /// <summary>The container it packed into, unpacked from or moved with (D-059).</summary>
    public Guid? ContainerId { get; init; }

    /// <summary>A correction's lot details before and after it, as JSON (#79); null for other movements and older corrections.</summary>
    public string? Details { get; init; }
}

/// <summary>A parent-to-child lot edge: <c>declared</c> by a job, or from a <c>split</c> or <c>merge</c> (D-030, no job).</summary>
public sealed record GenealogyEdge(Guid Id, Guid ParentLotId, Guid ChildLotId, Guid? JobId, string Basis = GenealogyEdge.Declared)
{
    public const string Declared = "declared";

    /// <summary>Picked as what a made lot was made from, out of the lots its job used (#81).</summary>
    public const string MadeFrom = "made_from";
    public const string Split = "split";
    public const string Merge = "merge";
}

/// <summary>A lot locked for a command, with its version and when it last changed.</summary>
public sealed record LockedLot(Lot Lot, long Version, DateTimeOffset UpdatedAt);

/// <summary>A movement as recorded, for reversing it.</summary>
/// <summary>A lot movement as reversing it needs it; <c>EffectiveAt</c> is when it counts in stock (D-057), its record time unless back-dated.</summary>
public sealed record LotEventRecord(Guid Id, DateTimeOffset OccurredAt, LotMovement Movement, Guid? RelatedLotId, DateTimeOffset? EffectiveAt = null);

/// <summary>A balance's movements and how its location flows (D-060), with its location's level readings (#81) when it has a level tag.</summary>
public sealed record BalanceHistory(Guid LotId, string FlowModel, TimeSpan? MinResidence, IReadOnlyList<BalanceMovementRow> Movements, BalanceLevelData? Levels = null);

/// <summary>
/// A balance's location's level readings (#81), in the level tag's unit: when it read empty (at or below <c>EmptyLevel</c>, from the
/// first reading of each empty stretch) and, with a tolerance set, the last good reading in the 15 minutes before each withdrawal.
/// </summary>
public sealed record BalanceLevelData(
    Guid MaterialId,
    Guid BaseUomId,
    Guid? LevelUomId,
    decimal? EmptyLevel,
    decimal? Tolerance,
    IReadOnlyList<EmptyPoint> Empties,
    IReadOnlyDictionary<Guid, decimal> ReadingBefore);

/// <summary>A current genealogy link to a lot: its parent, its basis (<see cref="GenealogyEdge"/>) and the job that made it.</summary>
public sealed record GenealogyLink(Guid ParentLotId, Guid ChildLotId, string Basis, Guid? JobId);

/// <summary>One movement of a balance, with the names screens show.</summary>
public sealed record BalanceMovementRow
{
    public required Guid EventId { get; init; }
    public DateTimeOffset At { get; init; }
    public decimal Delta { get; init; }
    public required string Kind { get; init; }
    public Guid? SourceLotId { get; init; }
    public string? SourceLotNumber { get; init; }
    public Guid? JobId { get; init; }
    public string? JobNumber { get; init; }
}

/// <summary>A location's flow settings (D-060), as changing them needs them.</summary>
/// <remarks><c>EmptyLevel</c> and <c>LevelTolerance</c> are in the level tag's unit (#81): a reading at or below empty means empty; null tolerance = not compared.</remarks>
public sealed record FlowSettings(
    Guid EquipmentId, Guid? SiteId, string Code, bool HoldsInventory, string FlowModel, TimeSpan? MinResidence, decimal? EmptyLevel = null, decimal? LevelTolerance = null);

/// <summary>One quantity movement of a lot, by when it counts and when it was recorded (D-057).</summary>
public sealed record QuantityPoint(DateTimeOffset EffectiveAt, DateTimeOffset OccurredAt, decimal Delta);

public sealed record InventoryLocation
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public Guid? SiteId { get; init; }
    public bool HoldsInventory { get; init; }

    /// <summary>Lots here mix into one balance per material (D-031).</summary>
    public bool ComminglesLots { get; init; }

    /// <summary>Takes one material at a time (D-032).</summary>
    public bool SingleMaterial { get; init; }
    public bool IsActive { get; init; }

    /// <summary>A feed point (#33): where lots are loaded onto a line.</summary>
    public bool IsFeedPoint { get; init; }
}

public sealed record InventoryMaterial
{
    public required Guid Id { get; init; }
    public required string Code { get; init; }
    public required Guid BaseUomId { get; init; }
    public required string BaseUomCode { get; init; }
    public bool IsLotTracked { get; init; }
    public bool IsActive { get; init; }

    /// <summary>A new lot expires this many days after it arrives (D-032); null: no shelf life.</summary>
    public int? ShelfLifeDays { get; init; }

    public decimal? BatchSize { get; init; }
    public decimal BatchTolerancePct { get; init; } = 2m;
    public string? BatchRule { get; init; }

    /// <summary>How it is made in batches (D-036); null when made continuously.</summary>
    public Domain.Materials.Batching? Batching => BatchSize is { } size ? new(size, BatchTolerancePct, BatchRule ?? Domain.Materials.BatchRule.Near) : null;
}

public sealed record InventoryJob
{
    public required Guid Id { get; init; }
    public required Guid SiteId { get; init; }
    public required string JobNumber { get; init; }
    public required string JobType { get; init; }
    public required string Status { get; init; }
    public Guid? MaterialId { get; init; }

    /// <summary>The line it is on, and its code, for execution checks (D-035).</summary>
    public Guid EquipmentId { get; init; }
    public string LineCode { get; init; } = "";
    public decimal? PlannedQty { get; init; }
    public Guid? UomId { get; init; }

    /// <summary>Planned with a partial last batch (D-067): it is reported without a stop reason.</summary>
    public bool PartialBatchPlanned { get; init; }
}

public sealed record InventoryReason
{
    public required Guid Id { get; init; }
    public Guid? SiteId { get; init; }
    public required string Kind { get; init; }
    public required string Code { get; init; }
    public required string Name { get; init; }
    public bool IsSelectable { get; init; }
    public bool RequiresComment { get; init; }
    public bool IsActive { get; init; }
}

/// <summary>Reason kinds inventory uses. Values match <c>core.reason_code.reason_kind</c>.</summary>
public static class ReasonKinds
{
    public const string InventoryAdjustment = "inventory_adjustment";
    public const string Hold = "hold";
    public const string Scrap = "scrap";
    public const string RunEnd = "run_end";
    public const string Utilization = "utilization";
    public const string SampleExclusion = "sample_exclusion";
    public const string SpcCause = "spc_cause";
}

/// <summary>A lot as Stock on a date works its past out from (#77): what it is now, with its movements counting after the time.</summary>
public sealed record PastLotRow(
    Guid LotId, Guid MaterialId, string MaterialCode, string MaterialName, string? MaterialClassName, string UomCode, LotNow Now, IReadOnlyList<PastEvent> EventsAfter);

/// <summary>A lot with every movement, for its state at a time (#77).</summary>
public sealed record LotPastSource(Guid LotId, Guid SiteId, string UomCode, LotNow Now, IReadOnlyList<PastEvent> Events);
