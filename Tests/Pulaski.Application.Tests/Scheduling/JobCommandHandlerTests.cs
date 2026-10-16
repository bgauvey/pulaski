using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

public class JobCommandHandlerTests : SchedulingHandlerTestBase
{
    private SequenceJobHandler Sequence() => new(F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.Events, F.Events, F.Time);

    private PinJobHandler Pin() => new(F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.Events, F.Events);

    private CancelJobHandler Cancel() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Boms, F.Stores.Units, F.Stores.Events, F.Events, F.Ids, F.Time);

    private DispatchWorkOrderHandler Dispatch() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Processes, F.Stores.Events, F.Events, F.Time);

    private FirmWorkOrderHandler Firm() => new(
        F.Stores.UnitOfWork, F.Stores.Scheduling, F.Stores.WorkOrders, F.Stores.Events, F.Events, F.Time);

    [Fact]
    public async Task Create_appends_a_production_job_with_the_work_order_material_and_unit()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);

        var first = await CreateProductionAsync(line, wo, "J-1");
        var second = await CreateProductionAsync(line, wo, "J-2");

        var job = Job(second);
        Assert.Equal((line.Id, Grey, Metre, wo.Id), (job.EquipmentId, job.MaterialId!.Value, job.UomId!.Value, job.WorkOrderId!.Value));
        Assert.Equal((1m, 2m), (Job(first).SequenceRank, job.SequenceRank));
        Assert.Equal(JobStatus.Planned, job.Status);
        Assert.Contains((second, (string?)null, JobStatus.Planned), F.Stores.Scheduling.StatusChanges);
        Assert.All(F.Stores.Events.Appended, e => Assert.Equal(EventTypes.JobCreated, e.Type));
        Assert.Equal(2, F.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Create_before_a_job_takes_the_midpoint()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var first = await CreateProductionAsync(line, wo, "J-1");
        var second = await CreateProductionAsync(line, wo, "J-2");

        var between = await CreateProductionAsync(line, wo, "J-3", before: second);

        Assert.Equal([first, between, second], F.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal(1.5m, Job(between).SequenceRank);
    }

    [Fact]
    public async Task Create_a_maintenance_job_with_a_duration()
    {
        var line = Line();

        var created = await Create().HandleAsync(
            new CreateJobCommand(line.Id, "PM-1", JobType.Maintenance, null, null, TimeSpan.FromHours(2), true, Now.AddDays(1), null),
            CancellationToken.None);

        var job = Job(created.Id);
        Assert.Equal((JobType.Maintenance, (TimeSpan?)TimeSpan.FromHours(2), true), (job.JobType, job.PlannedDuration, job.CountsAsPlannedDowntime));
        Assert.Equal(Now.AddDays(1), job.EarliestStart);
    }

    [Fact]
    public async Task Create_validates_the_shape_of_each_job_type()
    {
        var ex = await Assert.ThrowsAsync<ValidationException>(() => Create().HandleAsync(
            new CreateJobCommand(Guid.CreateVersion7(), "", JobType.Production, null, null, TimeSpan.FromHours(1), true, null, null),
            CancellationToken.None));

        Assert.Equal(
            ["CountsAsPlannedDowntime", "JobNumber", "PlannedDuration", "PlannedQty", "WorkOrderId"],
            ex.Errors.Keys.Order(StringComparer.Ordinal));

        var cleaning = await Assert.ThrowsAsync<ValidationException>(() => Create().HandleAsync(
            new CreateJobCommand(Guid.CreateVersion7(), "C-1", JobType.Cleaning, Guid.CreateVersion7(), 5m, null, false, null, null),
            CancellationToken.None));
        Assert.Equal(["PlannedQty", "WorkOrderId"], cleaning.Errors.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Create_refuses_ineligible_or_unschedulable_lines_unplanned_orders_other_sites_and_unknown_neighbours()
    {
        var line = Line();
        var ineligible = Line(eligible: false);
        var unschedulable = F.Stores.Scheduling.AddLine(Site, schedulable: false);
        var planned = await WorkOrderAsync(Grey);
        var dispatched = await WorkOrderAsync(Grey, WorkOrderStatus.Dispatched);
        var elsewhere = await WorkOrderAsync(Grey, site: Guid.CreateVersion7());

        var notSetUp = await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(ineligible, planned, "J-0"));
        Assert.Contains("capability", notSetUp.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(unschedulable, planned, "J-1"));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, dispatched, "J-2"));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, elsewhere, "J-3"));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, planned, "J-4", before: Guid.CreateVersion7()));
        Assert.Empty(F.Stores.Events.Appended);
    }

    [Fact]
    public async Task Sequence_moves_a_job_before_another_on_the_same_line()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var a = await CreateProductionAsync(line, wo, "J-1");
        var b = await CreateProductionAsync(line, wo, "J-2");
        var c = await CreateProductionAsync(line, wo, "J-3");

        var result = await Sequence().HandleAsync(new SequenceJobCommand(c, Version(c), line.Id, a), CancellationToken.None);

        Assert.Equal([c, a, b], F.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal(Version(c), result.Version);
        Assert.Equal(EventTypes.JobSequenced, F.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Sequence_moves_a_job_to_the_end_of_another_eligible_line()
    {
        var from = Line();
        var to = Line();
        var ineligible = Line(eligible: false);
        var wo = await WorkOrderAsync(Grey);
        var job = await CreateProductionAsync(from, wo, "J-1");
        var existing = await CreateProductionAsync(to, wo, "J-2");

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), ineligible.Id, null), CancellationToken.None));
        await Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), to.Id, null), CancellationToken.None);

        Assert.Empty(F.Stores.Scheduling.JobsOn(from.Id));
        Assert.Equal([existing, job], F.Stores.Scheduling.JobsOn(to.Id).Select(j => j.Id));
    }

    [Fact]
    public async Task Sequence_renumbers_the_line_when_ranks_run_out()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var a = await CreateProductionAsync(line, wo, "J-1");
        var b = await CreateProductionAsync(line, wo, "J-2");
        var c = await CreateProductionAsync(line, wo, "J-3");
        F.Stores.Scheduling.AddJob(Job(a) with { SequenceRank = 1.0000000001m }, Version(a));
        F.Stores.Scheduling.AddJob(Job(b) with { SequenceRank = 1.0000000002m }, Version(b));

        await Sequence().HandleAsync(new SequenceJobCommand(c, Version(c), line.Id, b), CancellationToken.None);

        Assert.Equal([a, c, b], F.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal((1m, 1.5m, 2m), (Job(a).SequenceRank, Job(c).SequenceRank, Job(b).SequenceRank));
    }

    [Fact]
    public async Task Sequence_refuses_locked_jobs_stale_versions_other_sites_and_dispatched_jobs()
    {
        var line = Line();
        var otherSite = Line(site: Guid.CreateVersion7());
        var wo = await WorkOrderAsync(Grey);
        var job = await CreateProductionAsync(line, wo, "J-1");

        await Assert.ThrowsAsync<ConflictException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job) - 1, line.Id, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), otherSite.Id, null), CancellationToken.None));

        await Pin().HandleAsync(new PinJobCommand(job, Version(job), true, null), CancellationToken.None);
        var locked = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), line.Id, null), CancellationToken.None));
        Assert.Contains("locked", locked.Message, StringComparison.Ordinal);

        await Pin().HandleAsync(new PinJobCommand(job, Version(job), false, null), CancellationToken.None);
        await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);
        var dispatched = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), line.Id, null), CancellationToken.None));
        Assert.Contains("un-dispatch", dispatched.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Firmed_job_changes_line_and_keeps_its_time()
    {
        var from = Line();
        var to = Line();
        var white = await WorkOrderAsync(White);
        var grey = await WorkOrderAsync(Grey);
        var early = await CreateProductionAsync(to, white, "T-1");       // 0-1 h on the target line
        var late = await CreateProductionAsync(to, white, "T-2", qty: 500m); // 1-6 h
        var firmed = await CreateProductionAsync(from, grey, "F-1");
        await Dispatch().HandleAsync(new DispatchWorkOrderCommand(grey.Id, WorkOrderVersion(grey)), CancellationToken.None);
        await Firm().HandleAsync(new FirmWorkOrderCommand(grey.Id, WorkOrderVersion(grey)), CancellationToken.None);
        var anchor = Job(firmed).PlannedStart!.Value;   // after the 30-minute changeover from white

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(firmed, Version(firmed), from.Id, null), CancellationToken.None));
        await Sequence().HandleAsync(new SequenceJobCommand(firmed, Version(firmed), to.Id, early), CancellationToken.None);

        // Placed by its time (between the job starting before it and the one after), not where it was dropped.
        Assert.Equal([early, firmed, late], F.Stores.Scheduling.JobsOn(to.Id).Select(j => j.Id));
        Assert.Equal((anchor, JobStatus.Firmed), (Job(firmed).PlannedStart!.Value, Job(firmed).Status));
    }

    [Fact]
    public async Task Pin_sets_lock_and_earliest_start_with_before_and_after()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var job = await CreateProductionAsync(line, wo, "J-1");

        await Pin().HandleAsync(new PinJobCommand(job, Version(job), true, Now.AddHours(6)), CancellationToken.None);

        Assert.Equal((true, (DateTimeOffset?)Now.AddHours(6)), (Job(job).IsLocked, Job(job).EarliestStart));
        Assert.Equal(EventTypes.JobPinned, F.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Cancel_a_planned_job_outright()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var job = await CreateProductionAsync(line, wo, "J-1");

        var result = await Cancel().HandleAsync(new CancelJobCommand(job, Version(job), "Not needed", false), CancellationToken.None);

        Assert.Null(result.ReplacementJobId);
        Assert.Equal(JobStatus.Cancelled, Job(job).Status);
        Assert.Empty(F.Stores.Scheduling.JobsOn(line.Id));
        var evt = F.Stores.Events.Appended[^1];
        Assert.Equal((EventTypes.JobCancelled, "Not needed"), (evt.Type, evt.Reason));
    }

    [Fact]
    public async Task Cancel_and_replace_a_started_job_for_what_it_did_not_make()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var job = await CreateProductionAsync(line, wo, "J-1", qty: 100m);
        await Dispatch().HandleAsync(new DispatchWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);
        await Firm().HandleAsync(new FirmWorkOrderCommand(wo.Id, WorkOrderVersion(wo)), CancellationToken.None);
        await SetStatusAsync(wo, WorkOrderStatus.InProgress);
        F.Stores.Scheduling.AddJob(Job(job) with { Status = JobStatus.Running }, Version(job));
        F.Stores.Scheduling.SetGoodQty(job, 60m);

        var result = await Cancel().HandleAsync(new CancelJobCommand(job, Version(job), "Die damage", true), CancellationToken.None);

        var replacement = Job(result.ReplacementJobId!.Value);
        Assert.Equal(JobStatus.Cancelled, Job(job).Status);
        Assert.Contains(job, F.Stores.Scheduling.EndedRuns);
        Assert.Equal(("J-1-R", 40m, JobStatus.Firmed, (Guid?)job), (replacement.JobNumber, replacement.PlannedQty!.Value, replacement.Status, replacement.ReplacesJobId));
        Assert.Equal([replacement.Id], F.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal(
            [EventTypes.JobCancelled, EventTypes.JobCreated],
            F.Stores.Events.Appended.TakeLast(2).Select(e => e.Type));
    }

    [Fact]
    public async Task Replace_refuses_a_job_with_nothing_left_to_make()
    {
        var line = Line();
        var wo = await WorkOrderAsync(Grey);
        var job = await CreateProductionAsync(line, wo, "J-1", qty: 100m);
        F.Stores.Scheduling.SetGoodQty(job, 100m);

        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Cancel().HandleAsync(new CancelJobCommand(job, Version(job), null, true), CancellationToken.None));
        Assert.Equal(JobStatus.Planned, Job(job).Status);
    }
}
