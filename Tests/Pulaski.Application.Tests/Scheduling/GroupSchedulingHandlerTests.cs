using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Scheduling;

/// <summary>Planning to a department and the floor assigning the job to a line (D-022).</summary>
public class GroupSchedulingHandlerTests : SchedulingHandlerTestBase
{
    private static readonly DateTimeOffset Saturday = Now.AddDays(1);
    private static readonly DateTimeOffset Monday = Now.AddDays(3);

    private AssignJobHandler Assign() => new(F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.Events, F.Events, F.Time);

    private GetLineTimelineHandler Timeline() => new(F.Stores.Scheduling, F.Time);

    private async Task<Guid> PlanToAsync(SchedulingLine target, Guid workOrderId, DateTimeOffset? start, DateTimeOffset? end, decimal qty = 1000m) =>
        (await Create().HandleAsync(
            new CreateJobCommand(target.Id, "G-" + Guid.NewGuid().ToString("N")[..4], JobType.Production, workOrderId, qty, null, false, start, null, end),
            CancellationToken.None)).Id;

    [Fact]
    public async Task A_job_planned_to_a_department_waits_there_in_its_window()
    {
        var department = F.Stores.Scheduling.AddGroup(Site, Line(), Line());
        var wo = await WorkOrderAsync(White);

        var id = await PlanToAsync(department, wo.Id, Saturday, Monday);

        var job = Job(id);
        Assert.Equal((department.Id, (Guid?)department.Id, (DateTimeOffset?)Monday), (job.EquipmentId, job.GroupEquipmentId, job.LatestEnd));
        Assert.Equal(EventTypes.JobCreated, F.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Planning_to_a_department_needs_a_finish_by_and_a_line_below_that_can_run_the_material()
    {
        var wo = await WorkOrderAsync(White);
        var department = F.Stores.Scheduling.AddGroup(Site, Line());
        var incapable = F.Stores.Scheduling.AddGroup(Site, Line(eligible: false));

        await Assert.ThrowsAsync<DomainRuleException>(() => PlanToAsync(department, wo.Id, Saturday, null));
        await Assert.ThrowsAsync<DomainRuleException>(() => PlanToAsync(incapable, wo.Id, Saturday, Monday));
        await Assert.ThrowsAsync<DomainRuleException>(() => Create().HandleAsync(
            new CreateJobCommand(department.Id, "CLEAN", JobType.Cleaning, null, null, TimeSpan.FromHours(1), false, null, null, Monday),
            CancellationToken.None));
    }

    [Fact]
    public async Task The_floor_assigns_it_to_a_line_in_the_department_and_can_send_it_back()
    {
        var ext1 = Line();
        var ext2 = Line();
        var department = F.Stores.Scheduling.AddGroup(Site, ext1, ext2);
        var wo = await WorkOrderAsync(White);
        var queued = await CreateProductionAsync(ext1, wo, "Q-1");
        var id = await PlanToAsync(department, wo.Id, Saturday, Monday);

        await Assign().HandleAsync(new AssignJobCommand(id, Version(id), ext1.Id, queued), CancellationToken.None);

        Assert.Equal([id, queued], F.Stores.Scheduling.JobsOn(ext1.Id).Select(j => j.Id));
        Assert.Equal(EventTypes.JobAssigned, F.Stores.Events.Appended[^1].Type);

        await Assign().HandleAsync(new AssignJobCommand(id, Version(id), ext2.Id, null), CancellationToken.None);
        Assert.Equal(ext2.Id, Job(id).EquipmentId);

        await Assign().HandleAsync(new AssignJobCommand(id, Version(id), department.Id, null), CancellationToken.None);
        Assert.True(Job(id).IsOnGroup);
    }

    [Fact]
    public async Task Assignment_refuses_lines_outside_the_department_or_without_the_capability()
    {
        var capable = Line();
        var incapable = Line(eligible: false);
        var outside = Line();
        var department = F.Stores.Scheduling.AddGroup(Site, capable, incapable);
        var id = await PlanToAsync(department, (await WorkOrderAsync(White)).Id, Saturday, Monday);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Assign().HandleAsync(new AssignJobCommand(id, Version(id), outside.Id, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Assign().HandleAsync(new AssignJobCommand(id, Version(id), incapable.Id, null), CancellationToken.None));
        Assert.True(Job(id).IsOnGroup);
    }

    [Fact]
    public async Task Dispatch_publishes_a_department_job_with_its_window_as_the_baseline()
    {
        var department = F.Stores.Scheduling.AddGroup(Site, Line());
        var wo = await WorkOrderAsync(White);
        var id = await PlanToAsync(department, wo.Id, Saturday, Monday);

        var result = await new DispatchWorkOrderHandler(
                F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Events, F.Events, F.Time)
            .HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);

        var baseline = Assert.Single(result.Jobs);
        Assert.Equal((id, Saturday, (DateTimeOffset?)Monday), (baseline.JobId, baseline.PlannedStart, baseline.PlannedEnd));
        Assert.Equal(JobStatus.Dispatched, Job(id).Status);
    }

    [Fact]
    public async Task The_department_timeline_shows_windows_with_each_line_that_could_run_them()
    {
        var ext1 = Line();
        var department = F.Stores.Scheduling.AddGroup(Site, ext1, Line(eligible: false));
        var id = await PlanToAsync(department, (await WorkOrderAsync(White)).Id, Saturday, Monday, qty: 500m);

        var timeline = await Timeline().HandleAsync(new GetLineTimelineQuery(department.Id), CancellationToken.None);

        Assert.True(timeline.IsGroup);
        var window = Assert.Single(timeline.Segments);
        Assert.Equal((SegmentKind.Window, id, Saturday, Monday), (window.Kind, window.JobId, window.Start, window.End));
        var candidate = Assert.Single(window.Candidates);
        Assert.Equal((ext1.Id, (TimeSpan?)TimeSpan.FromHours(5), true), (candidate.LineId, candidate.Work, candidate.FitsWindow));
        Assert.Empty(window.Warnings);
    }

    [Fact]
    public async Task On_a_line_a_firmed_department_job_follows_its_sequence_rather_than_an_anchor()
    {
        var ext1 = Line();
        var department = F.Stores.Scheduling.AddGroup(Site, ext1);
        var wo = await WorkOrderAsync(White);
        var queued = await CreateProductionAsync(ext1, wo, "Q-1", qty: 300m);
        var id = await PlanToAsync(department, wo.Id, null, Monday, qty: 200m);
        var job = Job(id) with { Status = JobStatus.Firmed, PlannedStart = Now, PlannedEnd = Monday };
        F.Stores.Scheduling.AddJob(job, Version(id));

        await Assign().HandleAsync(new AssignJobCommand(id, Version(id), ext1.Id, null), CancellationToken.None);
        var timeline = await Timeline().HandleAsync(new GetLineTimelineQuery(ext1.Id), CancellationToken.None);

        var segment = Assert.Single(timeline.Segments, s => s.JobId == id);
        Assert.Equal((Now.AddHours(3), Now.AddHours(5)), (segment.Start, segment.End));
        Assert.Equal((department.Id, (DateTimeOffset?)Monday), (segment.GroupEquipmentId!.Value, segment.LatestEnd));
        Assert.Empty(timeline.Overlaps);
    }
}
