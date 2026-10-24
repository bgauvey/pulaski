using Dapper;
using Npgsql;

namespace Pulaski.Infrastructure.Persistence;

/// <summary>
/// Housekeeping the API runs against its own database (D-028): checking the schema is new enough,
/// and keeping the history tables partitioned ahead.
/// </summary>
public sealed class DatabaseMaintenance(NpgsqlDataSource dataSource)
{
    /// <summary>The newest file in <c>db/migrations</c>, without <c>.sql</c>. This build needs it applied.</summary>
    public const string RequiredMigration = "0030_reason_trees";

    private const string HasMigrationSql = "SELECT EXISTS (SELECT 1 FROM public.schema_migration WHERE version = @Version)";

    /// <summary>True when the database records <paramref name="version"/> as applied.</summary>
    public async Task<bool> HasMigrationAsync(string version, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // to_regclass first, so a database that predates migrations answers false rather than failing.
        var exists = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT to_regclass('public.schema_migration') IS NOT NULL", cancellationToken: cancellationToken));
        return exists && await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(HasMigrationSql, new { Version = version }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// Creates the current month and the next <paramref name="months"/> − 1 for every partitioned
    /// history table, through <c>ts.ensure_partitions</c>.
    /// </summary>
    /// <returns>How many partitions were created; 0 when all were there.</returns>
    public async Task<int> EnsurePartitionsAsync(int months, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition("SELECT ts.ensure_partitions(@Months)", new { Months = months }, cancellationToken: cancellationToken));
    }
}
