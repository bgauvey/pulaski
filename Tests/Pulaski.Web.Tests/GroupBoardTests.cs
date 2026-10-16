using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Tests;

/// <summary>A department and its lines on the board (D-022).</summary>
public class GroupBoardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private static readonly EquipmentDto Dept = Equipment("EXT", null, schedulable: true, runs: false);
    private static readonly EquipmentDto Ext1 = Equipment("EXT-01", Dept.Id, schedulable: true, runs: true);
    private static readonly EquipmentDto Ext2 = Equipment("EXT-02", Dept.Id, schedulable: true, runs: true);
    private static readonly EquipmentDto Other = Equipment("MOLD-01", null, schedulable: true, runs: true);

    private static EquipmentDto Equipment(string code, Guid? parent, bool schedulable, bool runs) => new()
    {
        Id = Guid.CreateVersion7(),
        ParentId = parent,
        Code = code,
        Name = code,
        Level = runs ? "work_center" : "area",
        EquipmentRole = runs ? "production_line" : "area",
        IsSchedulable = schedulable,
        CanRunJob = runs,
        IsActive = true,
    };

    private static TimelineSegmentDto Segment(string number, string kind = "job", string status = "planned", Guid? group = null,
        IReadOnlyList<CandidateLineDto>? candidates = null) => new()
        {
            Kind = kind,
            JobId = Guid.CreateVersion7(),
            JobNumber = number,
            JobType = "production",
            Status = status,
            Start = T0,
            End = T0.AddDays(2),
            GroupEquipmentId = group,
            Candidates = candidates ?? [],
            Version = 1,
        };

    private static BoardLine Row(EquipmentDto e, params TimelineSegmentDto[] segments) =>
        new(e, new LineTimelineDto
        {
            LineId = e.Id,
            LineCode = e.Code,
            LineName = e.Name,
            ChangeoverMode = "auto",
            IsGroup = e.IsSchedulable && !e.CanRunJob,
            Segments = segments,
        });

    private static readonly Dictionary<Guid, Guid?> Parents = new[] { Dept, Ext1, Ext2, Other }.ToDictionary(e => e.Id, e => e.ParentId);

    [Fact]
    public void A_window_drops_only_on_the_lines_that_could_run_it_and_shows_as_a_ghost_there()
    {
        var window = Segment("WO-1-01", "window", group: Dept.Id, candidates: [new CandidateLineDto(Ext1.Id, "EXT-01", TimeSpan.FromHours(10), true)]);
        var board = ScheduleBoard.Build([Row(Dept, window), Row(Ext1), Row(Ext2), Row(Other)], [], T0, Parents);
        var item = board.Find(window.JobId, "window")!;

        Assert.True(board.CanDrop(item, board.FindLine(Ext1.Id)!));
        Assert.False(board.CanDrop(item, board.FindLine(Ext2.Id)!));     // in the department, but no capability
        Assert.False(board.CanDrop(item, board.FindLine(Other.Id)!));    // outside it
        Assert.False(board.CanDrop(item, board.FindLine(Dept.Id)!));     // already there
        Assert.Equal(["EXT-01"], board.AssignTargets(item).Select(l => l.Code));
        Assert.Equal(window.JobId, Assert.Single(board.GhostsOn(Ext1.Id)).Window.JobId);
        Assert.Empty(board.GhostsOn(Ext2.Id));
    }

    [Fact]
    public void An_assigned_job_moves_between_the_department_lines_or_back_and_dispatch_does_not_stop_it()
    {
        var job = Segment("WO-1-01", status: "dispatched", group: Dept.Id);
        var board = ScheduleBoard.Build([Row(Dept), Row(Ext1, job), Row(Ext2), Row(Other)], [], T0, Parents);
        var item = board.Find(job.JobId)!;

        Assert.True(item.CanChangeLine);
        Assert.Equal(["EXT", "EXT-02"], board.AssignTargets(item).Select(l => l.Code));
        Assert.False(board.CanDrop(item, board.FindLine(Other.Id)!));
    }

    [Fact]
    public void A_job_planned_to_a_line_never_drops_on_a_department()
    {
        var job = Segment("J-1");
        var board = ScheduleBoard.Build([Row(Dept), Row(Ext1, job), Row(Ext2)], [], T0, Parents);
        var item = board.Find(job.JobId)!;

        Assert.False(board.CanDrop(item, board.FindLine(Dept.Id)!));
        Assert.True(board.CanDrop(item, board.FindLine(Ext2.Id)!));
        Assert.True(board.IsUnder(Dept.Id, Ext1.Id));
        Assert.False(board.IsUnder(Ext1.Id, Dept.Id));
    }

    [Fact]
    public void A_suggested_window_opens_on_the_hour_and_closes_at_the_due_date_or_two_days_on()
    {
        var now = new DateTimeOffset(2026, 10, 16, 12, 20, 0, TimeSpan.Zero);
        var saturday = new DateTimeOffset(2026, 10, 17, 9, 45, 0, TimeSpan.Zero);
        var monday = new DateTimeOffset(2026, 10, 19, 6, 0, 0, TimeSpan.Zero);

        Assert.Equal((now.AddMinutes(40), monday), ScheduleLayout.SuggestWindow(now, monday, null));
        Assert.Equal((saturday.AddMinutes(-45), monday), ScheduleLayout.SuggestWindow(now, monday, null, saturday));
        Assert.Equal((now.AddMinutes(40), now.AddMinutes(40).AddDays(2)), ScheduleLayout.SuggestWindow(now, null, null, now.AddDays(-3)));
    }
}
