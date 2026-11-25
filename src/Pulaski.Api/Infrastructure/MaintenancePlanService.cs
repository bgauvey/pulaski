using Npgsql;
using Pulaski.Application.Maintenance;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Preventive maintenance (#127d, D-115): each round raises the requests plans are due. <c>Maintenance:Plans:Enabled</c> (true) switches it off;
/// <c>Maintenance:Plans:PollMinutes</c> (10) paces it. Failures are logged and retried on the next round.
/// </summary>
internal sealed partial class MaintenancePlanService(IServiceScopeFactory scopes, IConfiguration configuration, Application.Health.ServiceHealth health, ILogger<MaintenancePlanService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Maintenance:Plans:Enabled", true))
        {
            health.Disabled(Application.Health.BackgroundServices.MaintenancePlans);
            return;
        }

        var poll = TimeSpan.FromMinutes(Math.Max(1, configuration.GetValue("Maintenance:Plans:PollMinutes", 10)));
        using var timer = new PeriodicTimer(poll);
        health.Started(Application.Health.BackgroundServices.MaintenancePlans, poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var raised = await scope.ServiceProvider.GetRequiredService<MaintenancePlanRunner>().RaiseDueAsync(stoppingToken);
                if (raised > 0)
                {
                    LogRaised(logger, raised);
                }

                health.Succeeded(Application.Health.BackgroundServices.MaintenancePlans);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't check the maintenance plans; trying again in {Interval}.", poll);
                health.Failed(Application.Health.BackgroundServices.MaintenancePlans, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Raised {Count} preventive maintenance requests.")]
    private static partial void LogRaised(ILogger logger, int count);
}
