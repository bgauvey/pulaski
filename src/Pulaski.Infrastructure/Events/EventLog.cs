using System.Text.Json;
using Pulaski.Application.Abstractions;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Events;

/// <summary>Appends CloudEvents rows to <c>evt.event</c> in the command's transaction (D-007, D-008).</summary>
internal sealed class EventLog(DbSession session) : IEventLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // The command holds a lock on the aggregate row, so max(sequence) + 1 is safe per subject.
    // position and tx_id come from column defaults; the outbox relay orders by them.
    // Open screens hear of it once the transaction commits (#143): pg_notify is delivered on commit, never on rollback.
    private const string Append = """
        WITH appended AS (
            INSERT INTO evt.event (id, occurred_at, type, source, subject, subject_type, sequence,
                                   actor_type, actor_id, correlation_id, causation_id, reason, data)
            SELECT @Id, @OccurredAt, @Type, @Source, @Subject, @SubjectType,
                   coalesce(max(e.sequence), 0) + 1,
                   @ActorType, @ActorId, @CorrelationId, @CausationId, @Reason, CAST(@Data AS jsonb)
            FROM evt.event e
            WHERE e.subject = @Subject
            RETURNING 1)
        SELECT pg_notify('pulaski_live', @Live) FROM appended
        """;

    public Task AppendAsync(EventEnvelope envelope, CancellationToken cancellationToken) =>
        session.ExecuteAsync(
            Append,
            new
            {
                envelope.Id,
                envelope.OccurredAt,
                envelope.Type,
                envelope.Source,
                envelope.Subject,
                envelope.SubjectType,
                envelope.ActorType,
                envelope.ActorId,
                envelope.CorrelationId,
                envelope.CausationId,
                envelope.Reason,
                Data = JsonSerializer.Serialize(envelope.Data, Json),
                Live = JsonSerializer.Serialize(new LiveNotice(envelope.Type, envelope.SubjectType, envelope.Subject), Json),
            },
            cancellationToken);
}

/// <summary>What the database channel carries (#143): the event's type, its subject's type and id. No data.</summary>
internal sealed record LiveNotice(string Type, string Kind, Guid Id);
