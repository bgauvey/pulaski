using Dapper;
using Npgsql;
using Pulaski.Application.Integrations;
using Pulaski.Domain.Integrations;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Integrations;

/// <summary>A feed's saved settings (#73): reads on their own connection, saving in the command's transaction.</summary>
internal sealed class DeliveryFeedStore(DbSession session, NpgsqlDataSource dataSource) : IDeliveryFeedStore
{
    internal const string Columns = """
        feed, name, kind, enabled, url, topic, event_kinds, include_files, max_attempts, timeout_seconds, poll_seconds, start_from_now, updated_at, updated_by,
        xmin::text::bigint AS version
        """;

    internal const string Select = $"SELECT {Columns} FROM core.delivery_feed WHERE feed = @Feed";

    internal const string All = $"SELECT {Columns} FROM core.delivery_feed ORDER BY feed";

    private const string Insert = """
        INSERT INTO core.delivery_feed (feed, name, kind, enabled, url, topic, event_kinds, include_files, max_attempts, timeout_seconds, poll_seconds,
                                        start_from_now, created_at, updated_at, updated_by)
        VALUES (@Feed, @Name, @Kind, @Enabled, @Url, @Topic, @EventKinds, @IncludeFiles, @MaxAttempts, @TimeoutSeconds, @PollSeconds,
                @StartFromNow, @At, @At, @Actor)
        ON CONFLICT (feed) DO NOTHING
        RETURNING true
        """;

    private const string Ensure = """
        INSERT INTO core.delivery_feed (feed, name, kind, enabled, url, topic, event_kinds, include_files, max_attempts, timeout_seconds, poll_seconds,
                                        start_from_now)
        VALUES (@Feed, @Name, @Kind, @Enabled, @Url, @Topic, @EventKinds, @IncludeFiles, @MaxAttempts, @TimeoutSeconds, @PollSeconds, @StartFromNow)
        ON CONFLICT (feed) DO NOTHING
        RETURNING true
        """;

    private const string Save = """
        UPDATE core.delivery_feed
        SET name = @Name, enabled = @Enabled, url = @Url, topic = @Topic, event_kinds = @EventKinds, include_files = @IncludeFiles, max_attempts = @MaxAttempts,
            timeout_seconds = @TimeoutSeconds, poll_seconds = @PollSeconds, start_from_now = @StartFromNow, updated_at = @At, updated_by = @Actor
        WHERE feed = @Feed
        RETURNING xmin::text::bigint
        """;

    public async Task<StoredFeedSettings?> GetAsync(string feed, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return (await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(Select, new { Feed = feed }, cancellationToken: cancellationToken)))?.ToStored();
    }

    public async Task<StoredFeedSettings> GetForUpdateAsync(string feed, DeliveryFeedSettings defaults, CancellationToken cancellationToken)
    {
        var created = await session.QuerySingleOrDefaultAsync<bool?>(Ensure, Args(defaults), cancellationToken) == true;
        var row = (await session.QuerySingleOrDefaultAsync<Row>(Select + "\nFOR UPDATE", new { Feed = feed }, cancellationToken))!;
        return row.ToStored() with { Version = created ? 0 : row.Version };
    }

    public Task<long> SaveAsync(DeliveryFeedSettings settings, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<long>(
            Save,
            new
            {
                settings.Feed,
                settings.Name,
                settings.Enabled,
                settings.Url,
                settings.Topic,
                EventKinds = settings.EventKinds.ToArray(),
                settings.IncludeFiles,
                settings.MaxAttempts,
                settings.TimeoutSeconds,
                settings.PollSeconds,
                settings.StartFromNow,
                Actor = actor,
                At = at,
            },
            cancellationToken);

    public async Task<IReadOnlyList<StoredFeedSettings>> ListAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. (await connection.QueryAsync<Row>(new CommandDefinition(All, cancellationToken: cancellationToken))).Select(r => r.ToStored())];
    }

    public async Task<bool> InsertAsync(DeliveryFeedSettings settings, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.QuerySingleOrDefaultAsync<bool?>(
            Insert,
            new
            {
                settings.Feed,
                settings.Name,
                settings.Kind,
                settings.Enabled,
                settings.Url,
                settings.Topic,
                EventKinds = settings.EventKinds.ToArray(),
                settings.IncludeFiles,
                settings.MaxAttempts,
                settings.TimeoutSeconds,
                settings.PollSeconds,
                settings.StartFromNow,
                Actor = actor,
                At = at,
            },
            cancellationToken) == true;

    private static object Args(DeliveryFeedSettings s) => new
    {
        s.Feed,
        s.Name,
        s.Kind,
        s.Enabled,
        s.Url,
        s.Topic,
        EventKinds = s.EventKinds.ToArray(),
        s.IncludeFiles,
        s.MaxAttempts,
        s.TimeoutSeconds,
        s.PollSeconds,
        s.StartFromNow,
    };

    internal sealed class Row
    {
        public string Feed { get; init; } = "";
        public string Name { get; init; } = "";
        public string Kind { get; init; } = DeliveryFeedKinds.Webhook;
        public bool Enabled { get; init; }
        public string? Url { get; init; }
        public string? Topic { get; init; }
        public string[] EventKinds { get; init; } = [];
        public bool IncludeFiles { get; init; }
        public int MaxAttempts { get; init; }
        public int TimeoutSeconds { get; init; }
        public int PollSeconds { get; init; }
        public bool StartFromNow { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public string? UpdatedBy { get; init; }
        public long Version { get; init; }

        public DeliveryFeedSettings ToSettings() =>
            new(Feed, Enabled, Url, EventKinds, IncludeFiles, MaxAttempts, TimeoutSeconds, PollSeconds, StartFromNow, Name, Kind, Topic);

        public StoredFeedSettings ToStored() => new(ToSettings(), Version, UpdatedAt, UpdatedBy);
    }
}
