using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Infrastructure.Tests;

/// <summary>Schema migrations and partition maintenance (D-028), as the application login.</summary>
[Collection(DatabaseTests.Name)]
public sealed class MigrationTests(DatabaseFixture db)
{
    private static readonly string[] Migrations = [.. Directory
        .GetFiles(Path.Combine(DatabaseFixture.FindDbDirectory(), "migrations"), "*.sql")
        .Select(Path.GetFileNameWithoutExtension)
        .Order(StringComparer.Ordinal)
        .Cast<string>()];

    private DatabaseMaintenance Maintenance => db.Services.GetRequiredService<DatabaseMaintenance>();

    [Fact]
    public async Task A_fresh_schema_records_every_migration_and_the_build_needs_the_newest()
    {
        Assert.NotEmpty(Migrations);
        Assert.Equal(DatabaseMaintenance.RequiredMigration, Migrations[^1]);

        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var recorded = await connection.QueryAsync<string>("SELECT version FROM public.schema_migration ORDER BY version");
        Assert.Equal(Migrations, recorded);

        Assert.True(await Maintenance.HasMigrationAsync(DatabaseMaintenance.RequiredMigration, CancellationToken.None));
        Assert.False(await Maintenance.HasMigrationAsync("9999_not_yet", CancellationToken.None));
    }

    [Fact]
    public async Task Every_migration_reruns_cleanly_on_the_current_schema()
    {
        // A database upgraded by hand, or migrated twice, must not break: migrations are guarded.
        foreach (var version in Migrations)
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(DatabaseFixture.FindDbDirectory(), "migrations", version + ".sql"));
            await db.ExecuteAsync(sql);
        }
    }

    // Columns (by name, not position), indexes, constraints, function bodies and pulaski_app's grants;
    // partitions are left out, since they depend on the day the database was made.
    private const string Catalog = """
        SELECT 'col ' || table_schema || '.' || table_name || '.' || column_name || ' ' || data_type || ' ' || is_nullable || ' ' || coalesce(column_default, '')
        FROM information_schema.columns
        WHERE table_schema IN ('core', 'ts', 'evt', 'sec', 'public') AND table_name !~ '_p[0-9]{6}$'
        UNION ALL
        SELECT 'idx ' || schemaname || '.' || indexname || ' ' || regexp_replace(indexdef, 'INDEX \S+ ON', 'INDEX ON')
        FROM pg_indexes
        WHERE schemaname IN ('core', 'ts', 'evt', 'sec', 'public') AND tablename !~ '_p[0-9]{6}$'
        UNION ALL
        SELECT 'con ' || conrelid::regclass || ' ' || conname || ' ' || pg_get_constraintdef(oid)
        FROM pg_constraint
        WHERE connamespace::regnamespace::text IN ('core', 'ts', 'evt', 'sec', 'public') AND conrelid::regclass::text !~ '_p[0-9]{6}$'
        UNION ALL
        SELECT 'fn ' || p.oid::regprocedure || ' ' || md5(pg_get_functiondef(p.oid)) || ' ' || p.prosecdef
        FROM pg_proc p
        WHERE pronamespace::regnamespace::text IN ('core', 'ts', 'evt', 'sec')
        UNION ALL
        SELECT 'grant ' || table_schema || '.' || table_name || ' ' || privilege_type
        FROM information_schema.role_table_grants
        WHERE grantee = 'pulaski_app' AND table_name !~ '_p[0-9]{6}$'
        UNION ALL
        SELECT 'exec ' || p.oid::regprocedure
        FROM pg_proc p
        WHERE pronamespace::regnamespace::text IN ('core', 'ts', 'evt', 'sec') AND has_function_privilege('pulaski_app', p.oid, 'EXECUTE')
        ORDER BY 1
        """;

    [Fact]
    public async Task Migrating_the_schema_from_before_migrations_gives_the_schema_of_a_fresh_load()
    {
        var baseline = Path.Combine(AppContext.BaseDirectory, "Baseline");
        var dbDir = DatabaseFixture.FindDbDirectory();
        await using var old = await db.CreateScratchDatabaseAsync();
        await using (var connection = await old.OpenConnectionAsync())
        {
            await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(baseline, "schema-before-migrations.sql")));
            await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(baseline, "app-role-before-migrations.sql")));

            // As db/migrate.sh does: each file with its record in one transaction, then the grants.
            await connection.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS public.schema_migration (version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
            foreach (var version in Migrations)
            {
                await using var transaction = await connection.BeginTransactionAsync();
                await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(dbDir, "migrations", version + ".sql")), transaction: transaction);
                await connection.ExecuteAsync("INSERT INTO public.schema_migration (version) VALUES (@version)", new { version }, transaction);
                await transaction.CommitAsync();
            }

            await connection.ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(dbDir, "app-role.sql")));
        }

        await using var migrated = await old.OpenConnectionAsync();
        await using var fresh = await db.AdminDataSource.OpenConnectionAsync();
        var expected = (await fresh.QueryAsync<string>(Catalog)).ToList();
        var actual = (await migrated.QueryAsync<string>(Catalog)).ToList();
        Assert.Equal(expected.Except(actual), actual.Except(expected));
        Assert.Equal(expected.Count, actual.Count);
    }

    [Fact]
    public async Task The_application_keeps_every_history_table_partitioned_ahead()
    {
        var created = await Maintenance.EnsurePartitionsAsync(6, CancellationToken.None);

        Assert.True(created > 0);
        Assert.Equal(0, await Maintenance.EnsurePartitionsAsync(6, CancellationToken.None));

        var lastMonth = DateTime.UtcNow.AddMonths(5).ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        await using var connection = await db.AdminDataSource.OpenConnectionAsync();
        var parentsWithoutIt = await connection.QueryAsync<string>(
            """
            SELECT pt.partrelid::regclass::text
            FROM pg_partitioned_table pt
            JOIN pg_class c ON c.oid = pt.partrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname IN ('ts', 'evt')
              AND to_regclass(n.nspname || '.' || c.relname || '_p' || @lastMonth) IS NULL
            """,
            new { lastMonth });
        Assert.Empty(parentsWithoutIt);
    }
}
