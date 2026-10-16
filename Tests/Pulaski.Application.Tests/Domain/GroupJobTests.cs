using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Jobs planned to a group such as a department, and the window check (D-022).</summary>
public class GroupJobTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);   // Friday noon
    private static readonly DateTimeOffset Saturday = Now.AddHours(12);
    private static readonly DateTimeOffset Monday = Saturday.AddDays(2);
    private static readonly Guid Department = Guid.CreateVersion7();
    private static readonly Guid Line1 = Guid.CreateVersion7();
    private static readonly Guid Metre = Guid.CreateVersion7();
    private static readonly Guid White = Guid.CreateVersion7();

    private static Job WeekendJob(string status = JobStatus.Planned)
    {
        var job = Job.CreateForGroup(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "WO-1-01", Department,
            White, 10_000m, Metre, 1m, Saturday, Monday);
        return job with { Status = status };
    }

    [Fact]
    public void A_group_job_waits_on_the_group_with_its_window()
    {
        var job = WeekendJob();

        Assert.True(job.IsOnGroup);
        Assert.Equal((Department, (Guid?)Department, (DateTimeOffset?)Saturday, (DateTimeOffset?)Monday),
            (job.EquipmentId, job.GroupEquipmentId, job.EarliestStart, job.LatestEnd));
    }

    [Fact]
    public void A_group_job_needs_a_window_that_ends_after_it_starts()
    {
        Assert.Throws<DomainRuleException>(() => Job.CreateForGroup(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "J", Department, White, 1m, Metre, 1m, Saturday, null));
        Assert.Throws<DomainRuleException>(() => Job.CreateForGroup(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "J", Department, White, 1m, Metre, 1m, Monday, Saturday));
        Assert.Throws<DomainRuleException>(() => WeekendJob().Pin(false, Saturday, null));
    }

    [Theory]
    [InlineData(JobStatus.Planned)]
    [InlineData(JobStatus.Dispatched)]
    [InlineData(JobStatus.Firmed)]
    public void The_floor_assigns_it_to_a_line_and_can_send_it_back_whatever_the_plan_status(string status)
    {
        var onLine = WeekendJob(status).Assign(Line1, targetIsGroup: false, 5m);

        Assert.Equal((Line1, (Guid?)Department, false), (onLine.EquipmentId, onLine.GroupEquipmentId, onLine.IsOnGroup));
        Assert.True(onLine.Assign(Department, targetIsGroup: true, 1m).IsOnGroup);
    }

    [Fact]
    public void Locked_started_or_line_planned_jobs_are_not_assigned()
    {
        Assert.Throws<DomainRuleException>(() => (WeekendJob() with { IsLocked = true }).Assign(Line1, false, 1m));
        Assert.Throws<DomainRuleException>(() => WeekendJob(JobStatus.Running).Assign(Line1, false, 1m));
        Assert.Throws<DomainRuleException>(() => WeekendJob().Assign(Guid.CreateVersion7(), targetIsGroup: true, 1m));
        var linePlanned = Job.CreateProduction(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "J", Line1, White, 1m, Metre, 1m, null);
        Assert.Throws<DomainRuleException>(() => linePlanned.Assign(Line1, false, 1m));
    }

    [Fact]
    public void A_job_planned_to_a_group_is_not_moved_by_sequencing() =>
        Assert.Throws<DomainRuleException>(() => WeekendJob().Assign(Line1, false, 1m).MoveTo(Line1, 2m));

    private static PlannedJob Planned(decimal qty, DateTimeOffset? start, DateTimeOffset end, DateTimeOffset? due = null) =>
        new(Guid.CreateVersion7(), JobType.Production, JobStatus.Planned, new MaterialRef(White, null), qty, Metre, 0m,
            null, start, null, due, LatestEnd: end);

    private static Runner Runner(string code, decimal? rate) =>
        new(Guid.CreateVersion7(), code, rate is { } r ? new RunRate(White, Metre, r, RateSource.Calculated) : null);

    [Fact]
    public void Each_line_gets_an_estimate_and_the_fastest_is_picked_for_the_window_check()
    {
        var window = GroupWindows.Evaluate(
            Planned(10_000m, Saturday, Monday), [Runner("EXT-02", 500m), Runner("EXT-01", 1000m)], WorkCalendar.AlwaysOpen, Now);

        Assert.Equal((Saturday, Monday), (window.Start, window.End));
        Assert.Equal(("EXT-01", (TimeSpan?)TimeSpan.FromHours(10)), (window.Fastest!.LineCode, window.Fastest.Work));
        Assert.All(window.Runners, r => Assert.True(r.FitsWindow));
        Assert.Empty(window.Warnings);
    }

    [Fact]
    public void A_window_too_short_for_even_the_fastest_line_is_flagged()
    {
        var window = GroupWindows.Evaluate(
            Planned(100_000m, Saturday, Monday), [Runner("EXT-01", 1000m), Runner("EXT-02", 500m)], WorkCalendar.AlwaysOpen, Now);

        var warning = Assert.Single(window.Warnings);
        Assert.Equal(ScheduleWarningCode.WindowTooShort, warning.Code);
        Assert.Contains("EXT-01", warning.Message, StringComparison.Ordinal);
        Assert.All(window.Runners, r => Assert.False(r.FitsWindow));
    }

    [Fact]
    public void No_capable_line_no_rate_and_a_closed_window_are_flagged()
    {
        Assert.Equal(ScheduleWarningCode.NoRunner,
            Assert.Single(GroupWindows.Evaluate(Planned(1m, Saturday, Monday), [], WorkCalendar.AlwaysOpen, Now).Warnings).Code);
        Assert.Equal(ScheduleWarningCode.NoRunStandard,
            Assert.Single(GroupWindows.Evaluate(Planned(1m, Saturday, Monday), [Runner("EXT-01", null)], WorkCalendar.AlwaysOpen, Now).Warnings).Code);
        Assert.Equal(ScheduleWarningCode.WindowPassed,
            Assert.Single(GroupWindows.Evaluate(Planned(1m, null, Now.AddHours(-1)), [Runner("EXT-01", 10m)], WorkCalendar.AlwaysOpen, Now).Warnings).Code);
    }

    [Fact]
    public void Once_on_a_line_a_job_that_runs_past_its_finish_by_is_flagged()
    {
        var job = new PlannedJob(Guid.CreateVersion7(), JobType.Production, JobStatus.Planned, new MaterialRef(White, null),
            2000m, Metre, 0m, null, null, null, null, LatestEnd: Now.AddHours(1));
        var timeline = TimelineProjector.Project(new LinePlan(
            Line1, Now, ChangeoverMode.Auto, new MaterialRef(White, null), [job],
            [new RunRate(White, Metre, 1000m, RateSource.Calculated)], new ChangeoverMatrix(Line1, []), WorkCalendar.AlwaysOpen));

        Assert.Equal(ScheduleWarningCode.MissedFinishBy, Assert.Single(timeline.Segments[0].Warnings).Code);
    }
}
