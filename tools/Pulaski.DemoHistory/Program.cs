using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pulaski.Application;
using Pulaski.Application.Abstractions;
using Pulaski.DemoHistory;
using Pulaski.Infrastructure;

// The demo's history (D-101): the previous shift and the current one so far at the demo plant, recorded through the application's own command
// handlers with the clock set to each moment (see History). Run once, after the demo setup; a plant that has the history's shift schedule
// already is left alone.
//
//   PULASKI_CONNECTION   the database, as the API's login (a member of pulaski_app)
//   PULASKI_SITE_ZONE    the demo site's time zone (default America/Chicago, as db/seed-demo.sql)
var connection = Environment.GetEnvironmentVariable("PULASKI_CONNECTION") is { Length: > 0 } c
    ? c
    : throw new InvalidOperationException("Set PULASKI_CONNECTION to the database, as the API's login.");

await using var dataSource = NpgsqlDataSource.Create(connection);
var lookup = new Lookup(dataSource);
if (await lookup.OneAsync<int>("SELECT count(*)::int FROM core.shift_schedule WHERE code = @code", new { code = History.ScheduleCode }) > 0)
{
    await Console.Out.WriteLineAsync($"The plant has shift schedule {History.ScheduleCode} already, so its history is there: nothing to do.");
    return 0;
}

if (await lookup.OneAsync<int>("SELECT count(*)::int FROM core.equipment WHERE code IN ('EXT-02', 'PACK1', 'FG-STAGE', 'RM-STORE')") < 4
    || await lookup.OneAsync<int>("SELECT count(*)::int FROM core.quality_characteristic WHERE code IN ('MELT-TEMP', 'PROFILE-WIDTH')") < 2)
{
    await Console.Error.WriteLineAsync("This isn't the demo plant after its setup (db/seed-demo.sql, then tools/Pulaski.DemoSetup): the history needs both.");
    return 1;
}

if (await lookup.OneAsync<int>("SELECT count(*)::int FROM core.job_run r JOIN core.equipment e ON e.id = r.equipment_id WHERE e.code IN ('EXT-02', 'PACK1')") > 0)
{
    await Console.Error.WriteLineAsync("Line 2 or the packer has run jobs already; the history only goes into a plant where they haven't.");
    return 1;
}

var clock = new HistoryClock();
var acting = new Acting();
var services = new ServiceCollection();
services.AddSingleton<TimeProvider>(clock);
services.AddApplication();
services.AddInfrastructure(connection);
services.AddSingleton<IRequestContext>(acting);
await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

var window = HistoryWindow.For(DateTimeOffset.UtcNow, SiteTime.Zone);
var commands = new Commands(provider, acting, clock);
foreach (var (username, id) in await lookup.ListAsync<(string, Guid)>("SELECT username, id FROM sec.app_user WHERE username IS NOT NULL"))
{
    commands.KnowUser(username, id);
}

try
{
    await new History(commands, lookup, clock, window).RunAsync();
}
catch (InvalidOperationException ex)
{
    await Console.Error.WriteLineAsync($"The demo history stopped: {ex.Message}");
    return 1;
}

await Console.Out.WriteLineAsync($"The demo plant has its history: the previous shift from {window.PreviousStart:u} and the current one to {window.Now:u}.");
return 0;

namespace Pulaski.DemoHistory
{
    /// <summary>The demo site's time zone, which its shifts follow.</summary>
    public static class SiteTime
    {
        public static TimeZoneInfo Zone { get; } =
        TimeZoneInfo.FindSystemTimeZoneById(Environment.GetEnvironmentVariable("PULASKI_SITE_ZONE") is { Length: > 0 } zone ? zone : "America/Chicago");
    }
}
