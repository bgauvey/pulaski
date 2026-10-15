using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Tests;

public class ScheduleBoardTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Site = Guid.CreateVersion7();
    private static readonly EquipmentDto Ext1 = Line("EXT-01");
    private static readonly EquipmentDto Ext2 = Line("EXT-02");

    private static EquipmentDto Line(string code) => new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Site,
        Code = code,
        Name = code,
        Level = "work_center",
        EquipmentRole = "production_line",
        IsSchedulable = true,
        IsActive = true,
    };

    private static TimelineSegmentDto Job(
        string number,
        double startHours,
        double endHours,
        string status = "planned",
        Guid? workOrderId = null,
        decimal? qty = null,
        long version = 1,
        string kind = "job",
        Guid? id = null) => new()
        {
            Kind = kind,
            JobId = id ?? Guid.CreateVersion7(),
            JobNumber = number,
            JobType = kind == "job" ? "production" : "changeover",
            Status = status,
            WorkOrderId = workOrderId,
            Start = T0.AddHours(startHours),
            End = T0.AddHours(endHours),
            PlannedQty = qty,
            Version = kind == "job" ? version : null,
        };

    private static BoardLine Timeline(EquipmentDto line, IReadOnlyList<TimelineSegmentDto> segments, IReadOnlyList<TimelineOverlapDto>? overlaps = null) =>
        new(line, new LineTimelineDto
        {
            LineId = line.Id,
            LineCode = line.Code,
            LineName = line.Name,
            ChangeoverMode = "auto",
            Segments = segments,
            Overlaps = overlaps ?? [],
        });

    private static WorkOrderDto Order(string number, decimal qty, string status = "planned", long version = 1) => new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Site,
        SiteCode = "PLT1",
        OrderNumber = number,
        MaterialId = Guid.CreateVersion7(),
        MaterialCode = "PRF",
        MaterialName = "Profile",
        PlannedQty = qty,
        UomId = Guid.CreateVersion7(),
        UomCode = "m",
        Status = status,
        Origin = "local",
        Version = version,
    };

    [Fact]
    public void Overlaps_are_resolved_to_the_items_on_their_line()
    {
        var a = Job("J-1", 0, 4);
        var firmed = Job("J-2", 3, 5, "firmed");
        var board = ScheduleBoard.Build(
            [Timeline(Ext1, [a, firmed], [new(a.JobId, "job", firmed.JobId, "job", T0.AddHours(3), T0.AddHours(4))])],
            [],
            T0);

        var overlap = Assert.Single(board.Overlaps);
        Assert.Equal(("J-1", "J-2", TimeSpan.FromHours(1)), (overlap.First.Segment.JobNumber, overlap.Second.Segment.JobNumber, overlap.Duration));
        Assert.Equal("J-1 and J-2 overlap by 1h on EXT-01", overlap.Describe());
        Assert.True(board.IsOverlapping(board.Find(a.JobId)!));
        Assert.True(board.IsOverlapping(board.Find(firmed.JobId)!));
    }

    [Fact]
    public void A_placeholder_and_its_job_are_separate_items()
    {
        var id = Guid.CreateVersion7();
        var board = ScheduleBoard.Build(
            [Timeline(Ext1, [Job("J-1", 0, 1, kind: "changeover_placeholder", id: id), Job("J-1", 1, 3, id: id)])],
            [],
            T0);

        Assert.Equal(2, board.Items.Count);
        Assert.True(board.Find(id, "changeover_placeholder")!.IsPlaceholder);
        Assert.False(board.Find(id)!.IsPlaceholder);
    }

    [Fact]
    public void An_overlap_with_a_changeover_names_the_job_it_precedes()
    {
        var id = Guid.CreateVersion7();
        var placeholder = Job("J-2", 0, 1, kind: "changeover_placeholder", id: id);
        var firmed = Job("J-2", 0.5, 2, "firmed", id: id);
        var board = ScheduleBoard.Build(
            [Timeline(Ext1, [placeholder, firmed], [new(id, "changeover_placeholder", id, "job", T0.AddHours(0.5), T0.AddHours(1))])],
            [],
            T0);

        Assert.Equal("The changeover before J-2 and J-2 overlap by 30m on EXT-01", Assert.Single(board.Overlaps).Describe());
    }

    [Fact]
    public void New_overlaps_are_only_the_ones_not_seen_before()
    {
        var a = Job("J-1", 0, 4);
        var b = Job("J-2", 3, 5, "firmed");
        var c = Job("J-3", 4.5, 6, "firmed");
        var first = ScheduleBoard.Build([Timeline(Ext1, [a, b, c], [new(a.JobId, "job", b.JobId, "job", T0.AddHours(3), T0.AddHours(4))])], [], T0);
        var second = ScheduleBoard.Build(
            [Timeline(Ext1, [a, b, c],
            [
                new(a.JobId, "job", b.JobId, "job", T0.AddHours(3), T0.AddHours(4)),
                new(b.JobId, "job", c.JobId, "job", T0.AddHours(4.5), T0.AddHours(5)),
            ])],
            [],
            T0);

        var added = Assert.Single(second.NewOverlapsSince(first));
        Assert.Equal("J-3", added.Second.Segment.JobNumber);
        Assert.Empty(second.NewOverlapsSince(second));
    }

    [Fact]
    public void Changed_lists_moved_items_and_changed_orders_but_nothing_on_first_load()
    {
        var order = Order("WO-1", 100m);
        var still = Job("J-1", 0, 1);
        var moving = Job("J-2", 1, 2);
        var before = ScheduleBoard.Build([Timeline(Ext1, [still, moving])], [order], T0);
        var after = ScheduleBoard.Build(
            [Timeline(Ext1, [still, moving with { Start = T0.AddHours(2), End = T0.AddHours(3), Version = 2 }])],
            [order with { Version = 2 }],
            T0);

        Assert.Empty(before.ChangedSince(ScheduleBoard.Empty));
        Assert.Equal(
            new HashSet<string> { BoardItem.KeyOf(moving.JobId, "job"), ScheduleBoard.WorkOrderKey(order.Id) },
            after.ChangedSince(before));
    }

    [Fact]
    public void Unplaced_quantity_is_what_a_planned_order_has_not_put_on_a_line()
    {
        var planned = Order("WO-1", 1000m);
        var full = Order("WO-2", 500m);
        var dispatched = Order("WO-3", 800m, "dispatched");
        var board = ScheduleBoard.Build(
            [
                Timeline(Ext1, [Job("WO-1-01", 0, 1, workOrderId: planned.Id, qty: 400m), Job("WO-2-01", 1, 2, workOrderId: full.Id, qty: 500m)]),
                Timeline(Ext2, [Job("WO-1-02", 0, 1, workOrderId: planned.Id, qty: 100m)]),
            ],
            [planned, full, dispatched],
            T0);

        Assert.Equal(500m, board.UnplacedQty(planned));
        Assert.Equal(0m, board.UnplacedQty(full));
        Assert.Equal(0m, board.UnplacedQty(dispatched));
        Assert.Equal(["WO-1"], board.Unscheduled().Select(w => w.OrderNumber));
        Assert.Equal(["EXT-01", "EXT-02"], board.JobsFor(planned.Id).Select(j => j.LineCode));
    }

    [Fact]
    public void Next_job_number_skips_numbers_already_taken()
    {
        var order = Order("WO-7", 100m);
        var board = ScheduleBoard.Build([Timeline(Ext1, [Job("WO-7-01", 0, 1), Job("WO-7-02", 1, 2)])], [order], T0);

        Assert.Equal("WO-7-03", board.NextJobNumber(order));
    }

    [Fact]
    public void Planned_jobs_resequence_firmed_jobs_only_change_line_and_locked_jobs_stay_put()
    {
        var board = ScheduleBoard.Build(
            [Timeline(Ext1, [
                Job("P", 0, 1),
                Job("F", 1, 2, "firmed"),
                Job("D", 2, 3, "dispatched"),
                Job("L", 3, 4) with { IsLocked = true },
            ])],
            [],
            T0);

        Assert.Equal(
            [("P", true, true), ("F", false, true), ("D", false, false), ("L", false, false)],
            board.Items.Select(i => (i.Segment.JobNumber, i.CanResequence, i.CanChangeLine)));
    }
}

