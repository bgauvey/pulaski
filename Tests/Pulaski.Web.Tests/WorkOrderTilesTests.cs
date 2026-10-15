using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

public class WorkOrderTilesTests
{
    // 14:00 in Chicago (CDT, UTC-5).
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 19, 0, 0, TimeSpan.Zero);
    private const string Chicago = "America/Chicago";

    private static WorkOrderProgressDto Progress(
        string status = "dispatched",
        DateTimeOffset? start = null,
        DateTimeOffset? end = null,
        TimeSpan? startSlip = null,
        TimeSpan? finishSlip = null,
        bool running = false,
        decimal planned = 100m,
        decimal placed = 100m,
        bool overlap = false,
        IReadOnlyList<ScheduleWarningDto>? warnings = null) => new()
        {
            Order = new WorkOrderDto
            {
                Id = Guid.CreateVersion7(),
                SiteId = Guid.CreateVersion7(),
                SiteCode = "PLT1",
                OrderNumber = "WO-1",
                MaterialId = Guid.CreateVersion7(),
                MaterialCode = "M",
                MaterialName = "M",
                PlannedQty = planned,
                UomId = Guid.CreateVersion7(),
                UomCode = "m",
                Status = status,
                Origin = "local",
            },
            Jobs = placed > 0 ? [new WorkOrderJobDto(Guid.CreateVersion7(), "J", "job", status, Guid.CreateVersion7(), "L", Now, Now, placed, placed, [])] : [],
            ProjectedStart = start,
            ProjectedEnd = end,
            StartSlip = startSlip,
            FinishSlip = finishSlip,
            IsRunning = running,
            PlacedQty = placed,
            HasOverlap = overlap,
            Warnings = warnings ?? [],
        };

    [Fact]
    public void Today_is_the_site_day_not_the_utc_day()
    {
        // 23:00 Chicago is already tomorrow in UTC, but still today on site.
        var late = Progress(start: Now.AddHours(9), end: Now.AddHours(9.5));
        var tomorrow = Progress(start: Now.AddHours(11), end: Now.AddHours(12));

        Assert.Equal(new HashSet<string> { WorkOrderTiles.StartingToday, WorkOrderTiles.FinishingToday, WorkOrderTiles.OnTrack },
            WorkOrderTiles.Of(late, Now, Chicago));
        Assert.Equal(new HashSet<string> { WorkOrderTiles.OnTrack }, WorkOrderTiles.Of(tomorrow, Now, Chicago));
    }

    [Fact]
    public void A_running_order_is_not_starting_today()
    {
        var running = Progress(start: Now.AddHours(-1), end: Now.AddHours(1), running: true);

        Assert.DoesNotContain(WorkOrderTiles.StartingToday, WorkOrderTiles.Of(running, Now, Chicago));
    }

    [Fact]
    public void Behind_means_finishing_after_due_or_starting_well_after_the_baseline()
    {
        Assert.Contains(WorkOrderTiles.Behind, WorkOrderTiles.Of(Progress(finishSlip: TimeSpan.FromMinutes(1)), Now, Chicago));
        Assert.Contains(WorkOrderTiles.Behind, WorkOrderTiles.Of(Progress(startSlip: TimeSpan.FromMinutes(16)), Now, Chicago));
        Assert.Equal(new HashSet<string> { WorkOrderTiles.OnTrack }, WorkOrderTiles.Of(Progress(startSlip: TimeSpan.FromMinutes(10)), Now, Chicago));
    }

    [Fact]
    public void Warnings_overlaps_and_unplaced_quantity_are_flagged_and_closed_orders_count_nowhere()
    {
        Assert.Contains(WorkOrderTiles.NeedAttention, WorkOrderTiles.Of(Progress(overlap: true), Now, Chicago));
        Assert.Contains(WorkOrderTiles.NeedAttention, WorkOrderTiles.Of(Progress(warnings: [new("late", "Late.")]), Now, Chicago));
        Assert.Equal(new HashSet<string> { WorkOrderTiles.NotOnALine }, WorkOrderTiles.Of(Progress("planned", placed: 0m), Now, Chicago));
        Assert.Empty(WorkOrderTiles.Of(Progress("closed", finishSlip: TimeSpan.FromHours(1)), Now, Chicago));
    }
}
