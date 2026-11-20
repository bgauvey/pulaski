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
}
