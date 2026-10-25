using Npgsql;
using Pulaski.Infrastructure.Persistence;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Refuses to start against a database that is missing this build's newest migration (D-028), so
/// an out-of-date schema shows as one clear message instead of SQL errors on every screen. A
/// database that can't be reached is only logged: the API starts and reports it per request.
/// </summary>
internal sealed class SchemaVersionCheck(DatabaseMaintenance database, ILogger<SchemaVersionCheck> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        bool current;
        try
        {
            current = await database.HasMigrationAsync(DatabaseMaintenance.RequiredMigration, cancellationToken);
        }
        catch (NpgsqlException ex) when (ex is not PostgresException)
        {
            logger.LogWarning(ex, "Could not reach the database to check its schema version.");
            return;
        }

        if (!current)
        {
            throw new InvalidOperationException(
                $"The database is missing migration {DatabaseMaintenance.RequiredMigration}. Run db/migrate.sh <database>, then start the API again.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Keeps every partitioned history table a few months ahead (D-028): at startup, then every 12 h.
/// Without it, writes to <c>evt.event</c>, <c>ts.lot_event</c> and the rest fail once the
/// partitions the seed made run out. Each round also drops process values older than
/// <c>Retention:ProcessValueDays</c> (90 by default, #26). Failures are logged and retried on the next round.
/// </summary>
internal sealed class PartitionMaintenance(DatabaseMaintenance database, IConfiguration configuration, ILogger<PartitionMaintenance> logger)
    : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var months = configuration.GetValue("Pulaski:PartitionMonthsAhead", 3);
        var keepDays = Math.Max(7, configuration.GetValue("Retention:ProcessValueDays", 90));
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var created = await database.EnsurePartitionsAsync(months, stoppingToken);
                if (created > 0)
                {
                    logger.LogInformation("Created {Count} history partitions to keep {Months} months ready.", created, months);
                }

                var dropped = await database.DropOldProcessValuesAsync(keepDays, stoppingToken);
                if (dropped > 0)
                {
                    logger.LogInformation("Dropped {Count} months of process values older than {Days} days.", dropped, keepDays);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Could not create history partitions ahead; retrying in {Interval}.", Interval);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
