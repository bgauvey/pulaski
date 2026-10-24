using Dapper;
using Npgsql;
using Pulaski.Application.Integrations;
using Pulaski.Contracts.Integrations;
using Pulaski.Domain.Integrations;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Integrations;

/// <summary>SQL shared by the operators' store and the relay (D-061).</summary>
internal static class DeliverySql
{
    // An earlier undelivered event of the same subject holds a delivery back, so the receiver sees a subject's events in order.
    public const string HeldBy = """
        EXISTS (SELECT 1 FROM evt.delivery p
                WHERE p.sink = d.sink AND p.subject = d.subject AND p.status IN ('pending','failed')
                  AND (p.tx_id, p.position) < (d.tx_id, d.position))
        """;
}

/// <summary>Operators' side of outbound deliveries (D-061): reads on their own connection, changes in the command's transaction.</summary>
internal sealed class DeliveryStore(DbSession session, NpgsqlDataSource dataSource) : IDeliveryStore
{
    private const string List = $"""
        SELECT d.id, d.sink, d.event_id, d.event_type, d.subject, d.event_recorded_at AS occurred_at, d.status, d.attempts, d.next_attempt_at,
               d.last_attempt_at, d.last_status_code, d.last_error, d.delivered_at, d.resolved_by, d.resolve_note, d.created_at,
               (d.status = 'pending' AND {DeliverySql.HeldBy}) AS is_held,
               l.lot_number, coalesce(l.is_bulk, false) AS is_bulk, loc.code AS location_code
        FROM evt.delivery d
        LEFT JOIN core.lot l ON l.id = d.subject
        LEFT JOIN core.equipment loc ON loc.id = l.location_equipment_id
        WHERE d.sink = @Sink AND (@Status::text IS NULL OR d.status = @Status)
        ORDER BY d.tx_id DESC, d.position DESC
        LIMIT @Limit
        """;

    private const string Counts = $"""
        SELECT count(*) FILTER (WHERE d.status = 'pending')::int AS pending,
               count(*) FILTER (WHERE d.status = 'failed')::int AS failed,
               count(*) FILTER (WHERE d.status = 'pending' AND {DeliverySql.HeldBy})::int AS held,
               count(*) FILTER (WHERE d.status = 'delivered' AND d.delivered_at > @Now - interval '1 day')::int AS delivered_last_day,
               min(d.created_at) FILTER (WHERE d.status = 'pending') AS oldest_pending_at,
               max(d.delivered_at) AS last_delivered_at
        FROM evt.delivery d
        WHERE d.sink = @Sink
        """;

    private const string GetForUpdate = """
        SELECT id, sink, event_id, event_type, subject, status, attempts FROM evt.delivery WHERE id = @Id FOR UPDATE
        """;

    private const string Resolve = """
        UPDATE evt.delivery SET status = @Status, next_attempt_at = @At, resolved_by = @Actor, resolve_note = @Note, updated_at = @At
        WHERE id = @Id
        """;

    private const string RetryFailed = """
        UPDATE evt.delivery SET status = 'pending', next_attempt_at = @At, resolved_by = @Actor, resolve_note = NULL, updated_at = @At
        WHERE sink = @Sink AND status = 'failed'
        """;

