using Pulaski.Application.Scheduling;
using Pulaski.Application.Tests.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.WorkOrders;

public class WorkOrderProgressHandlerTests : SchedulingHandlerTestBase
{
    private ListWorkOrderProgressHandler Progress() =>
        new(F.Stores.WorkOrders, F.Stores.Processes, new GetLineTimelineHandler(F.Stores.Scheduling, F.Time));

    /// <summary>The selection: the site row, plus each given line as equipment that holds jobs.</summary>
    private void Select(params Guid[] lines) =>
        F.Stores.WorkOrders.Scope = id => id is null
            ? []
            : [new ScopeEquipmentRow { Id = Site, SiteId = Site }, .. lines.Select(l => new ScopeEquipmentRow { Id = l, SiteId = Site, HoldsJobs = true })];

    private async Task<WorkOrder> DueAsync(Guid material, DateTimeOffset due, decimal qty = 1000m)
    {
        var wo = await WorkOrderAsync(material, qty: qty);
        await F.Stores.WorkOrders.InsertAsync(wo with { DueAt = due }, Now, CancellationToken.None);
        return Current(wo);
    }

    [Fact]
    public async Task An_order_rolls_up_its_projected_jobs_and_how_late_it_finishes()
    {
        var line = Line();
        Select(line.Id);
        var wo = await DueAsync(White, Now.AddHours(1));
        await CreateProductionAsync(line, wo, "W-1", qty: 100m);   // Now to +1h
        await CreateProductionAsync(line, wo, "W-2", qty: 150m);   // +1h to +2.5h
        F.Stores.WorkOrders.Facts[wo.Id] = new WorkOrderFactsRow { WorkOrderId = wo.Id, CompletedQty = 40m, OpenJobs = 2 };

        var progress = Assert.Single(await Progress().HandleAsync(new ListWorkOrderProgressQuery(Site), CancellationToken.None));

        Assert.Equal(["W-1", "W-2"], progress.Jobs.Select(j => j.JobNumber));
        Assert.Equal((Now, Now.AddHours(2.5)), (progress.ProjectedStart!.Value, progress.ProjectedEnd!.Value));
        Assert.Equal(TimeSpan.FromHours(1.5), progress.FinishSlip);
        Assert.Null(progress.StartSlip);
        Assert.Equal((40m, 250m), (progress.CompletedQty, progress.PlacedQty));
        Assert.NotEmpty(progress.Order.NextStatuses);
    }

    [Fact]
    public async Task A_dispatched_order_projected_after_its_baseline_has_slipped()
    {
        var line = Line();
        Select(line.Id);
        var wo = await WorkOrderAsync(White);
        var id = await CreateProductionAsync(line, wo, "W-1");
        F.Stores.Scheduling.AddJob(
            Job(id) with { Status = JobStatus.Dispatched, PlannedStart = Now.AddHours(-2), PlannedEnd = Now.AddHours(-1) }, Version(id));

        var progress = Assert.Single(await Progress().HandleAsync(new ListWorkOrderProgressQuery(Site), CancellationToken.None));

        Assert.Equal(TimeSpan.FromHours(2), progress.StartSlip);
        Assert.Equal(Now.AddHours(-2), progress.BaselineStart);
    }

    [Fact]
    public async Task Only_orders_with_jobs_in_the_selection_or_no_jobs_at_all_are_listed()
    {
        var inside = Line();
        var outside = Line();
        Select(inside.Id);
        var here = await WorkOrderAsync(White);
        await CreateProductionAsync(inside, here, "H-1");
        var elsewhere = await WorkOrderAsync(White);
        await CreateProductionAsync(outside, elsewhere, "E-1");
        F.Stores.WorkOrders.Facts[elsewhere.Id] = new WorkOrderFactsRow { WorkOrderId = elsewhere.Id, OpenJobs = 1 };
        var unplanned = await WorkOrderAsync(White);
        var otherSite = await WorkOrderAsync(White, site: Guid.CreateVersion7());

        var listed = await Progress().HandleAsync(new ListWorkOrderProgressQuery(Site), CancellationToken.None);

        Assert.Equal(
            new HashSet<Guid> { here.Id, unplanned.Id },
            listed.Select(p => p.Order.Id).ToHashSet());
        Assert.Contains(otherSite.Id, (await Progress().HandleAsync(new ListWorkOrderProgressQuery(null), CancellationToken.None)).Select(p => p.Order.Id));
    }
}
