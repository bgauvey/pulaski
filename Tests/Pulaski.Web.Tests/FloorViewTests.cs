using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>What the floor and job screens show and offer (D-035).</summary>
public class FloorViewTests
{
    private static readonly DateTimeOffset Eight = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    private static JobExecutionDto Job(string status, string type = "production", bool ready = true, int runs = 1) => new()
    {
        JobId = Guid.CreateVersion7(),
        JobNumber = "J-1",
        JobType = type,
        Status = status,
        SiteId = Guid.CreateVersion7(),
        LineId = Guid.CreateVersion7(),
        LineCode = "EXT-01",
        WorkOrderStatus = "in_progress",
        IsReady = ready,
        Runs = [.. Enumerable.Range(1, runs).Select(n => new JobRunDto { Id = Guid.CreateVersion7(), RunNo = n, LineCode = "EXT-01", StartedAt = Eight })],
    };

    [Fact]
    public void Times_and_room_are_shown_plainly()
    {
        Assert.Equal(("45 min", "2 h 05 min", "0 min"), (
            FloorView.Elapsed(Eight, Eight.AddMinutes(45)),
            FloorView.Elapsed(Eight, Eight.AddMinutes(125)),
            FloorView.Elapsed(Eight, Eight.AddMinutes(-5))));
        Assert.Equal((10m, (decimal?)null), (FloorView.RoomLeft(110m, 95m, 5m), FloorView.RoomLeft(null, 1m, 0m)));
        Assert.Equal(0m, FloorView.RoomLeft(110m, 200m, 0m));
        Assert.Equal((91, 100, 40), (FloorView.CapPercent(110m, 95m, 5m), FloorView.CapPercent(110m, 500m, 0m), FloorView.Progress(1000m, 400m)));
    }

    [Fact]
    public void A_report_goes_to_the_open_run_or_else_the_latest()
    {
        var ended = new JobRunDto { Id = Guid.CreateVersion7(), RunNo = 1, LineCode = "EXT-01", StartedAt = Eight, EndedAt = Eight.AddHours(1) };
        var later = ended with { Id = Guid.CreateVersion7(), RunNo = 2 };
        var open = later with { Id = Guid.CreateVersion7(), RunNo = 3, EndedAt = null };

        Assert.Equal(open.Id, FloorView.DefaultRun([ended, open, later]));
        Assert.Equal(later.Id, FloorView.DefaultRun([ended, later]));
        Assert.Null(FloorView.DefaultRun([]));
    }

    [Theory]
    [InlineData("firmed", "production", true, true, false, false, false)]
    [InlineData("firmed", "production", false, false, false, false, false)]
    [InlineData("planned", "production", true, false, false, false, false)]
    [InlineData("planned", "cleaning", true, true, false, false, false)]
    [InlineData("running", "production", true, false, true, true, false)]
    [InlineData("paused", "production", false, true, false, true, false)]
    [InlineData("completed", "production", true, false, false, false, true)]
    public void The_job_page_offers_what_its_status_allows(string status, string type, bool ready, bool start, bool pause, bool complete, bool reopen)
    {
        var actions = FloorView.ActionsFor(Job(status, type, ready));

        Assert.Equal((start, pause, complete, reopen), (actions.CanStart, actions.CanPause, actions.CanComplete, actions.CanReopen));
    }

    [Fact]
    public void Production_is_reported_once_a_job_has_run_until_it_is_closed()
    {
        Assert.True(FloorView.ActionsFor(Job("completed")).CanReport);
        Assert.False(FloorView.ActionsFor(Job("firmed", runs: 0)).CanReport);
        Assert.False(FloorView.ActionsFor(Job("closed")).CanReport);
        Assert.False(FloorView.ActionsFor(Job("running", type: "cleaning")).CanReport);
        Assert.False(FloorView.ActionsFor(Job("completed") with { WorkOrderStatus = "closed" }).CanReopen);
    }

    [Fact]
    public void A_board_job_starts_when_ready_on_a_free_line()
    {
        var job = new BoardJobDto { JobId = Guid.CreateVersion7(), JobNumber = "J-2", JobType = "production", Status = "firmed", IsReady = true };
        var free = new LineBoardRowDto { LineId = Guid.CreateVersion7(), LineCode = "EXT-01", LineName = "Line 1" };
        var busy = free with { Running = new RunningJobDto { JobId = Guid.CreateVersion7(), JobNumber = "J-1", JobType = "production", RunId = Guid.CreateVersion7() } };

        Assert.True(FloorView.CanStart(job, free));
        Assert.False(FloorView.CanStart(job, busy));
        Assert.False(FloorView.CanStart(job with { IsReady = false }, free));
    }
}