    public async Task<IReadOnlyList<DeliveryDto>> ListAsync(string sink, string? status, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<DeliveryDto>(new CommandDefinition(List, new { Sink = sink, Status = status, Limit = limit }, cancellationToken: cancellationToken))];
    }

    public async Task<DeliveryCounts> CountAsync(string sink, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleAsync<CountRow>(new CommandDefinition(Counts, new { Sink = sink, Now = now }, cancellationToken: cancellationToken));
        return new DeliveryCounts(row.Pending, row.Failed, row.Held, row.DeliveredLastDay, row.OldestPendingAt, row.LastDeliveredAt);
    }

    public Task<DeliveryRecord?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken) =>
        session.QuerySingleOrDefaultAsync<DeliveryRecord>(GetForUpdate, new { Id = id }, cancellationToken);

    public Task ResolveAsync(Guid id, string status, string actor, string? note, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(Resolve, new { Id = id, Status = status, Actor = actor, Note = note, At = at }, cancellationToken);

    public Task<int> RetryFailedAsync(string sink, string actor, DateTimeOffset at, CancellationToken cancellationToken) =>
        session.ExecuteAsync(RetryFailed, new { Sink = sink, Actor = actor, At = at }, cancellationToken);

    private sealed class CountRow
    {
        public int Pending { get; init; }
        public int Failed { get; init; }
        public int Held { get; init; }
        public int DeliveredLastDay { get; init; }
        public DateTimeOffset? OldestPendingAt { get; init; }
        public DateTimeOffset? LastDeliveredAt { get; init; }
    }
}

/// <summary>An event to deliver, as the relay sends it.</summary>
public sealed record OutboundEvent
{
    public Guid DeliveryId { get; init; }
    public int Attempts { get; init; }
    public Guid EventId { get; init; }
    public string Type { get; init; } = "";
    public string Source { get; init; } = "";
    public Guid Subject { get; init; }
    public string SubjectType { get; init; } = "";
    public long Sequence { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
    public string ActorId { get; init; } = "";
    public Guid? CorrelationId { get; init; }
    public string? Reason { get; init; }

    /// <summary>The event's data, as recorded (JSON).</summary>
    public string Data { get; init; } = "{}";
}

/// <summary>
/// The relay's database side (D-061). <see cref="CaptureAsync"/> copies new events a sink takes from <c>evt.event</c> into
/// <c>evt.delivery</c> in commit order, reading only below the oldest transaction still running so none is skipped, and moves the
/// sink's checkpoint; <see cref="DueAsync"/> picks the next deliveries not held back by an earlier one of their subject.
/// </summary>
public sealed class DeliveryRelay(NpgsqlDataSource dataSource)
{
    /// <summary>Distinct per sink, so two API instances never relay the same sink at once.</summary>
    private const long LockBase = 0x50554C41_00000000; // "PULA"

    private const string EnsureCheckpoint = """
        INSERT INTO evt.sink_checkpoint (sink_name, last_tx_id, last_position)
        SELECT @Sink, coalesce(e.tx_id, '0'::xid8), coalesce(e.position, 0)
        FROM (SELECT 1) one
        LEFT JOIN LATERAL (SELECT tx_id, position FROM evt.event WHERE @FromNow ORDER BY tx_id DESC, position DESC LIMIT 1) e ON true
        ON CONFLICT (sink_name) DO NOTHING
        """;

    private const string Capture = """
        WITH cp AS (
            SELECT last_tx_id, last_position FROM evt.sink_checkpoint WHERE sink_name = @Sink FOR UPDATE),
        batch AS (
            SELECT e.id, e.recorded_at, e.tx_id, e.position, e.type, e.subject
            FROM evt.event e, cp
            WHERE (e.tx_id, e.position) > (cp.last_tx_id, cp.last_position)
              AND e.tx_id < pg_snapshot_xmin(pg_current_snapshot())
            ORDER BY e.tx_id, e.position
            LIMIT @Batch),
        ins AS (
            INSERT INTO evt.delivery (sink, event_id, event_recorded_at, tx_id, position, event_type, subject)
            SELECT @Sink, b.id, b.recorded_at, b.tx_id, b.position, b.type, b.subject
            FROM batch b
            WHERE b.type LIKE ANY(@Patterns)
            ON CONFLICT (sink, event_id) DO NOTHING
            RETURNING 1),
        upd AS (
            UPDATE evt.sink_checkpoint c SET last_tx_id = l.tx_id, last_position = l.position, updated_at = now()
            FROM (SELECT tx_id, position FROM batch ORDER BY tx_id DESC, position DESC LIMIT 1) l
            WHERE c.sink_name = @Sink
            RETURNING 1)
        SELECT (SELECT count(*) FROM batch)::int AS scanned, (SELECT count(*) FROM ins)::int AS captured, (SELECT count(*) FROM upd)::int AS moved
        """;

