using Npgsql;
using Pulaski.Application.Materials;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Runs material and people connections when their schedule is due (#71), checking every <c>Connections:Schedule:PollSeconds</c> (60).
/// <c>Connections:Schedule:Enabled</c> = false stops it, for an instance that shouldn't pull. Runs act as <c>system</c>.
/// </summary>
internal sealed class ConnectionScheduleService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<ConnectionScheduleService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Connections:Schedule:Enabled", true))
        {
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(10, configuration.GetValue("Connections:Schedule:PollSeconds", 60)));
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var ran = await scope.ServiceProvider.GetRequiredService<ScheduledConnections>().RunDueAsync(stoppingToken);
                if (ran > 0)
                {
                    logger.LogInformation("Ran {Count} scheduled connections.", ran);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't check scheduled connections; trying again in {Interval}.", poll);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
