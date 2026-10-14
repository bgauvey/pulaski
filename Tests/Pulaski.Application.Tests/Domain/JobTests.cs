using Pulaski.Domain;
using Pulaski.Domain.Scheduling;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Domain;

public class JobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);

    private static Job Production(string status = JobStatus.Planned, bool locked = false)
    {
        var job = Job.CreateProduction(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "J-1", Guid.CreateVersion7(),
            Guid.CreateVersion7(), 100m, Guid.CreateVersion7(), 1m, null);
        return job with { Status = status, IsLocked = locked };
    }

    [Fact]
    public void Production_job_starts_planned_and_unlocked()
    {
        var job = Production();

        Assert.Equal((JobType.Production, JobStatus.Planned, false), (job.JobType, job.Status, job.IsLocked));
    }

    [Fact]
    public void Production_job_needs_a_positive_quantity() =>
        Assert.Throws<DomainRuleException>(() => Job.CreateProduction(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "J-1", Guid.CreateVersion7(),
            Guid.CreateVersion7(), 0m, Guid.CreateVersion7(), 1m, null));

    [Theory]
    [InlineData(JobType.Production)]
    [InlineData("party")]
    public void Activity_must_be_a_non_production_type(string jobType) =>
        Assert.Throws<DomainRuleException>(() => Job.CreateActivity(
            Guid.CreateVersion7(), Guid.CreateVersion7(), jobType, "C-1", Guid.CreateVersion7(), null, false, 1m, null));

    [Fact]
    public void Activity_duration_must_be_positive() =>
        Assert.Throws<DomainRuleException>(() => Job.CreateActivity(
            Guid.CreateVersion7(), Guid.CreateVersion7(), JobType.Cleaning, "C-1", Guid.CreateVersion7(), TimeSpan.Zero, false, 1m, null));

    [Fact]
    public void Planned_job_moves_anywhere()
    {
        var line = Guid.CreateVersion7();

        var moved = Production().MoveTo(line, 2.5m);

        Assert.Equal((line, 2.5m), (moved.EquipmentId, moved.SequenceRank));
    }

    [Fact]
    public void Locked_job_cannot_move()
    {
        var ex = Assert.Throws<DomainRuleException>(() => Production(locked: true).MoveTo(Guid.CreateVersion7(), 1m));
        Assert.Contains("locked", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Firmed_job_changes_machine_but_not_its_place_in_time()
    {
        var firmed = Production(JobStatus.Firmed);

        Assert.Equal(2m, firmed.MoveTo(Guid.CreateVersion7(), 2m).SequenceRank);
        var ex = Assert.Throws<DomainRuleException>(() => firmed.MoveTo(firmed.EquipmentId, 2m));
        Assert.Contains("not move in time", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Dispatched_job_must_be_undispatched_before_it_moves()
    {
        var ex = Assert.Throws<DomainRuleException>(() => Production(JobStatus.Dispatched).MoveTo(Guid.CreateVersion7(), 1m));
        Assert.Contains("un-dispatch", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Completed)]
    public void Started_or_finished_jobs_do_not_move(string status) =>
        Assert.Throws<DomainRuleException>(() => Production(status).MoveTo(Guid.CreateVersion7(), 1m));

    [Fact]
    public void Pin_sets_lock_and_earliest_start()
    {
        var pinned = Production(JobStatus.Running).Pin(true, Now);

        Assert.Equal((true, (DateTimeOffset?)Now), (pinned.IsLocked, pinned.EarliestStart));
    }

    [Fact]
    public void Finished_job_cannot_be_pinned() =>
        Assert.Throws<DomainRuleException>(() => Production(JobStatus.Closed).Pin(true, null));

    [Fact]
    public void Dispatch_publishes_the_baseline()
    {
        var dispatched = Production().Dispatch(Now, Now.AddHours(1), Now.AddHours(3));

        Assert.Equal(JobStatus.Dispatched, dispatched.Status);
        Assert.Equal((Now, Now.AddHours(1), Now.AddHours(3)), (dispatched.DispatchedAt!.Value, dispatched.PlannedStart!.Value, dispatched.PlannedEnd!.Value));
    }

    [Fact]
    public void Zero_length_projection_publishes_no_end() =>
        Assert.Null(Production().Dispatch(Now, Now, Now).PlannedEnd);

    [Fact]
    public void Only_planned_jobs_are_dispatched() =>
        Assert.Throws<DomainRuleException>(() => Production(JobStatus.Dispatched).Dispatch(Now, Now, Now.AddHours(1)));

    [Fact]
    public void Undispatch_returns_to_planned_and_clears_the_baseline()
    {
        var job = Production().Dispatch(Now, Now, Now.AddHours(1)).Undispatch();

        Assert.Equal(JobStatus.Planned, job.Status);
        Assert.Null(job.PlannedStart);
        Assert.Null(job.DispatchedAt);
    }

    [Fact]
    public void Firm_keeps_the_baseline_and_records_when()
    {
        var job = Production().Dispatch(Now, Now.AddHours(1), Now.AddHours(2)).Firm(Now.AddMinutes(5));

        Assert.Equal((JobStatus.Firmed, Now.AddHours(1), Now.AddMinutes(5)), (job.Status, job.PlannedStart!.Value, job.FirmedAt!.Value));
        Assert.Throws<DomainRuleException>(() => job.Undispatch());
    }

    [Fact]
    public void Only_dispatched_jobs_are_firmed() =>
        Assert.Throws<DomainRuleException>(() => Production().Firm(Now));

    [Fact]
    public void Cancel_works_until_the_job_is_finished()
    {
        Assert.Equal(JobStatus.Cancelled, Production(JobStatus.Running).Cancel().Status);
        Assert.Throws<DomainRuleException>(() => Production(JobStatus.Completed).Cancel());
    }

    [Fact]
    public void Replacement_takes_the_remaining_quantity_and_slot_and_follows_the_order()
    {
        var running = Production(JobStatus.Running, locked: true);
        var id = Guid.CreateVersion7();

        var replacement = running.Replacement(id, 40m, WorkOrderStatus.InProgress, Now);

        Assert.Equal((id, "J-1-R", 40m, JobStatus.Firmed), (replacement.Id, replacement.JobNumber, replacement.PlannedQty!.Value, replacement.Status));
        Assert.Equal((running.EquipmentId, running.SequenceRank, (Guid?)running.Id), (replacement.EquipmentId, replacement.SequenceRank, replacement.ReplacesJobId));
        Assert.Equal((Now, Now), (replacement.PlannedStart!.Value, replacement.FirmedAt!.Value));
        Assert.False(replacement.IsLocked);
        Assert.Equal(JobStatus.Planned, running.Replacement(id, 40m, WorkOrderStatus.Planned, Now).Status);
    }

    [Fact]
    public void Replacement_needs_production_and_something_left_to_make()
    {
        Assert.Throws<DomainRuleException>(() => Production(JobStatus.Running).Replacement(Guid.CreateVersion7(), 0m, WorkOrderStatus.Firmed, Now));
        var cleaning = Job.CreateActivity(
            Guid.CreateVersion7(), Guid.CreateVersion7(), JobType.Cleaning, "C-1", Guid.CreateVersion7(), TimeSpan.FromHours(1), false, 1m, null);
        Assert.Throws<DomainRuleException>(() => cleaning.Replacement(Guid.CreateVersion7(), 1m, WorkOrderStatus.Firmed, Now));
    }
}
