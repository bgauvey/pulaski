using Pulaski.Application.Abstractions;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Feed point automation's writes and the reads it makes under its locks (#33, D-079); runs inside the current <see cref="IUnitOfWork"/>.</summary>
public interface IFeedRepository
{
    /// <summary>The feed points below a line, each with whether it has an active consumed counter.</summary>
    Task<IReadOnlyList<FeedPointRow>> ListFeedPointsOfLineAsync(Guid lineId, CancellationToken cancellationToken);

    /// <summary>The standing movements that name the feed points (from or to), counting up to <paramref name="until"/>, in effective order.</summary>
    Task<IReadOnlyList<FeedEvent>> ListFeedEventsAsync(IReadOnlyCollection<Guid> feedPointIds, DateTimeOffset until, CancellationToken cancellationToken);

    /// <summary>The job's components consumed by backflush or by time window.</summary>
    Task<IReadOnlyList<AutoComponent>> ListAutoComponentsAsync(Guid jobId, CancellationToken cancellationToken);

    /// <summary>When the run's previous report counted, before this one; null when this is its first.</summary>
    Task<DateTimeOffset?> GetPreviousReportAtAsync(Guid runId, Guid reportId, DateTimeOffset before, CancellationToken cancellationToken);

    /// <summary>Available lots of the material with stock at the locations, first expiry first, then oldest.</summary>
    Task<IReadOnlyList<Guid>> ListLotsAtAsync(IReadOnlyCollection<Guid> locationIds, Guid materialId, CancellationToken cancellationToken);

    /// <summary>Whether a source (a machine count) has already been posted: consumed from a lot or recorded as short.</summary>
    Task<bool> IsPostedAsync(Guid sourceRef, CancellationToken cancellationToken);

    /// <summary>Inserts a shortfall; one already recorded for the same source, place and material is left as it is.</summary>
    Task InsertShortfallAsync(FeedShortfall shortfall, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>Machine consumed counts at feed points among the equipment, since then, not yet posted, oldest first.</summary>
    Task<IReadOnlyList<CounterRow>> ListUnpostedCountsAsync(IReadOnlyCollection<Guid> equipmentIds, DateTimeOffset since, int take, CancellationToken cancellationToken);

    /// <summary>What was running at a feed point's line at a time, and its counter's unit.</summary>
    Task<CounterContext?> GetCounterContextAsync(Guid feedPointId, DateTimeOffset at, CancellationToken cancellationToken);

    Task<Versioned<FeedShortfall>?> GetShortfallForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<long> UpdateShortfallAsync(FeedShortfall shortfall, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The open shortfalls a source left, locked.</summary>
    Task<IReadOnlyList<Versioned<FeedShortfall>>> LockOpenShortfallsOfAsync(Guid sourceRef, CancellationToken cancellationToken);

    /// <summary>The standing automated consumptions a source made (neither reversed nor reversals), by lot and movement.</summary>
    Task<IReadOnlyList<(Guid LotId, Guid EventId)>> ListAutoConsumptionsAsync(Guid sourceRef, CancellationToken cancellationToken);
}

/// <summary>Feed points, their loads and shortfalls, for screens (#33).</summary>
public interface IFeedReadStore
{
    /// <summary>A site's feed points, with their line; null site = every site.</summary>
    Task<IReadOnlyList<FeedPointDto>> ListFeedPointsAsync(Guid? siteId, CancellationToken cancellationToken);

    /// <summary>The standing movements that name the feed points, in effective order.</summary>
    Task<IReadOnlyList<FeedEvent>> ListFeedEventsAsync(IReadOnlyCollection<Guid> feedPointIds, CancellationToken cancellationToken);

    /// <summary>How the lots are shown: number, balance, location, material, quantity and status.</summary>
    Task<IReadOnlyList<LotSummaryDto>> GetLotsAsync(IReadOnlyCollection<Guid> lotIds, CancellationToken cancellationToken);

    /// <summary>A site's shortfalls in a status, newest first; null site = every site.</summary>
    Task<IReadOnlyList<FeedShortfallDto>> ListShortfallsAsync(Guid? siteId, string status, CancellationToken cancellationToken);

    Task<FeedShortfallDto?> GetShortfallAsync(Guid id, CancellationToken cancellationToken);
}

public sealed record FeedPointRow(Guid Id, string Code, bool HasCounter);

/// <summary>A job's component consumed automatically: <c>backflush</c> or <c>time_window</c>, planned for the job's planned quantity.</summary>
public sealed record AutoComponent(Guid MaterialId, string Method, decimal PlannedQty, Guid UomId);

/// <summary>A machine count of material consumed at a feed point, in the counter's unit (scaled).</summary>
public sealed record CounterRow(Guid Id, DateTimeOffset OccurredAt, Guid FeedPointId, decimal Quantity);

/// <summary>The site and code of a feed point, its line and what ran there at a time, and the unit its counter counts in.</summary>
public sealed record CounterContext(Guid SiteId, string FeedPointCode, Guid? LineId, Guid? RunId, Guid? JobId, Guid? CounterUomId);

/// <summary>What an automated consumption couldn't cover (<c>core.feed_shortfall</c>).</summary>
public sealed record FeedShortfall(
    Guid Id,
    Guid SiteId,
    Guid EquipmentId,
    Guid? JobId,
    Guid? RunId,
    Guid? MaterialId,
    Guid? LotId,
    decimal Quantity,
    Guid? UomId,
    string Source,
    Guid SourceRef,
    DateTimeOffset OccurredAt,
    string Reason,
    string Status = FeedShortfallStatus.Open,
    Guid? ResolvedLotId = null,
    string? ResolvedBy = null,
    DateTimeOffset? ResolvedAt = null,
    string? ResolutionNote = null);

public static class FeedShortfallStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Dismissed = "dismissed";

    public static IReadOnlyList<string> All { get; } = [Open, Resolved, Dismissed];
}
