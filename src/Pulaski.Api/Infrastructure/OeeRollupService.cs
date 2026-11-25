using Npgsql;
using Pulaski.Application.MachineData;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Keeps the daily OEE rollups current (#66, D-072): every minute it rolls the days that need it, newest first, a batch at a time, until none
/// are left. <c>Rollups:Oee:Enabled</c> (true) switches it off, <c>Rollups:Oee:PollSeconds</c> (60) and <c>Rollups:Oee:DaysPerRound</c> (31)
/// pace it. Failures are logged and retried on the next round; until then long reports work the days out from the history.
/// </summary>
internal sealed partial class OeeRollupService(IServiceScopeFactory scopes, IConfiguration configuration, Application.Health.ServiceHealth health, ILogger<OeeRollupService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Rollups:Oee:Enabled", true))
        {
            health.Disabled(Application.Health.BackgroundServices.OeeRollups);
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(5, configuration.GetValue("Rollups:Oee:PollSeconds", 60)));
        var batch = Math.Clamp(configuration.GetValue("Rollups:Oee:DaysPerRound", 31), 1, 500);
        using var timer = new PeriodicTimer(poll);
        health.Started(Application.Health.BackgroundServices.OeeRollups, poll);
        do
        {
            try
            {
                int rolled;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    rolled = await scope.ServiceProvider.GetRequiredService<OeeRollups>().RollAsync(batch, stoppingToken);
                    if (rolled > 0)
                    {
                        LogRolled(logger, rolled);
                    }
                }
                while (rolled == batch && !stoppingToken.IsCancellationRequested);

                health.Succeeded(Application.Health.BackgroundServices.OeeRollups);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't roll up OEE; trying again in {Interval}.", poll);
                health.Failed(Application.Health.BackgroundServices.OeeRollups, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rolled up OEE for {Count} production days.")]
    private static partial void LogRolled(ILogger logger, int count);
}
