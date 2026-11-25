using System.Collections.Concurrent;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Localization;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Health;

namespace Pulaski.Application.Health;

/// <summary>What a health item is (#141).</summary>
public static class HealthStatus
{
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Failing = "failing";
    public const string Off = "off";

    /// <summary>Worse first: failing, warning, ok, off.</summary>
    public static int Rank(string status) => status switch { Failing => 0, Warning => 1, Ok => 2, _ => 3 };

    public static string Worst(IEnumerable<string> statuses) => statuses.OrderBy(Rank).FirstOrDefault() is { } worst && worst != Off ? worst : Ok;
}

/// <summary>The background services that report their rounds (#141), by the key the health page names them with.</summary>
public static class BackgroundServices
{
    public const string Partitions = "partitions";
    public const string Deliveries = "deliveries";
    public const string DeliveryRetention = "delivery_retention";
    public const string OeeRollups = "oee_rollups";
    public const string AllocationRefresh = "allocation_refresh";
    public const string RateRefresh = "rate_refresh";
    public const string MaintenancePlans = "maintenance_plans";
    public const string TagSampling = "tag_sampling";
    public const string Connections = "connections";
    public const string QualificationNotices = "qualification_notices";
    public const string Alerts = "alerts";
    public const string Mail = "mail";
    public const string Live = "live";
}

/// <summary>A background service's rounds since the API started (#141): how often it means to run, its last success and its last failure.</summary>
public sealed record ServiceRound(string Key, bool Enabled, TimeSpan? Every, DateTimeOffset? StartedAt, DateTimeOffset? SucceededAt, DateTimeOffset? FailedAt, string? Error);

/// <summary>
/// Where background services say how their rounds went (#141, D-118): in memory, per API process, from its start. A service says it started (with
/// how often it runs; null for one that runs continuously), that it is switched off, and each success or failure.
/// </summary>
public sealed class ServiceHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, ServiceRound> _rounds = new(StringComparer.Ordinal);

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public void Started(string key, TimeSpan? every) =>
        _rounds.AddOrUpdate(key, _ => new ServiceRound(key, true, every, time.GetUtcNow(), null, null, null), (_, r) => r with { Enabled = true, Every = every });

    public void Disabled(string key) => _rounds[key] = new ServiceRound(key, false, null, null, null, null, null);

    public void Succeeded(string key) =>
        _rounds.AddOrUpdate(key, _ => new ServiceRound(key, true, null, time.GetUtcNow(), time.GetUtcNow(), null, null), (_, r) => r with { SucceededAt = time.GetUtcNow() });

    public void Failed(string key, string error) =>
        _rounds.AddOrUpdate(
            key, _ => new ServiceRound(key, true, null, time.GetUtcNow(), null, time.GetUtcNow(), Short(error)), (_, r) => r with { FailedAt = time.GetUtcNow(), Error = Short(error) });

    public IReadOnlyList<ServiceRound> Snapshot() => [.. _rounds.Values.OrderBy(r => r.Key, StringComparer.Ordinal)];

    private static string Short(string error) => error.Length <= 300 ? error : error[..300] + "…";
}

/// <summary>When something is unhealthy (#141). Pure: each gives a status and what was found.</summary>
public static class HealthRules
{
    public static readonly TimeSpan EdgeQuiet = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan EdgeLost = TimeSpan.FromHours(1);
    public static readonly TimeSpan DeliveryLate = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ScheduleLate = TimeSpan.FromHours(1);

    /// <summary>A service: off when switched off; failing when its last round failed; a warning when it hasn't succeeded for three rounds.</summary>
    public static (string Status, MessageDto Message) Service(ServiceRound round, DateTimeOffset now)
    {
        if (!round.Enabled)
        {
            return (HealthStatus.Off, Notes.Of($"Switched off in configuration."));
        }

        if (round.FailedAt is { } failed && (round.SucceededAt is not { } ok || failed > ok))
        {
            return (HealthStatus.Failing, Notes.Of($"Its last round failed: {round.Error ?? ""}"));
        }

        var since = round.SucceededAt ?? round.StartedAt;
        if (round.Every is { } every && since is { } from && now - from > every * 3)
        {
            return round.SucceededAt is null
                ? (HealthStatus.Warning, Notes.Of($"No round has finished since it started."))
                : (HealthStatus.Warning, Notes.Of($"No round has finished for {Minutes(now - from)} minutes; it runs every {Minutes(every)} minutes."));
        }

        return round.SucceededAt is null
            ? (HealthStatus.Ok, Notes.Of($"Running; no round has finished yet."))
            : (HealthStatus.Ok, Notes.Of($"Running."));
    }

