using Pulaski.Application.Execution;
using Pulaski.Contracts.Execution;

namespace Pulaski.Application.Tests.Execution;

/// <summary>Which line an operator station serves (#146).</summary>
public class StationLineTests
{
    private static LineDisplayLineDto Line(string code) => new() { LineId = Guid.NewGuid(), Code = code, Status = "idle" };

    [Fact]
    public void The_line_asked_for_then_the_first_offered_then_the_only_one()
    {
        LineDisplayLineDto a = Line("EXT-01"), b = Line("EXT-02");

        Assert.Same(b, StationLine.Choose([a, b], "ext-02", a.LineId));
        Assert.Null(StationLine.Choose([a, b], "PACK1", a.LineId));
        Assert.Same(a, StationLine.Choose([a, b], null, a.LineId));
        Assert.Null(StationLine.Choose([a, b], null, null));
        Assert.Same(a, StationLine.Choose([a], " ", null));
    }

    [Fact]
    public void A_station_acts_only_on_its_lines_running_or_next_jobs()
    {
        Guid running = Guid.NewGuid(), next = Guid.NewGuid();
        var station = new StationDto
        {
            TerminalName = "S",
            TerminalCode = "S",
            RunningJob = new RunningJobDto { JobId = running, JobNumber = "J1", JobType = "production", RunId = Guid.NewGuid() },
            Next = [new BoardJobDto { JobId = next, JobNumber = "J2", JobType = "production", Status = "dispatched" }],
        };

        Assert.True(StationJobs.IsOnLine(station, running));
        Assert.True(StationJobs.IsOnLine(station, next));
        Assert.False(StationJobs.IsOnLine(station, Guid.NewGuid()));
    }

    [Fact]
    public void A_station_report_says_what_it_made_and_where_it_went()
    {
        ReportProductionResponse Result(params string?[] lots) => new()
        {
            RunId = Guid.NewGuid(),
            Lots = [.. lots.Select(l => new Pulaski.Contracts.Inventory.LotTransactionResponse { LotId = Guid.NewGuid(), LotNumber = l, Status = "available" })],
        };

        Assert.Equal("Reported 1,200 m good and 12 m scrap; good output in lot J-03.", StationReporting.Done(1200m, 12m, "m", Result("J-03")).Text);
        Assert.Equal("Reported 250 m good; good output in lots J-03, J-04.", StationReporting.Done(250m, 0m, "m", Result("J-03", "J-04")).Text);
        Assert.Equal("Reported 5 m scrap.", StationReporting.Done(0m, 5m, "m", Result()).Text);
    }

    [Fact]
    public void A_call_at_the_station_says_whether_it_was_already_waiting()
    {
        var id = Guid.NewGuid();
        Assert.Equal("Maintenance is called.", StationAndon.Called("Maintenance", new Pulaski.Contracts.Andon.AndonCallResponse(id, "open")).Text);
        Assert.Equal("Maintenance was called already; that call is still waiting.",
            StationAndon.Called("Maintenance", new Pulaski.Contracts.Andon.AndonCallResponse(id, "open", AlreadyOpen: true)).Text);
        Assert.Equal("Maintenance was called already; that call is still being answered.",
            StationAndon.Called("Maintenance", new Pulaski.Contracts.Andon.AndonCallResponse(id, "acknowledged", AlreadyOpen: true)).Text);
    }
}
