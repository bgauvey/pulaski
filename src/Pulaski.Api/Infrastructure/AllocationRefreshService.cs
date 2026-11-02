using Npgsql;
using Pulaski.Application.Inventory;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Keeps allocations current for sites that switch it on (#82): each round refreshes the sites whose stock, jobs or orders changed since
/// their last refresh, once the changes have been quiet for <c>Allocations:Refresh:QuietSeconds</c> (60) or the last refresh is older than
/// <c>Allocations:Refresh:AtMostSeconds</c> (900). <c>Allocations:Refresh:Enabled</c> (true) switches the service off for every site;
/// <c>Allocations:Refresh:PollSeconds</c> (30) paces it. Failures are logged and retried on the next round.
/// </summary>
internal sealed class AllocationRefreshService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AllocationRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Allocations:Refresh:Enabled", true))
        {
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("Allocations:Refresh:PollSeconds", 30)));
        var quiet = TimeSpan.FromSeconds(Math.Max(0, configuration.GetValue("Allocations:Refresh:QuietSeconds", 60)));
        var atMost = TimeSpan.FromSeconds(Math.Max(60, configuration.GetValue("Allocations:Refresh:AtMostSeconds", 900)));
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var refreshed = await scope.ServiceProvider.GetRequiredService<AllocationAutoRefresh>().RefreshDueAsync(quiet, atMost, stoppingToken);
                if (refreshed > 0)
                {
                    logger.LogInformation("Refreshed allocations at {Count} sites.", refreshed);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't refresh allocations; trying again in {Interval}.", poll);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
