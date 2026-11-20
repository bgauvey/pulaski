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
        Assert.Null(StationView.Limits(Station(null)));
        Assert.Null(StationView.Limits(Station("Olu")));
        Assert.Contains("no Pulaski user", StationView.Limits(Station("Noa", user: false)), StringComparison.Ordinal);
        Assert.Equal("Your role doesn't let you code stops at this site.", StationView.Limits(Station("Ann", stops: false)));
        Assert.Equal("Your role doesn't let you start or stop jobs or report production at this site.", StationView.Limits(Station("Ann", run: false, report: false)));
        Assert.Equal("Your role doesn't let you run jobs, report production or code stops at this site.", StationView.Limits(Station("Pia", run: false, report: false, stops: false)));
    }

    [Fact]
    public void It_says_what_an_action_did()
    {
        var started = Station("Olu") with { RunningJob = new RunningJobDto { JobId = Guid.NewGuid(), JobNumber = "WO-1-10", JobType = "production", RunId = Guid.NewGuid() } };
        Assert.Equal("WO-1-10 is running.", StationView.Done("start", started));
        Assert.Equal(("Paused.", "Completed."), (StationView.Done("pause", started), StationView.Done("complete", started)));
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
        Assert.Equal("1,200 m good · 12 m scrap", StationView.MachineFigures(counts));
        Assert.Equal("1,200 m good", StationView.MachineFigures(counts with { ScrapQty = 0 }));

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
        Assert.Equal("At FG it goes into lot WO-1-10-02 (30 of 100 m).", StationView.LotHint(job, "FG"));
        Assert.Equal("At RM it starts a new lot.", StationView.LotHint(job, "RM"));
        Assert.Null(StationView.LotHint(job with { LotSize = null }, "FG"));
        Assert.Null(StationView.LotHint(job, null));
    }
}
