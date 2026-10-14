using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

public class WorkOrderPlanningHandlerTests : SchedulingHandlerTestBase
{
    private DispatchWorkOrderHandler Dispatch() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Events, F.Events, F.Time);

    private UndispatchWorkOrderHandler Undispatch() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Events, F.Events);

    private FirmWorkOrderHandler Firm() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Events, F.Events, F.Time);

    [Fact]
    public async Task Dispatch_publishes_the_baseline_for_every_job_of_the_order()
    {
        var a = Line();
        var b = Line();
        var white = await WorkOrderAsync(White, qty: 300m);
        var grey = await WorkOrderAsync(Grey);
        var other = await CreateProductionAsync(a, grey, "G-1");       // stays planned; changes over from white first
        var onA = await CreateProductionAsync(a, white, "W-1");
        var onB = await CreateProductionAsync(b, white, "W-2", qty: 200m);

        var result = await Dispatch().HandleAsync(new DispatchWorkOrderCommand(white.Id, WorkOrderVersion(white), "Week 42"), CancellationToken.None);

        Assert.Equal(WorkOrderStatus.Dispatched, Current(white).Status);
        Assert.Equal(WorkOrderVersion(white), result.Version);
        Assert.Empty(result.Warnings);
        Assert.Equal([onA, onB], result.Jobs.Select(j => j.JobId).Order());

        // On line A it follows the grey job (0.5 h changeover + 1 h); on line B it runs straight away.
        Assert.Equal((Now.AddHours(1.5), Now.AddHours(2.5), JobStatus.Dispatched), (Job(onA).PlannedStart!.Value, Job(onA).PlannedEnd!.Value, Job(onA).Status));
        Assert.Equal((Now, Now.AddHours(2)), (Job(onB).PlannedStart!.Value, Job(onB).PlannedEnd!.Value));
        Assert.Equal(JobStatus.Planned, Job(other).Status);

        var evt = F.Stores.Events.Appended[^1];
        Assert.Equal((EventTypes.WorkOrderDispatched, white.Id, "Week 42"), (evt.Type, evt.Subject, evt.Reason));
        Assert.Contains((onB, (string?)JobStatus.Planned, JobStatus.Dispatched), F.Stores.Scheduling.StatusChanges);
    }

    [Fact]
    public async Task Dispatch_warns_when_job_quantities_do_not_add_up()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White, qty: 1000m);
        await CreateProductionAsync(line, wo, "W-1", qty: 400m);

        var result = await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        Assert.Equal("quantity_mismatch", Assert.Single(result.Warnings).Code);
    }

    [Fact]
    public async Task Dispatch_refuses_an_order_without_jobs_or_with_a_job_that_is_no_longer_eligible()
    {
        var line = Line(eligible: false);
        var empty = await WorkOrderAsync(White);
        var wo = await WorkOrderAsync(White);
        F.Stores.Scheduling.AddCapability(line.Id, White);
        var job = await CreateProductionAsync(line, wo, "W-1");
        F.Stores.Scheduling.AddJob(Job(job) with { MaterialId = Grey }, Version(job));   // e.g. capability retired since

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Dispatch().HandleAsync(new DispatchWorkOrderCommand(empty.Id, WorkOrderVersion(empty)), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() =>
            Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo) + 1), CancellationToken.None));
        Assert.Equal(WorkOrderStatus.Planned, Current(wo).Status);
    }

    [Fact]
    public async Task Undispatch_takes_the_order_and_its_jobs_back_to_planned()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White);
        var job = await CreateProductionAsync(line, wo, "W-1");
        await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        await Undispatch().HandleAsync(new UndispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        Assert.Equal((WorkOrderStatus.Planned, JobStatus.Planned), (Current(wo).Status, Job(job).Status));
        Assert.Null(Job(job).PlannedStart);
        Assert.Equal(EventTypes.WorkOrderUndispatched, F.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Firm_freezes_the_order_and_it_can_no_longer_be_undispatched()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White);
        var job = await CreateProductionAsync(line, wo, "W-1");
        await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);
        var baseline = Job(job).PlannedStart;
        F.Time.Advance(TimeSpan.FromHours(1));

        await Firm().HandleAsync(new FirmWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        Assert.Equal((WorkOrderStatus.Firmed, JobStatus.Firmed), (Current(wo).Status, Job(job).Status));
        Assert.Equal((baseline, Now.AddHours(1)), (Job(job).PlannedStart, Job(job).FirmedAt!.Value));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Undispatch().HandleAsync(new UndispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None));
        Assert.Contains("firmed", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Planned_order_cannot_be_firmed()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White);
        await CreateProductionAsync(line, wo, "W-1");

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Firm().HandleAsync(new FirmWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None));
    }

    [Fact]
    public async Task Firmed_job_holds_its_time_when_work_is_added_ahead_of_it()
    {
        var line = Line();
        var wo = await WorkOrderAsync(White);
        var job = await CreateProductionAsync(line, wo, "W-1");
        await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);
        await Firm().HandleAsync(new FirmWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        var rush = await WorkOrderAsync(White);
        await CreateProductionAsync(line, rush, "R-1", before: job);

        var timeline = await new GetLineTimelineHandler(F.Stores.Scheduling, F.Time)
            .HandleAsync(new GetLineTimelineQuery(line.Id), CancellationToken.None);
        var firmed = timeline.Segments.Single(s => s.JobId == job);
        Assert.Equal(Now, firmed.Start);
        Assert.Equal(ScheduleWarningCode.FirmedConflict, Assert.Single(firmed.Warnings).Code);
    }
}
