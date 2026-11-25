using Npgsql;
using Pulaski.Application.Scheduling;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// The scheduled recalculation of planning rates from run history (#70, D-098): each round recalculates the sites that switched it on and are
/// due (every <c>every_days</c> of their settings). <c>RunStandards:Refresh:Enabled</c> (true) switches it off for every site;
/// <c>RunStandards:Refresh:PollMinutes</c> (15) paces it. Failures are logged and retried on the next round.
/// </summary>
internal sealed partial class RateRefreshService(IServiceScopeFactory scopes, IConfiguration configuration, Application.Health.ServiceHealth health, ILogger<RateRefreshService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("RunStandards:Refresh:Enabled", true))
        {
            health.Disabled(Application.Health.BackgroundServices.RateRefresh);
            return;
        }

        var poll = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("RunStandards:Refresh:PollMinutes", 15)));
        using var timer = new PeriodicTimer(poll);
        health.Started(Application.Health.BackgroundServices.RateRefresh, poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sites = await scope.ServiceProvider.GetRequiredService<RateRefreshRunner>().RefreshDueAsync(stoppingToken);
                if (sites > 0)
                {
                    LogRecalculated(logger, sites);
                }

                health.Succeeded(Application.Health.BackgroundServices.RateRefresh);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't recalculate planning rates; trying again in {Interval}.", poll);
                health.Failed(Application.Health.BackgroundServices.RateRefresh, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recalculated planning rates at {Count} sites.")]
    private static partial void LogRecalculated(ILogger logger, int count);
}