    /// <summary>An edge node: heard from within 2 minutes; quiet up to an hour; lost after that; never connected is a warning.</summary>
    public static (string Status, MessageDto Message) EdgeNode(bool isActive, DateTimeOffset? lastSeen, DateTimeOffset now) => (isActive, lastSeen) switch
    {
        (false, _) => (HealthStatus.Off, Notes.Of($"Switched off.")),
        (_, null) => (HealthStatus.Warning, Notes.Of($"It has never connected.")),
        (_, { } seen) when now - seen <= EdgeQuiet => (HealthStatus.Ok, Notes.Of($"Sending.")),
        (_, { } seen) when now - seen <= EdgeLost => (HealthStatus.Warning, Notes.Of($"Quiet for {Minutes(now - seen)} minutes.")),
        (_, { } seen) => (HealthStatus.Failing, Notes.Of($"Not heard from for {Hours(now - seen)} hours.")),
    };

    /// <summary>A feed: failing with failed deliveries (which hold their subjects back); a warning when its oldest waiting delivery is 15 minutes old.</summary>
    public static (string Status, MessageDto Message) Feed(bool enabled, int pending, DateTimeOffset? oldestPending, int failed, DateTimeOffset now)
    {
        if (failed > 0)
        {
            return failed == 1
                ? (HealthStatus.Failing, Notes.Of($"1 delivery failed; it holds back later ones for the same subject."))
                : (HealthStatus.Failing, Notes.Of($"{failed} deliveries failed; they hold back later ones for the same subjects."));
        }

        if (!enabled)
        {
            return pending == 0 ? (HealthStatus.Off, Notes.Of($"Switched off.")) : (HealthStatus.Warning, Notes.Of($"Switched off with {pending} waiting."));
        }

        if (oldestPending is { } oldest && now - oldest > DeliveryLate)
        {
            return (HealthStatus.Warning, Notes.Of($"{pending} waiting, the oldest for {Minutes(now - oldest)} minutes."));
        }

        return pending == 0 ? (HealthStatus.Ok, Notes.Of($"Nothing waiting.")) : (HealthStatus.Ok, Notes.Of($"{pending} waiting."));
    }

    /// <summary>A scheduled connection: failing when its last run failed; a warning when it didn't apply, or its run is an hour overdue.</summary>
    public static (string Status, MessageDto Message) Connection(bool isActive, bool scheduled, string? lastOutcome, DateTimeOffset? nextRun, DateTimeOffset now)
    {
        if (!isActive)
        {
            return (HealthStatus.Off, Notes.Of($"Switched off."));
        }

        return lastOutcome switch
        {
            "failed" => (HealthStatus.Failing, Notes.Of($"Its last run failed.")),
            "not_applied" => (HealthStatus.Warning, Notes.Of($"Its last run found problems and changed nothing.")),
            _ when scheduled && nextRun is { } next && now - next > ScheduleLate => (HealthStatus.Warning, Notes.Of($"Its scheduled run is {Hours(now - next)} hours late.")),
            null => (HealthStatus.Ok, Notes.Of($"Not run yet.")),
            _ => (HealthStatus.Ok, Notes.Of($"Its last run applied.")),
        };
    }

    private static int Minutes(TimeSpan span) => (int)Math.Floor(span.TotalMinutes);

    private static int Hours(TimeSpan span) => (int)Math.Floor(span.TotalHours);
}

/// <summary>What the health page reads from the database (#141).</summary>
public interface ISystemHealthReadStore
{
    /// <summary>The latest migration applied, and the one this API needs.</summary>
    Task<(string? Applied, string Required, bool HasRequired)> MigrationAsync(CancellationToken cancellationToken);

    /// <summary>The history tables with no partition for next month yet.</summary>
    Task<IReadOnlyList<string>> MissingPartitionsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<(Guid Id, string Code, string SiteCode, bool IsActive, DateTimeOffset? LastSeenAt)>> EdgeNodesAsync(CancellationToken cancellationToken);

    /// <summary>Each feed (configured, or with deliveries): switched on, waiting, its oldest waiting, failed.</summary>
    Task<IReadOnlyList<(string Feed, bool Enabled, int Pending, DateTimeOffset? OldestPending, int Failed)>> FeedsAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<(string Code, string Target, bool IsActive, bool Scheduled, string? LastOutcome, DateTimeOffset? LastRunAt, DateTimeOffset? NextRunAt)>> ConnectionsAsync(
        CancellationToken cancellationToken);

    /// <summary>Production days of each site changed more than a day ago and not rolled up since, with the oldest.</summary>
    Task<IReadOnlyList<(string SiteCode, int Days, DateOnly Oldest)>> StaleRollupsAsync(CancellationToken cancellationToken);
}

/// <summary>Everything the health page shows (#141), worked out now.</summary>
public sealed record GetSystemHealthQuery : IQuery<SystemHealthDto>;

