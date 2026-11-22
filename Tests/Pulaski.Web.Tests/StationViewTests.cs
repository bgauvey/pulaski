using Pulaski.Web.Services.Localization;
using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>What an operator station says about who is signed on (#150).</summary>
public class StationViewTests
{
    private static StationDto Station(string? name, bool user = true, bool run = true, bool report = true, bool stops = true) => new()
    {
        TerminalName = "EXT-01 station",
        TerminalCode = "ST-1",
        PersonName = name,
        PersonIsUser = user,
        CanRunJobs = run,
        CanReport = report,
        CanCodeStops = stops,
    };

    [Fact]
    public void It_says_what_the_person_cant_do_and_nothing_when_they_can_do_it_all()
    {
        Assert.Null(StationView.Limits(Text.English, Station(null)));
        Assert.Null(StationView.Limits(Text.English, Station("Olu")));
        Assert.Contains("no Pulaski user", StationView.Limits(Text.English, Station("Noa", user: false)), StringComparison.Ordinal);
        Assert.Equal("Your role doesn't let you code stops at this site.", StationView.Limits(Text.English, Station("Ann", stops: false)));
        Assert.Equal("Your role doesn't let you start or stop jobs or report production at this site.", StationView.Limits(Text.English, Station("Ann", run: false, report: false)));
        Assert.Equal("Your role doesn't let you run jobs, report production or code stops at this site.", StationView.Limits(Text.English, Station("Pia", run: false, report: false, stops: false)));
    }

    [Fact]
    public void It_says_what_an_action_did()
    {
        var started = Station("Olu") with { RunningJob = new RunningJobDto { JobId = Guid.NewGuid(), JobNumber = "WO-1-10", JobType = "production", RunId = Guid.NewGuid() } };
        Assert.Equal("WO-1-10 is running.", StationView.Done(Text.English, "start", started));
        Assert.Equal(("Paused.", "Completed."), (StationView.Done(Text.English, "pause", started), StationView.Done(Text.English, "complete", started)));
    }

    [Fact]
    public void A_refusal_names_its_fields()
    {
        Assert.Equal("Reason Other needs a comment.",
            StationView.Problem("One or more fields are invalid.", new Dictionary<string, string[]> { ["comment"] = ["Reason Other needs a comment."] }));
        Assert.Equal("Pause or complete EXT-01's job first.", StationView.Problem("Pause or complete EXT-01's job first.", new Dictionary<string, string[]>()));
    }

    [Fact]
    public void Reporting_words_name_the_machine_figures_and_the_lot_at_the_location()
    {
        var counts = new MachineCountsDto { GoodQty = 1200m, ScrapQty = 12m, UomCode = "m" };
        Assert.Equal("1,200 m good · 12 m scrap", StationView.MachineFigures(Text.English, counts));
        Assert.Equal("1,200 m good", StationView.MachineFigures(Text.English, counts with { ScrapQty = 0 }));

        var job = new JobExecutionDto
        {
            JobId = Guid.NewGuid(),
            JobNumber = "WO-1-10",
            JobType = "production",
            Status = "running",
            SiteId = Guid.NewGuid(),
            LineId = Guid.NewGuid(),
            LineCode = "EXT-01",
            BaseUomCode = "m",
            LotSize = 100m,
            FillingLots = [new FillingLotDto { LotId = Guid.NewGuid(), LotNumber = "WO-1-10-02", LocationCode = "FG", Quantity = 30m }],
        };
        Assert.Equal("At FG it goes into lot WO-1-10-02 (30 of 100 m).", StationView.LotHint(Text.English, job, "FG"));
        Assert.Equal("At RM it starts a new lot.", StationView.LotHint(Text.English, job, "RM"));
        Assert.Null(StationView.LotHint(Text.English, job with { LotSize = null }, "FG"));
        Assert.Null(StationView.LotHint(Text.English, job, null));
    }

    [Fact]
    public void Stops_say_how_long_and_how_long_ago()
    {
        var now = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
        var open = new Pulaski.Contracts.MachineData.DowntimeDto { Id = Guid.NewGuid(), EquipmentId = Guid.NewGuid(), EquipmentCode = "EXT-01-SAW", PackMlName = "Stopped", StartedAt = now.AddMinutes(-4) };
        Assert.Equal("stopped 4 min ago, still stopped", StationView.StopWhen(Text.English, open, now));
        Assert.Equal("stopped for 12 min, 35 min ago", StationView.StopWhen(Text.English, open with { StartedAt = now.AddMinutes(-47), EndedAt = now.AddMinutes(-35) }, now));
        Assert.Equal(("A stop needs its reason", "Stops needing their reasons: 3"), (StationView.StopsTitle(Text.English, 1), StationView.StopsTitle(Text.English, 3)));
    }

    [Fact]
    public void Calls_say_how_long_they_have_waited_and_who_is_on_them()
    {
        var now = new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero);
        var call = new Pulaski.Contracts.Andon.AndonCallDto
        {
            Id = Guid.NewGuid(),
            SiteId = Guid.NewGuid(),
            LineId = Guid.NewGuid(),
            LineCode = "EXT-01",
            TypeId = Guid.NewGuid(),
            TypeCode = "MAINT",
            TypeName = "Maintenance",
            Status = "open",
            RaisedAt = now.AddMinutes(-4),
            RaisedBy = "dana",
        };
        Assert.Equal("waiting 4 min", AndonCallView.State(Text.English, call, now));
        Assert.Equal("waiting 12 min: escalated", AndonCallView.State(Text.English, call with { RaisedAt = now.AddMinutes(-12), IsEscalated = true }, now));
        Assert.Equal("lee is on it", AndonCallView.State(Text.English, call with { Status = "acknowledged", AcknowledgedAt = now, AcknowledgedBy = "lee" }, now));
        Assert.Equal("Lee Ong is on it", AndonCallView.State(Text.English, call with { Status = "acknowledged", AcknowledgedAt = now, AcknowledgedBy = "lee", AcknowledgedByName = "Lee Ong" }, now));
        Assert.Equal(("A call for help", "Calls for help: 2"), (AndonCallView.Title(Text.English, 1), AndonCallView.Title(Text.English, 2)));

        Assert.Equal("dana", AndonCallView.Caller(Text.English, call));
        Assert.Equal("Dana Ito at ST-1", AndonCallView.Caller(Text.English, call with { RaisedByName = "Dana Ito", RaisedFrom = "ST-1" }));
        Assert.Null(AndonCallView.Took(Text.English, call));
        Assert.Equal(("—", "45 s", "12 min"), (AndonCallView.Time(null), AndonCallView.Time(45), AndonCallView.Time(720)));
        Assert.Equal("answered in 3 min, resolved in 25 min",
            AndonCallView.Took(Text.English, call with { AcknowledgedAt = call.RaisedAt.AddMinutes(3), ResolvedAt = call.RaisedAt.AddMinutes(25) }));
    }
}
