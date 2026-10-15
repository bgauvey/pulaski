using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

public class PlacementPreviewHandlerTests : SchedulingHandlerTestBase
{
    private PreviewJobPlacementHandler Preview() => new(F.Stores.Scheduling, F.Stores.WorkOrders, F.Ids, F.Time);

    [Fact]
    public async Task Preview_at_the_end_shows_the_changeover_and_runtime_and_saves_nothing()
    {
        var line = Line();
        var white = await WorkOrderAsync(White);
        var queued = await CreateProductionAsync(line, white, "W-1");   // 100 m at 100 m/h: Now to Now+1h
        var grey = await WorkOrderAsync(Grey);

        var result = await Preview().HandleAsync(new PreviewJobPlacementQuery(line.Id, grey.Id, 200m, null), CancellationToken.None);

        var mine = result.Timeline.Segments.Where(s => s.JobId == result.PreviewJobId).ToList();
        Assert.Equal(
            [(SegmentKind.ChangeoverPlaceholder, Now.AddHours(1), Now.AddHours(1.5)), (SegmentKind.Job, Now.AddHours(1.5), Now.AddHours(3.5))],
            mine.Select(s => (s.Kind, s.Start, s.End)));
        Assert.Equal((grey.Id, grey.OrderNumber, (decimal?)200m), (mine[1].WorkOrderId, mine[1].JobNumber, mine[1].PlannedQty));
        Assert.Empty(result.Delayed);
        Assert.Equal([queued], (await F.Stores.Scheduling.GetLinePlanAsync(line.Id, Now, Now.AddDays(1), CancellationToken.None))!.Jobs.Select(j => j.JobId));
    }

    [Fact]
    public async Task Preview_before_a_job_reports_how_far_it_pushes_that_job()
    {
        var line = Line();
        var white = await WorkOrderAsync(White);
        var queued = await CreateProductionAsync(line, white, "W-1");
        var another = await WorkOrderAsync(White);

        var result = await Preview().HandleAsync(
            new PreviewJobPlacementQuery(line.Id, another.Id, 300m, queued, "W-2"), CancellationToken.None);

        var mine = Assert.Single(result.Timeline.Segments, s => s.JobId == result.PreviewJobId);
        Assert.Equal(("W-2", Now, Now.AddHours(3)), (mine.JobNumber, mine.Start, mine.End));
        var delay = Assert.Single(result.Delayed);
        Assert.Equal((queued, "W-1", TimeSpan.FromHours(3)), (delay.JobId, delay.JobNumber, delay.Delay));
    }

    [Fact]
    public async Task Preview_refuses_what_creating_the_job_would_refuse()
    {
        var eligible = Line();
        var ineligible = Line(eligible: false);
        var planned = await WorkOrderAsync(White);
        var dispatched = await WorkOrderAsync(White, WorkOrderStatus.Dispatched);
        var elsewhere = await WorkOrderAsync(White, site: Guid.CreateVersion7());

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Preview().HandleAsync(new PreviewJobPlacementQuery(ineligible.Id, planned.Id, 10m, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Preview().HandleAsync(new PreviewJobPlacementQuery(eligible.Id, dispatched.Id, 10m, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Preview().HandleAsync(new PreviewJobPlacementQuery(eligible.Id, elsewhere.Id, 10m, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Preview().HandleAsync(new PreviewJobPlacementQuery(eligible.Id, planned.Id, 10m, Guid.CreateVersion7()), CancellationToken.None));
    }
}
