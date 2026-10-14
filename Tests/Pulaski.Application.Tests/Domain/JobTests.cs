using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

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
    public void Move_changes_line_and_rank()
    {
        var line = Guid.CreateVersion7();

        var moved = Production(JobStatus.Scheduled).MoveTo(line, 2.5m);

        Assert.Equal((line, 2.5m), (moved.EquipmentId, moved.SequenceRank));
    }

    [Fact]
    public void Locked_job_cannot_move()
    {
        var ex = Assert.Throws<DomainRuleException>(() => Production(locked: true).MoveTo(Guid.CreateVersion7(), 1m));
        Assert.Contains("locked", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(JobStatus.Dispatched)]
    [InlineData(JobStatus.Running)]
    [InlineData(JobStatus.Completed)]
    public void Only_planned_or_scheduled_jobs_move(string status) =>
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
    public void Dispatch_records_status_and_time()
    {
        var dispatched = Production(JobStatus.Scheduled).Dispatch(Now);

        Assert.Equal((JobStatus.Dispatched, (DateTimeOffset?)Now), (dispatched.Status, dispatched.DispatchedAt));
    }

    [Fact]
    public void Dispatched_job_cannot_be_dispatched_again() =>
        Assert.Throws<DomainRuleException>(() => Production(JobStatus.Dispatched).Dispatch(Now));

    [Fact]
    public void Publish_sets_baseline_and_schedules_a_planned_job()
    {
        var published = Production().Publish(Now, Now.AddHours(2));

        Assert.Equal((Now, Now.AddHours(2), JobStatus.Scheduled), (published.PlannedStart!.Value, published.PlannedEnd!.Value, published.Status));
    }

    [Fact]
    public void Publish_keeps_a_dispatched_job_dispatched() =>
        Assert.Equal(JobStatus.Dispatched, Production(JobStatus.Dispatched).Publish(Now, Now.AddHours(1)).Status);

    [Fact]
    public void Zero_length_projection_publishes_no_end()
    {
        var published = Production().Publish(Now, Now);

        Assert.Equal(Now, published.PlannedStart);
        Assert.Null(published.PlannedEnd);
    }

    [Fact]
    public void Started_job_cannot_be_republished() =>
        Assert.Throws<DomainRuleException>(() => Production(JobStatus.Running).Publish(Now, Now.AddHours(1)));
}
