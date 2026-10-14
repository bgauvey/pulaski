using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Scheduling;

public class JobCommandHandlerTests
{
    private static readonly DateTimeOffset Now = HandlerFixture.Now;

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Guid _metre = Guid.CreateVersion7();
    private readonly Guid _white = Guid.CreateVersion7();
    private readonly Guid _grey = Guid.CreateVersion7();

    private CreateJobHandler Create() => new(
        _f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.WorkOrders, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private SequenceJobHandler Sequence() => new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Events, _f.Events, _f.Time);

    private DispatchJobHandler Dispatch() => new(
        _f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.WorkOrders, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private PublishScheduleHandler Publish() => new(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Events, _f.Events, _f.Time);

    /// <summary>A line that last ran white, with 100 m/h rates and a 30-minute standard changeover to grey.</summary>
    private SchedulingLine Line(string mode = ChangeoverMode.Auto) =>
        _f.Stores.Scheduling.AddLine(_site, changeoverMode: mode, plan: p => p with
        {
            PreviousMaterial = new PlanMaterialRow { MaterialId = _white, MaterialCode = "WH" },
            Rates =
            [
                new RunRateRow { MaterialId = _white, UomId = _metre, RatePerHour = 100m, RateSource = RateSource.Calculated },
                new RunRateRow { MaterialId = _grey, UomId = _metre, RatePerHour = 100m, RateSource = RateSource.Calculated },
            ],
            StandardChangeovers = [new StandardChangeoverRow { MaterialId = _grey, StandardChangeover = TimeSpan.FromMinutes(30) }],
        });

    private async Task<WorkOrder> WorkOrderAsync(Guid material, string status = WorkOrderStatus.Released, Guid? site = null)
    {
        var planned = WorkOrder.CreateLocal(
            Guid.CreateVersion7(), site ?? _site, "WO-" + Guid.NewGuid().ToString("N")[..6], material, 1000m, _metre,
            null, null, null, null, 0);
        var wo = planned with { Status = status };
        await _f.Stores.WorkOrders.InsertAsync(wo, Now, CancellationToken.None);
        return wo;
    }

    private async Task<Guid> CreateProductionAsync(SchedulingLine line, WorkOrder wo, string number, Guid? before = null, decimal qty = 100m) =>
        (await Create().HandleAsync(
            new CreateJobCommand(line.Id, number, JobType.Production, wo.Id, qty, null, false, null, before),
            CancellationToken.None)).Id;

    private Job Job(Guid id) => _f.Stores.Scheduling.FindJob(id)!.Value;

    private long Version(Guid id) => _f.Stores.Scheduling.FindJob(id)!.Version;

    [Fact]
    public async Task Create_appends_a_production_job_with_the_work_order_material_and_unit()
    {
        var line = Line();
        var wo = await WorkOrderAsync(_grey);

        var first = await CreateProductionAsync(line, wo, "J-1");
        var second = await CreateProductionAsync(line, wo, "J-2");

        var job = Job(second);
        Assert.Equal((line.Id, _grey, _metre, wo.Id), (job.EquipmentId, job.MaterialId!.Value, job.UomId!.Value, job.WorkOrderId!.Value));
        Assert.Equal((1m, 2m), (Job(first).SequenceRank, job.SequenceRank));
        Assert.Equal(JobStatus.Planned, job.Status);
        Assert.Contains((second, (string?)null, JobStatus.Planned), _f.Stores.Scheduling.StatusChanges);
        Assert.All(_f.Stores.Events.Appended, e => Assert.Equal(EventTypes.JobCreated, e.Type));
        Assert.Equal(2, _f.Stores.UnitOfWork.Committed);
    }

    [Fact]
    public async Task Create_before_a_job_takes_the_midpoint()
    {
        var line = Line();
        var wo = await WorkOrderAsync(_grey);
        var first = await CreateProductionAsync(line, wo, "J-1");
        var second = await CreateProductionAsync(line, wo, "J-2");

        var between = await CreateProductionAsync(line, wo, "J-3", before: second);

        Assert.Equal([first, between, second], _f.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
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
    public async Task Create_refuses_unschedulable_lines_closed_orders_other_sites_and_unknown_neighbours()
    {
        var line = Line();
        var unschedulable = _f.Stores.Scheduling.AddLine(_site, schedulable: false);
        var released = await WorkOrderAsync(_grey);
        var closed = await WorkOrderAsync(_grey, WorkOrderStatus.Closed);
        var elsewhere = await WorkOrderAsync(_grey, site: Guid.CreateVersion7());

        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(unschedulable, released, "J-1"));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, closed, "J-2"));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, elsewhere, "J-3"));
        await Assert.ThrowsAsync<DomainRuleException>(() => CreateProductionAsync(line, released, "J-4", before: Guid.CreateVersion7()));
        Assert.Empty(_f.Stores.Events.Appended);
    }

    [Fact]
    public async Task Sequence_moves_a_job_before_another_on_the_same_line()
    {
        var line = Line();
        var wo = await WorkOrderAsync(_grey);
        var a = await CreateProductionAsync(line, wo, "J-1");
        var b = await CreateProductionAsync(line, wo, "J-2");
        var c = await CreateProductionAsync(line, wo, "J-3");

        var result = await Sequence().HandleAsync(new SequenceJobCommand(c, Version(c), line.Id, a), CancellationToken.None);

        Assert.Equal([c, a, b], _f.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal(Version(c), result.Version);
        Assert.Equal(EventTypes.JobSequenced, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Sequence_moves_a_job_to_the_end_of_another_line()
    {
        var from = Line();
        var to = Line();
        var wo = await WorkOrderAsync(_grey);
        var job = await CreateProductionAsync(from, wo, "J-1");
        var existing = await CreateProductionAsync(to, wo, "J-2");

        await Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), to.Id, null), CancellationToken.None);

        Assert.Empty(_f.Stores.Scheduling.JobsOn(from.Id));
        Assert.Equal([existing, job], _f.Stores.Scheduling.JobsOn(to.Id).Select(j => j.Id));
    }

    [Fact]
    public async Task Sequence_renumbers_the_line_when_ranks_run_out()
    {
        var line = Line();
        var wo = await WorkOrderAsync(_grey);
        var a = await CreateProductionAsync(line, wo, "J-1");
        var b = await CreateProductionAsync(line, wo, "J-2");
        var c = await CreateProductionAsync(line, wo, "J-3");
        _f.Stores.Scheduling.AddJob(Job(a) with { SequenceRank = 1.0000000001m }, Version(a));
        _f.Stores.Scheduling.AddJob(Job(b) with { SequenceRank = 1.0000000002m }, Version(b));

        await Sequence().HandleAsync(new SequenceJobCommand(c, Version(c), line.Id, b), CancellationToken.None);

        Assert.Equal([a, c, b], _f.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal((1m, 1.5m, 2m), (Job(a).SequenceRank, Job(c).SequenceRank, Job(b).SequenceRank));
    }

    [Fact]
    public async Task Sequence_refuses_locked_jobs_stale_versions_and_other_sites()
    {
        var line = Line();
        var otherSite = _f.Stores.Scheduling.AddLine(Guid.CreateVersion7());
        var wo = await WorkOrderAsync(_grey);
        var job = await CreateProductionAsync(line, wo, "J-1");

        await Assert.ThrowsAsync<ConflictException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job) - 1, line.Id, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), otherSite.Id, null), CancellationToken.None));

        var pin = new PinJobHandler(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Events, _f.Events);
        await pin.HandleAsync(new PinJobCommand(job, Version(job), true, null), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Sequence().HandleAsync(new SequenceJobCommand(job, Version(job), line.Id, null), CancellationToken.None));
        Assert.Contains("locked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Pin_sets_lock_and_earliest_start_with_before_and_after()
    {
        var line = Line();
        var wo = await WorkOrderAsync(_grey);
        var job = await CreateProductionAsync(line, wo, "J-1");
        var pin = new PinJobHandler(_f.Stores.UnitOfWork, _f.Stores.Scheduling, _f.Stores.Events, _f.Events);

        await pin.HandleAsync(new PinJobCommand(job, Version(job), true, Now.AddHours(6)), CancellationToken.None);

        Assert.Equal((true, (DateTimeOffset?)Now.AddHours(6)), (Job(job).IsLocked, Job(job).EarliestStart));
        Assert.Equal(EventTypes.JobPinned, _f.Stores.Events.Appended[^1].Type);
    }

    [Fact]
    public async Task Dispatch_in_auto_mode_turns_the_placeholder_into_a_dispatched_changeover_job()
    {
        var line = Line();
        var wo = await WorkOrderAsync(_grey);
        var job = await CreateProductionAsync(line, wo, "J-1");

        var result = await Dispatch().HandleAsync(new DispatchJobCommand(job, Version(job)), CancellationToken.None);

        var changeover = Job(result.ChangeoverJobId!.Value);
        Assert.Equal((JobType.Changeover, "J-1-CO", JobStatus.Dispatched), (changeover.JobType, changeover.JobNumber, changeover.Status));
        Assert.Null(changeover.PlannedDuration);
        Assert.Equal([changeover.Id, job], _f.Stores.Scheduling.JobsOn(line.Id).Select(j => j.Id));
        Assert.Equal((JobStatus.Dispatched, (DateTimeOffset?)Now), (Job(job).Status, Job(job).DispatchedAt));
        Assert.Equal(Version(job), result.Version);
        Assert.Equal(
            [EventTypes.JobCreated, EventTypes.JobCreated, EventTypes.JobDispatched],
            _f.Stores.Events.Appended.Select(e => e.Type));
        Assert.Contains((job, (string?)JobStatus.Planned, JobStatus.Dispatched), _f.Stores.Scheduling.StatusChanges);

        // The real changeover job now suppresses the placeholder and is sized from the matrix.
        var timeline = await new GetLineTimelineHandler(_f.Stores.Scheduling, _f.Time)
            .HandleAsync(new GetLineTimelineQuery(line.Id), CancellationToken.None);
        Assert.Equal([(SegmentKind.Job, changeover.Id), (SegmentKind.Job, job)], timeline.Segments.Select(s => (s.Kind, s.JobId)));
        Assert.Equal(TimeSpan.FromMinutes(30), timeline.Segments[0].Work);
    }

    [Fact]
    public async Task Dispatch_creates_no_changeover_in_scheduler_mode_or_without_a_material_change()
    {
        var scheduler = Line(ChangeoverMode.Scheduler);
        var auto = Line();
        var grey = await WorkOrderAsync(_grey);
        var white = await WorkOrderAsync(_white);
        var a = await CreateProductionAsync(scheduler, grey, "J-1");
        var b = await CreateProductionAsync(auto, white, "J-2");

        Assert.Null((await Dispatch().HandleAsync(new DispatchJobCommand(a, Version(a)), CancellationToken.None)).ChangeoverJobId);
        Assert.Null((await Dispatch().HandleAsync(new DispatchJobCommand(b, Version(b)), CancellationToken.None)).ChangeoverJobId);
    }

    [Fact]
    public async Task Dispatch_needs_a_released_work_order_and_a_current_version()
    {
        var line = Line();
        var planned = await WorkOrderAsync(_grey, WorkOrderStatus.Planned);
        var job = await CreateProductionAsync(line, planned, "J-1");

        await Assert.ThrowsAsync<ConflictException>(() =>
            Dispatch().HandleAsync(new DispatchJobCommand(job, Version(job) + 1), CancellationToken.None));
        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Dispatch().HandleAsync(new DispatchJobCommand(job, Version(job)), CancellationToken.None));
        Assert.Contains("release it", ex.Message, StringComparison.Ordinal);
        Assert.Equal(JobStatus.Planned, Job(job).Status);
    }

    [Fact]
    public async Task Publish_baselines_jobs_that_have_not_started_and_schedules_planned_ones()
    {
        var line = Line();
        var white = await WorkOrderAsync(_white);
        var running = await CreateProductionAsync(line, white, "J-0");
        var next = await CreateProductionAsync(line, white, "J-1");
        var later = await CreateProductionAsync(line, white, "J-2", qty: 50m);
        _f.Stores.Scheduling.AddJob(Job(running) with { Status = JobStatus.Running }, Version(running));

        var result = await Publish().HandleAsync(new PublishScheduleCommand(line.Id), CancellationToken.None);

        Assert.Equal(line.Id, result.LineId);
        Assert.Equal([next, later], result.Jobs.Select(j => j.JobId));
        Assert.Equal((Now.AddHours(1), Now.AddHours(2)), (Job(next).PlannedStart!.Value, Job(next).PlannedEnd!.Value));
        Assert.Equal((Now.AddHours(2), Now.AddHours(2.5)), (Job(later).PlannedStart!.Value, Job(later).PlannedEnd!.Value));
        Assert.All([next, later], id => Assert.Equal(JobStatus.Scheduled, Job(id).Status));
        Assert.Null(Job(running).PlannedStart);
        Assert.Equal(Version(later), result.Jobs[1].Version);

        var evt = _f.Stores.Events.Appended[^1];
        Assert.Equal((EventTypes.SchedulePublished, line.Id, "equipment"), (evt.Type, evt.Subject, evt.SubjectType));
        Assert.Contains((later, (string?)JobStatus.Planned, JobStatus.Scheduled), _f.Stores.Scheduling.StatusChanges);
    }

    [Fact]
    public async Task Publish_refuses_unknown_and_unschedulable_lines()
    {
        var unschedulable = _f.Stores.Scheduling.AddLine(_site, schedulable: false);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Publish().HandleAsync(new PublishScheduleCommand(Guid.CreateVersion7()), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() =>
            Publish().HandleAsync(new PublishScheduleCommand(unschedulable.Id), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() =>
            Publish().HandleAsync(new PublishScheduleCommand(unschedulable.Id, 0), CancellationToken.None));
    }
}
