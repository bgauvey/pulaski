using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Execution;
using Pulaski.Application.Inventory;
using Pulaski.Application.Scheduling;
using Pulaski.Application.WorkOrders;
using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Execution;

/// <summary>Starting, pausing and completing jobs, and reporting production (D-035).</summary>
public class ExecutionHandlerTests
{
    private readonly HandlerFixture _f = new();

    private InMemoryScheduling Scheduling => _f.Stores.Scheduling;

    private InMemoryExecution Execution => _f.Stores.Execution;

    private StartJobHandler Start() => new(_f.Stores.UnitOfWork, Scheduling, _f.Stores.WorkOrders, Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time);

    private PauseJobHandler Pause() => new(_f.Stores.UnitOfWork, Scheduling, _f.Stores.WorkOrders, Execution, _f.Stores.Inventory, _f.Stores.Events, _f.Events, _f.Time);

    private CompleteJobHandler Complete() => new(_f.Stores.UnitOfWork, Scheduling, _f.Stores.WorkOrders, Execution, _f.Stores.Events, _f.Events, _f.Time);

    private ReopenJobHandler Reopen() => new(_f.Stores.UnitOfWork, Scheduling, _f.Stores.WorkOrders, Execution, _f.Stores.Events, _f.Events);

    private long Version(Guid jobId) => Scheduling.FindJob(jobId)!.Version;

    /// <summary>A dispatched order with one dispatched job on a line that can run its material.</summary>
    private (WorkOrder Order, SchedulingLine Line, Job Job) Dispatched(string status = WorkOrderStatus.Dispatched, string jobStatus = JobStatus.Dispatched)
    {
        var order = _f.Stores.WorkOrders.Add(status);
        var line = Scheduling.AddLine(order.SiteId);
        Scheduling.AddCapability(line.Id, order.MaterialId);
        var job = Scheduling.AddJob(Job.CreateProduction(
            Guid.CreateVersion7(), order.SiteId, order.Id, "J-" + Guid.NewGuid().ToString("N")[..4], line.Id, order.MaterialId, 100m, order.UomId, 1m, null)
            with
        { Status = jobStatus });
        return (order, line, job);
    }

    [Fact]
    public async Task Starting_firms_a_dispatched_order_opens_a_run_and_puts_the_order_in_progress()
    {
        var (order, line, job) = Dispatched();

        var started = await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None);

