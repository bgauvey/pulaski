using Dapper;
using Npgsql;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Shifts;

namespace Pulaski.Infrastructure.Shifts;

/// <summary>Read side for shift schedules (D-038).</summary>
internal sealed class ShiftQueries(NpgsqlDataSource dataSource) : IShiftReadStore
{
    private const string List = $"""
        SELECT {ShiftSql.Columns}
        FROM core.shift_schedule s
        JOIN core.equipment site ON site.id = s.site_id
        WHERE @SiteId::uuid IS NULL OR s.site_id = @SiteId
        ORDER BY site.code, s.is_active DESC, s.code
        """;

    private const string Get = $"""
        SELECT {ShiftSql.Columns}
        FROM core.shift_schedule s
        JOIN core.equipment site ON site.id = s.site_id
        WHERE s.id = @Id
        """;

    private const string Users = """
        SELECT shift_schedule_id AS schedule_id, code FROM core.equipment WHERE shift_schedule_id = ANY(@Ids) ORDER BY code
        """;

    private const string TimeZone = "SELECT time_zone FROM core.equipment WHERE id = @Id AND level = 'site'";

    public async Task<IReadOnlyList<ShiftScheduleDto>> ListAsync(Guid? siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ShiftSql.Row>(new CommandDefinition(List, new { SiteId = siteId }, cancellationToken: cancellationToken))).AsList();
        return await WithUsersAsync(connection, rows, cancellationToken);
    }

    public async Task<ShiftScheduleDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<ShiftSql.Row>(new CommandDefinition(Get, new { Id = id }, cancellationToken: cancellationToken));
        return row is null ? null : (await WithUsersAsync(connection, [row], cancellationToken))[0];
    }

    public async Task<string?> GetSiteTimeZoneAsync(Guid siteId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<string?>(new CommandDefinition(TimeZone, new { Id = siteId }, cancellationToken: cancellationToken));
    }

    private static async Task<IReadOnlyList<ShiftScheduleDto>> WithUsersAsync(NpgsqlConnection connection, List<ShiftSql.Row> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var users = (await connection.QueryAsync<(Guid ScheduleId, string Code)>(
                new CommandDefinition(Users, new { Ids = rows.Select(r => r.Id).ToArray() }, cancellationToken: cancellationToken)))
            .ToLookup(u => u.ScheduleId, u => u.Code);
        return [.. rows.Select(r => r.ToDto([.. users[r.Id]]))];
    }
}
