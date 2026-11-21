using Microsoft.Extensions.Options;
using Npgsql;
using Pulaski.Api.Security;
using Pulaski.Application.Alerts;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Sends e-mail alerts (#121, D-109) every <c>Alerts:PollSeconds</c> (60, at least 15), shortages every 10 minutes (#165);
/// <c>Alerts:Enabled</c> = false stops it. Without a mail server nothing is sent or recorded.
/// </summary>
internal sealed partial class AlertService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<AlertService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Alerts:Enabled", true))
        {
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(15, configuration.GetValue("Alerts:PollSeconds", 60)));
        using var timer = new PeriodicTimer(poll);
        var shortagesAt = DateTimeOffset.MinValue;
        do
        {
            try
            {
                // Shortages project the schedule, so they are worked out every AlertSender.ShortageEvery, not every round.
                var now = TimeProvider.System.GetUtcNow();
                var withShortages = now - shortagesAt >= AlertSender.ShortageEvery;
                await using var scope = scopes.CreateAsyncScope();
                var sent = await scope.ServiceProvider.GetRequiredService<AlertSender>().SendDueAsync(stoppingToken, withShortages);
                if (withShortages)
                {
                    shortagesAt = now;
                }

                if (sent > 0)
                {
                    LogSent(logger, sent);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't send alerts; trying again in {Interval}.", poll);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sent {Count} alerts.")]
    private static partial void LogSent(ILogger logger, int count);
}

/// <summary>
/// Where alert e-mails link to (#121): <c>Alerts:UiBaseUrl</c>, else the password reset's <c>Security:PasswordReset:UiBaseUrl</c>; an http(s)
/// address or none.
/// </summary>
internal sealed class AlertSettings(IConfiguration configuration, IOptions<SecurityOptions> security) : IAlertSettings
{
    public string? UiBaseUrl
    {
        get
        {
            var configured = configuration["Alerts:UiBaseUrl"];
            var url = string.IsNullOrWhiteSpace(configured) ? security.Value.PasswordReset.UiBaseUrl : configured;
            return Uri.TryCreate(url, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
                ? uri.ToString().TrimEnd('/')
                : null;
        }
    }
}
