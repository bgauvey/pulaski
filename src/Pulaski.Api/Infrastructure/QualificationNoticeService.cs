using Npgsql;
using Pulaski.Application.Labor;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Sends qualification expiry notices (#84) every <c>Labor:ExpiryNotices:PollMinutes</c> (60); <c>Labor:ExpiryNotices:Enabled</c> = false stops it.
/// Without a mail server nothing is sent or marked.
/// </summary>
internal sealed class QualificationNoticeService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<QualificationNoticeService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Labor:ExpiryNotices:Enabled", true))
        {
            return;
        }

        var poll = TimeSpan.FromMinutes(Math.Max(5, configuration.GetValue("Labor:ExpiryNotices:PollMinutes", 60)));
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<QualificationExpiryNotices>().SendDueAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Sent expiry notices for {Count} qualification grants.", sent);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't send qualification expiry notices; trying again in {Interval}.", poll);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
