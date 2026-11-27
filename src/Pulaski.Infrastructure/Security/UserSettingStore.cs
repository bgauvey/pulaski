using Dapper;
using Npgsql;
using Pulaski.Application.Security;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Security;

/// <summary>A user's own view settings (#131): read with their profile.</summary>
internal sealed class UserSettingQueries(NpgsqlDataSource dataSource) : IUserSettingStore
{
    public async Task<IReadOnlyDictionary<string, string>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<(string Key, string Value)>(new CommandDefinition(
            "SELECT key, value::text FROM sec.user_setting WHERE app_user_id = @UserId AND value <> 'null'::jsonb ORDER BY key",
            new { UserId = userId }, cancellationToken: cancellationToken));
        return rows.ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
    }
}

internal sealed class UserSettingRepository(DbSession session) : IUserSettingRepository
{
    public async Task<bool> SetAsync(Guid userId, string key, string json, DateTimeOffset at, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(
            """
            INSERT INTO sec.user_setting (app_user_id, key, value, updated_at)
            SELECT u.id, @Key, @Json::jsonb, @At FROM sec.app_user u WHERE u.id = @UserId
            ON CONFLICT (app_user_id, key) DO UPDATE SET value = EXCLUDED.value, updated_at = EXCLUDED.updated_at
            """, new { UserId = userId, Key = key, Json = json, At = at }, cancellationToken) > 0;
}