        Assert.Equal(JobStatus.Running, Scheduling.FindJob(job.Id)!.Value.Status);
        var run = Assert.Single(Execution.RunsOf(job.Id));
        Assert.Equal((started.RunId, 1, line.Id, HandlerFixture.Now), (run.Id, run.RunNo, run.EquipmentId, run.StartedAt));
        Assert.Equal(WorkOrderStatus.InProgress, _f.Stores.WorkOrders.Find(order.Id)!.Value.Status);
        Assert.Contains($"Work order {order.OrderNumber} is now in progress.", started.Notes);
        Assert.Equal(
            [EventTypes.WorkOrderFirmed, EventTypes.JobStarted, EventTypes.WorkOrderStatusChanged],
            _f.Stores.Events.Appended.Select(e => e.Type));
        Assert.Contains((job.Id, JobStatus.Firmed, JobStatus.Running), Scheduling.StatusChanges);
    }

    [Fact]
    public async Task A_job_starts_only_from_the_floor_on_a_free_line_once_its_links_are_met()
    {
        var planned = Dispatched(WorkOrderStatus.Planned, JobStatus.Planned);
        var notOnFloor = await Assert.ThrowsAsync<DomainRuleException>(() =>
            Start().HandleAsync(new StartJobCommand(planned.Job.Id, Version(planned.Job.Id)), CancellationToken.None));
        Assert.Contains("dispatch it before starting", notOnFloor.Message, StringComparison.Ordinal);

        var (_, line, job) = Dispatched(WorkOrderStatus.Firmed, JobStatus.Firmed);
        Execution.OpenRun(Guid.CreateVersion7(), line.Id, HandlerFixture.Now.AddHours(-1));
        var busy = await Assert.ThrowsAsync<DomainRuleException>(() => Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None));
        Assert.Contains("pause or complete it first", busy.Message, StringComparison.Ordinal);

        var waiting = Dispatched(WorkOrderStatus.Firmed, JobStatus.Firmed);
        var pack = Scheduling.AddJob(waiting.Job with { ProcessOperationId = Guid.CreateVersion7() }, Version(waiting.Job.Id));
        var extrude = Guid.CreateVersion7();
        Execution.Upstream[pack.Id] = (
            [new UpstreamLink(extrude, "operation 10 Extrude", JobReadiness.AfterEnd, TimeSpan.Zero)],
            [new UpstreamJob(extrude, "J-EXT", JobStatus.Running, HandlerFixture.Now.AddHours(-1), null)]);
        var notReady = await Assert.ThrowsAsync<DomainRuleException>(() => Start().HandleAsync(new StartJobCommand(pack.Id, Version(pack.Id)), CancellationToken.None));
        Assert.Contains("Waiting for operation 10 Extrude to end (J-EXT)", notReady.Message, StringComparison.Ordinal);

        await Assert.ThrowsAsync<ConflictException>(() => Start().HandleAsync(new StartJobCommand(pack.Id, Version(pack.Id) - 1), CancellationToken.None));
    }

    [Fact]
    public async Task A_new_material_on_an_automatic_line_starts_a_changeover_first_unless_it_is_skipped()
    {
        var (_, line, job) = Dispatched(WorkOrderStatus.Firmed, JobStatus.Firmed);
        Execution.LastRunJob[line.Id] = (JobType.Production, Guid.CreateVersion7(), "PRF-WHITE");

        var changeover = await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None);

        var co = Scheduling.FindJob(changeover.JobId)!.Value;
        Assert.Equal((JobType.Changeover, JobStatus.Running, job.JobNumber + "-CO"), (co.JobType, co.Status, co.JobNumber));
        Assert.Equal(JobStatus.Firmed, Scheduling.FindJob(job.Id)!.Value.Status);
        Assert.Contains("changes from PRF-WHITE", Assert.Single(changeover.Notes), StringComparison.Ordinal);

        await Complete().HandleAsync(new CompleteJobCommand(co.Id, Version(co.Id)), CancellationToken.None);
        var skipped = await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id), SkipChangeover: true), CancellationToken.None);
        Assert.Equal(job.Id, skipped.JobId);
    }

    [Fact]
    public async Task A_department_job_starts_on_the_line_the_floor_picks()
    {
        var order = _f.Stores.WorkOrders.Add(WorkOrderStatus.Firmed);
        var line = Scheduling.AddLine(order.SiteId);
        var other = Scheduling.AddLine(order.SiteId);
        var department = Scheduling.AddGroup(order.SiteId, line);
        Scheduling.AddCapability(line.Id, order.MaterialId);
        Scheduling.AddCapability(other.Id, order.MaterialId);
        var job = Scheduling.AddJob(Job.CreateForGroup(
            Guid.CreateVersion7(), order.SiteId, order.Id, "J-D", department.Id, order.MaterialId, 100m, order.UomId, 1m, null, HandlerFixture.Now.AddDays(2))
            with
        { Status = JobStatus.Firmed });

        await Assert.ThrowsAsync<ValidationException>(() => Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id), other.Id), CancellationToken.None));
        await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id), line.Id), CancellationToken.None);

        Assert.Equal((line.Id, JobStatus.Running), (Scheduling.FindJob(job.Id)!.Value.EquipmentId, Scheduling.FindJob(job.Id)!.Value.Status));
    }

    [Fact]
    public async Task Pausing_ends_the_run_and_completing_the_last_job_completes_the_order()
    {
        var (order, _, job) = Dispatched();
        var reason = _f.Stores.Inventory.AddReason(ReasonKinds.RunEnd, "SHIFT", siteId: order.SiteId);
        await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None);
        _f.Time.Advance(TimeSpan.FromHours(2));

        var paused = await Pause().HandleAsync(new PauseJobCommand(job.Id, Version(job.Id), reason.Id), CancellationToken.None);
        Assert.Contains("ended after 2 h 00 min", Assert.Single(paused.Notes), StringComparison.Ordinal);
        Assert.Equal((false, reason.Id), (Execution.RunsOf(job.Id)[0].IsOpen, Execution.RunsOf(job.Id)[0].EndReasonCodeId));

        await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None);
        Assert.Equal(2, Execution.RunsOf(job.Id).Count);
        var done = await Complete().HandleAsync(new CompleteJobCommand(job.Id, Version(job.Id)), CancellationToken.None);

        Assert.Equal((JobStatus.Completed, HandlerFixture.Now.AddHours(2)), (Scheduling.FindJob(job.Id)!.Value.Status, Scheduling.FindJob(job.Id)!.Value.CompletedAt));
        Assert.All(Execution.RunsOf(job.Id), r => Assert.False(r.IsOpen));
        Assert.Equal(WorkOrderStatus.Completed, _f.Stores.WorkOrders.Find(order.Id)!.Value.Status);
        Assert.Contains(done.Notes, n => n.Contains("Nothing was reported", StringComparison.Ordinal));
        Assert.Contains(done.Notes, n => n.Contains("is complete", StringComparison.Ordinal));

        var reopened = await Reopen().HandleAsync(new ReopenJobCommand(job.Id, Version(job.Id)), CancellationToken.None);
        Assert.Equal(JobStatus.Paused, Scheduling.FindJob(job.Id)!.Value.Status);
        Assert.Equal(WorkOrderStatus.InProgress, _f.Stores.WorkOrders.Find(order.Id)!.Value.Status);
        Assert.Contains($"Work order {order.OrderNumber} is in progress again.", reopened.Notes);
    }

    [Fact]
    public async Task Completing_an_upstream_job_says_which_downstream_jobs_can_start()
    {
        var (_, _, job) = Dispatched(WorkOrderStatus.Firmed, JobStatus.Firmed);
        await Start().HandleAsync(new StartJobCommand(job.Id, Version(job.Id)), CancellationToken.None);
        var pack = Guid.CreateVersion7();
        Execution.Downstream[job.Id] = [new DownstreamJob(pack, "J-PACK", "PACK1", JobStatus.Firmed)];

        var done = await Complete().HandleAsync(new CompleteJobCommand(job.Id, Version(job.Id)), CancellationToken.None);

        Assert.Contains("J-PACK on PACK1 can start now.", done.Notes);
    }

    [Fact]
    public async Task The_order_follows_its_jobs_once_execution_starts()
    {
        var handler = new ChangeWorkOrderStatusHandler(_f.Stores.UnitOfWork, _f.Stores.WorkOrders, Execution, _f.Stores.Events, _f.Events);
        var order = _f.Stores.WorkOrders.Add(WorkOrderStatus.Firmed);

        var ex = await Assert.ThrowsAsync<DomainRuleException>(() =>
            handler.HandleAsync(new ChangeWorkOrderStatusCommand(order.Id, 10, WorkOrderStatus.InProgress, null), CancellationToken.None));
        Assert.Contains("follows its jobs", ex.Message, StringComparison.Ordinal);
    }
}