public class ScheduleLayoutTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);

    private static BoardItem Item(string number, double startHours, double endHours, string status = "planned", string kind = "job", Guid? id = null) =>
        new(Guid.Empty, "EXT-01", new TimelineSegmentDto
        {
            Kind = kind,
            JobId = id ?? Guid.CreateVersion7(),
            JobNumber = number,
            JobType = "production",
            Status = status,
            Start = T0.AddHours(startHours),
            End = T0.AddHours(endHours),
        });

    [Fact]
    public void A_drop_goes_before_the_first_job_whose_middle_is_later()
    {
        var a = Item("A", 0, 2);
        var b = Item("B", 2, 6);
        var c = Item("C", 6, 8);
        IReadOnlyList<BoardItem> line = [a, b, c];

        Assert.Equal(a.JobId, ScheduleLayout.BeforeJobAt(line, T0.AddHours(0.5), null));
        Assert.Equal(b.JobId, ScheduleLayout.BeforeJobAt(line, T0.AddHours(3), null));
        Assert.Equal(c.JobId, ScheduleLayout.BeforeJobAt(line, T0.AddHours(5), null));
        Assert.Null(ScheduleLayout.BeforeJobAt(line, T0.AddHours(7.5), null));
    }

    [Fact]
    public void A_drop_skips_the_moving_job_the_running_job_and_placeholders()
    {
        var running = Item("R", -1, 1, "running");
        var placeholder = Item("C", 1, 2, kind: "changeover_placeholder");
        var moving = Item("M", 2, 3);
        var next = Item("N", 3, 4);

        Assert.Equal(next.JobId, ScheduleLayout.BeforeJobAt([running, placeholder, moving, next], T0, moving.JobId));
    }

    [Fact]
    public void Next_job_after_ignores_placeholders()
    {
        var a = Item("A", 0, 1);
        var placeholder = Item("B", 1, 2, kind: "changeover_placeholder");
        var b = Item("B", 2, 3, id: placeholder.JobId);

        Assert.Equal(b.JobId, ScheduleLayout.NextJobAfter([a, placeholder, b], a.JobId));
        Assert.Null(ScheduleLayout.NextJobAfter([a, placeholder, b], b.JobId));
    }

    [Fact]
    public void Overlapping_bars_stack_in_lanes_and_back_to_back_bars_share_one()
    {
        var a = Item("A", 0, 4);
        var b = Item("B", 3, 5);
        var c = Item("C", 5, 6);

        var lanes = ScheduleLayout.Lanes([a, b, c]);

        Assert.Equal([0, 1, 0], new[] { a, b, c }.Select(i => lanes[i.Key]));
    }

    [Fact]
    public void Month_grid_is_six_weeks_starting_on_monday()
    {
        var grid = ScheduleLayout.MonthGrid(new DateOnly(2026, 10, 1));

        Assert.Equal(42, grid.Count);
        Assert.Equal(new DateOnly(2026, 9, 28), grid[0]);
        Assert.All(grid.Where((_, i) => i % 7 == 0), d => Assert.Equal(DayOfWeek.Monday, d.DayOfWeek));
    }

    [Fact]
    public void Items_show_on_every_site_day_they_run()
    {
        // 22:00 to 02:00 UTC is 17:00 to 21:00 in Chicago (CDT), all on one site day.
        var evening = Item("E", 16, 20);
        var overnight = Item("O", 20, 30);

        Assert.Equal(["E", "O"], ScheduleLayout.OnDay([evening, overnight], new DateOnly(2026, 10, 14), "America/Chicago").Select(i => i.Segment.JobNumber));
        Assert.Equal(["O"], ScheduleLayout.OnDay([evening, overnight], new DateOnly(2026, 10, 15), "America/Chicago").Select(i => i.Segment.JobNumber));
        Assert.Empty(ScheduleLayout.OnDay([evening, overnight], new DateOnly(2026, 10, 16), "America/Chicago"));
    }

    [Fact]
    public void Gantt_scale_maps_time_to_percent_and_back()
    {
        var scale = new GanttScale(T0, TimeSpan.FromHours(10), null);

        Assert.Equal(25d, scale.Percent(T0.AddHours(2.5)), 6);
        Assert.Equal(0d, scale.Percent(T0.AddHours(-1)));
        Assert.Equal(100d, scale.Percent(T0.AddHours(11)));
        Assert.Equal(20d, scale.Width(T0.AddHours(-3), T0.AddHours(2)), 6);
        Assert.Equal(T0.AddHours(5), scale.At(0.5));
    }

    [Theory]
    [InlineData(45, "45m")]
    [InlineData(60, "1h")]
    [InlineData(135, "2h 15m")]
    [InlineData(1500, "1d 1h")]
    [InlineData(2880, "2d")]
    public void Durations_read_short(int minutes, string expected) =>
        Assert.Equal(expected, ScheduleFormat.Duration(TimeSpan.FromMinutes(minutes)));
}
