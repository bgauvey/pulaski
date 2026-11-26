using Dapper;
using Npgsql;
using Pulaski.Application.Audit;
using Pulaski.Infrastructure.Instructions;

namespace Pulaski.Infrastructure.Audit;

/// <summary>The audit trail over <c>evt.event</c> (#129): by subject through its index, else within a window of the partition key.</summary>
internal sealed class AuditQueries(NpgsqlDataSource dataSource) : IAuditReadStore
{
    private static readonly string Sql = $"""
        SELECT e.id, e.recorded_at, e.occurred_at, e.type, e.source, e.subject, e.subject_type, e.actor_type, e.actor_id,
               {InstructionSql.UserName("e.actor_id")} AS actor_name, e.reason, e.data::text AS data
        FROM evt.event e
        WHERE (@SubjectId::uuid IS NULL OR e.subject = @SubjectId)
          AND (@From::timestamptz IS NULL OR e.recorded_at >= @From)
          AND e.recorded_at <= @To
          AND (@Area::text IS NULL OR e.subject_type = @Area)
          AND (@Action::text IS NULL OR e.type = @Action OR e.type LIKE @ActionPrefix)
          AND (@Actor::text IS NULL OR e.actor_id ILIKE @ActorPattern OR {InstructionSql.UserName("e.actor_id")} ILIKE @ActorPattern)
          AND (@Text::text IS NULL OR e.data::text ILIKE @TextPattern)
          AND (@BeforeAt::timestamptz IS NULL OR (e.recorded_at, e.id) < (@BeforeAt, @BeforeId))
        ORDER BY e.recorded_at DESC, e.id DESC
        LIMIT @Take
        """;

    public async Task<IReadOnlyList<AuditRow>> SearchAsync(AuditFilter filter, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return [.. await connection.QueryAsync<AuditRow>(new CommandDefinition(Sql, new
        {
            filter.SubjectId,
            filter.From,
            filter.To,
            filter.Area,
            filter.Action,
            ActionPrefix = filter.Action is { } a ? Like(a) + "%" : null,
            filter.Actor,
            ActorPattern = filter.Actor is { } who ? "%" + Like(who) + "%" : null,
            filter.Text,
            TextPattern = filter.Text is { } t ? "%" + Like(t) + "%" : null,
            BeforeAt = filter.BeforeRecordedAt,
            BeforeId = filter.BeforeId ?? Guid.Empty,
            Take = filter.Limit + 1,
        }, cancellationToken: cancellationToken))];
    }

    private static string Like(string s) => s.Replace(@"\", @"\\", StringComparison.Ordinal).Replace("%", @"\%", StringComparison.Ordinal).Replace("_", @"\_", StringComparison.Ordinal);
}
