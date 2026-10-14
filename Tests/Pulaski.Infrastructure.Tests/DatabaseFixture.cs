using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pulaski.Application;
using Pulaski.Application.Abstractions;
using Pulaski.Testing;

namespace Pulaski.Infrastructure.Tests;

/// <summary>
/// Creates a throwaway database from db/schema-draft.sql and db/app-role.sql, and drops it
/// afterwards. The application under test connects as a throwaway login that is a member of
/// pulaski_app, so it has exactly the production privileges (D-012): for example, no UPDATE or
/// DELETE on evt.event. Set PULASKI_TEST_ADMIN_CONNECTION to point at a server where the user can
/// create databases and roles; the default is the local server.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    private const string DefaultAdminConnection = "Host=localhost;Database=postgres";

    private readonly string _adminConnection =
        Environment.GetEnvironmentVariable("PULASKI_TEST_ADMIN_CONNECTION") ?? DefaultAdminConnection;

    private readonly string _databaseName = $"pulaski_test_{Guid.NewGuid():N}"[..21];
    private readonly string _appLogin = $"pulaski_it_{Guid.NewGuid():N}"[..19];
    private readonly string _appPassword = Guid.NewGuid().ToString("N");

    /// <summary>Owner connection, for setup and for asserting on tables.</summary>
    public NpgsqlDataSource AdminDataSource { get; private set; } = null!;

    /// <summary>Connection string for the application login (member of pulaski_app).</summary>
    public string AppConnectionString { get; private set; } = "";

    /// <summary>The real Application + Infrastructure wiring, connected as the application login.</summary>
    public ServiceProvider Services { get; private set; } = null!;

    public Guid UomId { get; private set; }

    public Guid MaterialId { get; private set; }

    public async Task InitializeAsync()
    {
        await using (var admin = new NpgsqlConnection(_adminConnection))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand(
                $"CREATE DATABASE {_databaseName}; CREATE ROLE {_appLogin} LOGIN PASSWORD '{_appPassword}'", admin);
            await create.ExecuteNonQueryAsync();
        }

        var builder = new NpgsqlConnectionStringBuilder(_adminConnection) { Database = _databaseName };
        AdminDataSource = NpgsqlDataSource.Create(builder.ConnectionString);

        var dbDir = FindDbDirectory();
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(dbDir, "schema-draft.sql")));
        await ExecuteAsync(await File.ReadAllTextAsync(Path.Combine(dbDir, "app-role.sql")));
        await ExecuteAsync($"GRANT pulaski_app TO {_appLogin}");
        await ExecuteAsync("SELECT ts.create_month_partitions('evt.event', current_date - 31, 3)");
        await ExecuteAsync("SELECT ts.create_month_partitions('ts.job_status_change', current_date - 31, 3)");

        UomId = Guid.CreateVersion7();
        MaterialId = Guid.CreateVersion7();
        await ExecuteAsync(
            $"""
            INSERT INTO core.uom (id, code, name, dimension) VALUES ('{UomId}', 'm', 'Metre', 'length');
            INSERT INTO core.material (id, code, name, material_type, base_uom_id)
            VALUES ('{MaterialId}', 'PRF-1', 'Profile 1', 'finished', '{UomId}');
            """);

        AppConnectionString = new NpgsqlConnectionStringBuilder(builder.ConnectionString)
        {
            Username = _appLogin,
            Password = _appPassword,
        }.ConnectionString;

        var services = new ServiceCollection();
        services.AddApplication();
        services.AddInfrastructure(AppConnectionString);
        services.AddSingleton<IRequestContext, FixedRequestContext>();
        Services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    public async Task DisposeAsync()
    {
        await Services.DisposeAsync();
        await AdminDataSource.DisposeAsync();
        NpgsqlConnection.ClearAllPools();

        await using var admin = new NpgsqlConnection(_adminConnection);
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand(
            $"DROP DATABASE IF EXISTS {_databaseName} WITH (FORCE); DROP ROLE IF EXISTS {_appLogin}", admin);
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Runs one command through its real handler, in its own DI scope (one unit of work).</summary>
    public async Task<TResult> SendAsync<TResult>(ICommand<TResult> command)
    {
        await using var scope = Services.CreateAsyncScope();
        var handlerType = typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(TResult));
        var handler = scope.ServiceProvider.GetRequiredService(handlerType);
        var handle = handlerType.GetMethod(nameof(ICommandHandler<ICommand<TResult>, TResult>.HandleAsync))!;
        try
        {
            return await (Task<TResult>)handle.Invoke(handler, [command, CancellationToken.None])!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(ex.InnerException);
            throw;
        }
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var command = AdminDataSource.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    private static string FindDbDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "db", "app-role.sql");
            if (File.Exists(candidate))
            {
                return Path.GetDirectoryName(candidate)!;
            }
        }

        throw new DirectoryNotFoundException("Could not find db/app-role.sql above the test output directory.");
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseTests : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "Database";
}