    private const string Due = $"""
        SELECT d.id AS delivery_id, d.attempts, e.id AS event_id, e.type, e.source, e.subject, e.subject_type, e.sequence, e.occurred_at,
               e.actor_id, e.correlation_id, e.reason, e.data::text AS data
        FROM evt.delivery d
        JOIN evt.event e ON e.id = d.event_id AND e.recorded_at = d.event_recorded_at
        WHERE d.sink = @Sink AND d.status = 'pending' AND d.next_attempt_at <= @Now AND NOT {DeliverySql.HeldBy}
        ORDER BY d.tx_id, d.position
        LIMIT @Batch
        """;

    private const string Record = """
        UPDATE evt.delivery
        SET status = @Status, attempts = attempts + 1, next_attempt_at = @Next, last_attempt_at = @At, last_status_code = @Code,
            last_error = @Error, delivered_at = CASE WHEN @Status = 'delivered' THEN @At END, updated_at = @At
        WHERE id = @Id AND status = 'pending'
        """;

    /// <summary>Runs <paramref name="work"/> while holding the sink's lock; false when another instance holds it.</summary>
    public async Task<bool> WithLockAsync(string sink, Func<Task> work, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var key = LockBase + StableHash(sink);
        if (!await connection.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_lock(@Key)", new { Key = key }, cancellationToken: cancellationToken)))
        {
            return false;
        }

        try
        {
            await work();
            return true;
        }
        finally
        {
            await connection.ExecuteAsync("SELECT pg_advisory_unlock(@Key)", new { Key = key });
        }
    }

    /// <summary>
    /// Copies the next batch of events the sink takes (those whose type starts with one of <paramref name="typePrefixes"/>) into its
    /// queue; returns how many were looked at and taken. A sink seen for the first time starts after the events already logged when
    /// <paramref name="fromNow"/>, else from the beginning.
    /// </summary>
    public async Task<(int Scanned, int Captured)> CaptureAsync(string sink, IReadOnlyList<string> typePrefixes, bool fromNow, int batch, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(EnsureCheckpoint, new { Sink = sink, FromNow = fromNow }, transaction, cancellationToken: cancellationToken));
        var patterns = typePrefixes.Select(p => p.Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal) + "%").ToArray();
        var row = await connection.QuerySingleAsync<CaptureRow>(new CommandDefinition(
            Capture, new { Sink = sink, Patterns = patterns, Batch = batch }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
        return (row.Scanned, row.Captured);
    }

    public async Task<IReadOnlyList<OutboundEvent>> DueAsync(string sink, DateTimeOffset now, int batch, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<OutboundEvent>(new CommandDefinition(Due, new { Sink = sink, Now = now, Batch = batch }, cancellationToken: cancellationToken))];
    }

    /// <summary>Records an attempt's outcome; a delivery an operator resolved meanwhile is left as they set it.</summary>
    public async Task RecordAsync(Guid deliveryId, DeliveryOutcome outcome, int? statusCode, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            Record,
            new { Id = deliveryId, outcome.Status, Next = outcome.NextAttemptAt, At = at, Code = statusCode, Error = outcome.Error },
            cancellationToken: cancellationToken));
    }

    // The same on every instance and run, unlike string.GetHashCode (FNV-1a, 31 bits).
    private static long StableHash(string text)
    {
        var hash = 2166136261u;
        foreach (var c in text)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return hash & 0x7FFFFFFF;
    }

    private sealed class CaptureRow
    {
        public int Scanned { get; init; }
        public int Captured { get; init; }
        public int Moved { get; init; }
    }
}
