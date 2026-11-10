using Npgsql;
using Pulaski.Application.Scheduling;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// The scheduled recalculation of planning rates from run history (#70, D-098): each round recalculates the sites that switched it on and are
/// due (every <c>every_days</c> of their settings). <c>RunStandards:Refresh:Enabled</c> (true) switches it off for every site;
/// <c>RunStandards:Refresh:PollMinutes</c> (15) paces it. Failures are logged and retried on the next round.
/// </summary>
internal sealed class RateRefreshService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<RateRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("RunStandards:Refresh:Enabled", true))
        {
            return;
        }

        var poll = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("RunStandards:Refresh:PollMinutes", 15)));
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sites = await scope.ServiceProvider.GetRequiredService<RateRefreshRunner>().RefreshDueAsync(stoppingToken);
                if (sites > 0)
                {
                    logger.LogInformation("Recalculated planning rates at {Count} sites.", sites);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't recalculate planning rates; trying again in {Interval}.", poll);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
