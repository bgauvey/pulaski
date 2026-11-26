using Dapper;
using Npgsql;

namespace Pulaski.Infrastructure.Tests;

/// <summary>The reporting views (#132, D-129): they read the real schema, and the reporting role reads them and nothing else.</summary>
[Collection(DatabaseTests.Name)]
public sealed class ReportViewTests(DatabaseFixture db)
{
    public static readonly string[] Views =
    [
        "site", "equipment", "uom", "material", "reason", "production_report", "production", "run", "stop", "oee_shift", "oee_day", "work_order", "job",
    ];

    [Fact]
    public async Task Every_view_reads_and_the_reporting_role_reads_only_them()
    {
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var present = (await connection.QueryAsync<string>("SELECT table_name FROM information_schema.views WHERE table_schema = 'report'")).ToHashSet();
        Assert.Equal(Views.Order(), present.Order());
        foreach (var view in Views)
        {
            await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM report.{view}");
        }

        // As a login holding pulaski_report: every view, and not a table behind them.
        await using var tx = await connection.BeginTransactionAsync();
        await connection.ExecuteAsync("SET LOCAL ROLE pulaski_report", transaction: tx);
        foreach (var view in Views)
        {
            await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM report.{view}", transaction: tx);
        }

        await connection.ExecuteAsync("SAVEPOINT s", transaction: tx);
        var denied = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteScalarAsync<long>("SELECT count(*) FROM core.lot", transaction: tx));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
        await connection.ExecuteAsync("ROLLBACK TO SAVEPOINT s", transaction: tx);
        var noWrite = await Assert.ThrowsAsync<PostgresException>(() => connection.ExecuteAsync("CREATE VIEW report.mine AS SELECT 1", transaction: tx));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, noWrite.SqlState);
    }
}
