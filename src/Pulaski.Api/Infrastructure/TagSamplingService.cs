using Npgsql;
using Pulaski.Application.Quality;

namespace Pulaski.Api.Infrastructure;

/// <summary>
/// Samples characteristics from their process-value tags (D-099): each round, every active binding makes its results due since its last.
/// <c>Quality:AutoSampling:Enabled</c> (true) switches it off; <c>Quality:AutoSampling:PollSeconds</c> (60) paces it. Failures are logged and
/// retried on the next round.
/// </summary>
internal sealed class TagSamplingService(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<TagSamplingService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Quality:AutoSampling:Enabled", true))
        {
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(10, configuration.GetValue("Quality:AutoSampling:PollSeconds", 60)));
        using var timer = new PeriodicTimer(poll);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var made = await scope.ServiceProvider.GetRequiredService<TagSampler>().RunDueAsync(stoppingToken);
                if (made > 0)
                {
                    logger.LogInformation("Made {Count} results from process values.", made);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
            {
                logger.LogError(ex, "Couldn't sample process values; trying again in {Interval}.", poll);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
