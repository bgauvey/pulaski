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
}