internal sealed class GetSystemHealthHandler(ISystemHealthReadStore store, ServiceHealth services, IMailOutbox mail, TimeProvider time)
    : IQueryHandler<GetSystemHealthQuery, SystemHealthDto>
{
    public async Task<SystemHealthDto> HandleAsync(GetSystemHealthQuery query, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var sections = new List<HealthSectionDto>
        {
            Section("database", await DatabaseAsync(cancellationToken)),
            Section("services", [.. services.Snapshot().Where(r => r.Key != BackgroundServices.Mail).Select(r => Item(r.Key, HealthRules.Service(r, now), r.SucceededAt ?? r.FailedAt, null))]),
            Section("edge", [.. (await store.EdgeNodesAsync(cancellationToken)).Select(n =>
                Item($"{n.SiteCode} · {n.Code}", HealthRules.EdgeNode(n.IsActive, n.LastSeenAt, now), n.LastSeenAt, $"edge-nodes/{n.Id}"))]),
            Section("deliveries", [.. (await store.FeedsAsync(cancellationToken)).Select(f =>
                Item(f.Feed, HealthRules.Feed(f.Enabled, f.Pending, f.OldestPending, f.Failed, now), f.OldestPending, "integrations"))]),
            Section("connections", [.. (await store.ConnectionsAsync(cancellationToken)).Select(c =>
                Item(c.Code, HealthRules.Connection(c.IsActive, c.Scheduled, c.LastOutcome, c.NextRunAt, now), c.LastRunAt, c.Target == "people" ? "people" : "materials"))]),
            Section("rollups", [.. (await store.StaleRollupsAsync(cancellationToken)).Select(r => Item(
                r.SiteCode,
                (HealthStatus.Warning, r.Days == 1
                    ? Notes.Of($"1 production day isn't rolled up yet ({r.Oldest:yyyy-MM-dd}); long OEE reports work it out from the history.")
                    : Notes.Of($"{r.Days} production days aren't rolled up yet, since {r.Oldest:yyyy-MM-dd}; long OEE reports work them out from the history.")),
                null,
                "machines?tab=reports"))]),
            Section("mail", [Mail(services.Snapshot().FirstOrDefault(r => r.Key == BackgroundServices.Mail), now)]),
        };

        return new SystemHealthDto
        {
            CheckedAt = now,
            StartedAt = services.StartedAt,
            Status = HealthStatus.Worst(sections.Select(s => s.Status)),
            Sections = sections,
        };
    }

    private async Task<IReadOnlyList<HealthItemDto>> DatabaseAsync(CancellationToken cancellationToken)
    {
        var (applied, required, hasRequired) = await store.MigrationAsync(cancellationToken);
        var missing = await store.MissingPartitionsAsync(cancellationToken);
        return
        [
            Item("migration", hasRequired
                ? (HealthStatus.Ok, Notes.Of($"At {applied ?? required}, as this version needs."))
                : (HealthStatus.Failing, Notes.Of($"At {applied ?? "?"}; this version needs {required}. Run db/migrate.sh.")), null, null),
            Item("partitions", missing.Count == 0
                ? (HealthStatus.Ok, Notes.Of($"Every history table has next month's partition."))
                : (HealthStatus.Failing, Notes.Of($"No partition for next month yet: {string.Join(", ", missing)}. Writes there will fail once the month starts.")), null, null),
        ];
    }

    private HealthItemDto Mail(ServiceRound? round, DateTimeOffset now)
    {
        if (!mail.CanSend)
        {
            return Item("smtp", (HealthStatus.Off, Notes.Of($"No mail server is set up: alerts, password resets and notices aren't e-mailed.")), null, null);
        }

        if (round is { FailedAt: { } failed } && (round.SucceededAt is not { } sent || failed > sent))
        {
            return Item("smtp", (HealthStatus.Failing, Notes.Of($"The last e-mail couldn't be sent: {round.Error ?? ""}")), failed, null);
        }

        return Item("smtp", (HealthStatus.Ok, round?.SucceededAt is null ? Notes.Of($"Set up; nothing sent yet.") : Notes.Of($"Sending.")), round?.SucceededAt, null);
    }

    private static HealthItemDto Item(string name, (string Status, MessageDto Message) found, DateTimeOffset? at, string? link) =>
        new() { Name = name, Status = found.Status, Message = found.Message, At = at, Link = link };

    private static HealthSectionDto Section(string key, IReadOnlyList<HealthItemDto> items) => new()
    {
        Key = key,
        Status = HealthStatus.Worst(items.Select(i => i.Status)),
        Items = [.. items.OrderBy(i => HealthStatus.Rank(i.Status)).ThenBy(i => i.Name, StringComparer.Ordinal)],
    };
}
