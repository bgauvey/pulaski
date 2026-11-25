using Npgsql;
using Pulaski.Application.Labor;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Sends qualification expiry notices (#84) every <c>Labor:ExpiryNotices:PollMinutes</c> (60); <c>Labor:ExpiryNotices:Enabled</c> = false stops it.
/// Without a mail server nothing is sent or marked.
/// </summary>
internal sealed partial class QualificationNoticeService(IServiceScopeFactory scopes, IConfiguration configuration, Application.Health.ServiceHealth health, ILogger<QualificationNoticeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Labor:ExpiryNotices:Enabled", true))
        {
            health.Disabled(Application.Health.BackgroundServices.QualificationNotices);
            return;
        }

        var poll = TimeSpan.FromMinutes(Math.Max(5, configuration.GetValue("Labor:ExpiryNotices:PollMinutes", 60)));
        using var timer = new PeriodicTimer(poll);
        health.Started(Application.Health.BackgroundServices.QualificationNotices, poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<QualificationExpiryNotices>().SendDueAsync(stoppingToken);
                if (sent > 0)
                {
                    LogSent(logger, sent);
                }

                health.Succeeded(Application.Health.BackgroundServices.QualificationNotices);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't send qualification expiry notices; trying again in {Interval}.", poll);
                health.Failed(Application.Health.BackgroundServices.QualificationNotices, ex.Message);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent expiry notices for {Count} qualification grants.")]
    private static partial void LogSent(ILogger logger, int count);
}
