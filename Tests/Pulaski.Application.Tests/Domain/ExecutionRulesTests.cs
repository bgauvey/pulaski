using Pulaski.Domain;
using Pulaski.Domain.Execution;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Runs, the rate cap, readiness and the consumption check (D-035).</summary>
public class ExecutionRulesTests
{
    private static readonly DateTimeOffset Eight = new(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_cap_is_elapsed_time_at_the_ideal_rate_plus_tolerance()
    {
        Assert.Equal(110m, RateCap.Cap(100m, 110m, TimeSpan.FromHours(1)));

        RateCap.Ensure(100m, 110m, TimeSpan.FromHours(1), 60m, 50m, "ea", "EXT-01");
        var ex = Assert.Throws<DomainRuleException>(() => RateCap.Ensure(100m, 110m, TimeSpan.FromHours(1), 0m, 1000m, "ea", "EXT-01"));
        Assert.Contains("1,000 ea in 1 h 00 min is more than EXT-01 can make", ex.Message, StringComparison.Ordinal);
        Assert.Contains("At most 110 ea more", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_ends_once_and_adds_up_good_and_scrap()
    {
        var run = JobRun.Open(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1, Eight, null);

        Assert.Equal(TimeSpan.FromMinutes(30), run.Elapsed(Eight.AddMinutes(30)));
        var made = run.Add(90m, 10m);
        Assert.Equal((90m, 10m, 100m), (made.GoodQty, made.ScrapQty, made.TotalQty));
        Assert.Throws<DomainRuleException>(() => run.Add(0m, 0m));
        Assert.Throws<DomainRuleException>(() => run.Add(-1m, 2m));

        var ended = made.End(Eight.AddHours(2), null);
        Assert.Equal((false, TimeSpan.FromHours(2)), (ended.IsOpen, ended.Elapsed(Eight.AddDays(1))));
        Assert.Throws<DomainRuleException>(() => ended.End(Eight.AddHours(3), null));
    }

    [Fact]
    public void A_downstream_job_waits_for_its_links_and_their_lags()
    {
        var extrude = Guid.CreateVersion7();
        var afterStart = new UpstreamLink(extrude, "operation 10 Extrude", JobReadiness.AfterStart, TimeSpan.FromMinutes(30));
        var afterEnd = afterStart with { DependencyType = JobReadiness.AfterEnd, Lag = TimeSpan.Zero };

        var notStarted = new UpstreamJob(extrude, "J-10", "firmed", null, null);
        Assert.Contains("to start, then 30 min", JobReadiness.For([afterStart], [notStarted], Eight).Waiting, StringComparison.Ordinal);

        var started = notStarted with { Status = "running", FirstStartedAt = Eight };
        var waiting = JobReadiness.For([afterStart], [started], Eight.AddMinutes(10));
        Assert.Equal((false, Eight.AddMinutes(30)), (waiting.IsReady, waiting.ReadyAt));
        Assert.True(JobReadiness.For([afterStart], [started], Eight.AddMinutes(30)).IsReady);

        Assert.Contains("to end (J-10)", JobReadiness.For([afterEnd], [started], Eight).Waiting, StringComparison.Ordinal);
        Assert.True(JobReadiness.For([afterEnd], [started with { Status = "completed", CompletedAt = Eight }], Eight).IsReady);
        Assert.True(JobReadiness.For([afterEnd], [started with { Status = "cancelled" }], Eight).IsReady);
        Assert.True(JobReadiness.For([], [], Eight).IsReady);
    }

    [Fact]
    public void Consumption_warns_beyond_the_plan_for_what_was_made()
    {
        Assert.Null(ConsumptionCheck.Warning(1450m, 1000m, 0m, 1500m, 0m, 110m, "RESIN", "kg"));
        Assert.Contains("is more than planned", ConsumptionCheck.Warning(1450m, 1000m, 0m, 1500m, 200m, 110m, "RESIN", "kg"), StringComparison.Ordinal);

        // Making twice the plan allows twice the components.
        Assert.Null(ConsumptionCheck.Warning(1450m, 1000m, 2000m, 3000m, 100m, 110m, "RESIN", "kg"));
        Assert.Null(ConsumptionCheck.Warning(0m, 1000m, 0m, 0m, 9999m, 110m, "RESIN", "kg"));
    }

    [Fact]
    public void A_job_starts_from_the_floor_pauses_completes_and_reopens()
    {
        var job = Job.CreateProduction(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "J-1", Guid.CreateVersion7(), Guid.CreateVersion7(), 100m, Guid.CreateVersion7(), 1m, null);

        Assert.Contains("dispatch its work order", Assert.Throws<DomainRuleException>(job.Start).Message, StringComparison.Ordinal);
        var running = (job with { Status = JobStatus.Firmed }).Start();
        Assert.Throws<DomainRuleException>(running.Start);
        var paused = running.Pause();
        Assert.Equal(JobStatus.Running, paused.Start().Status);

        var done = paused.Complete(Eight);
        Assert.Equal((JobStatus.Completed, Eight), (done.Status, done.CompletedAt));
        Assert.Equal((JobStatus.Paused, (DateTimeOffset?)null), (done.Reopen().Status, done.Reopen().CompletedAt));
        Assert.Equal(JobStatus.Closed, done.Close().Status);
        Assert.Throws<DomainRuleException>(() => job.Complete(Eight));

        var cleaning = Job.CreateActivity(Guid.CreateVersion7(), Guid.CreateVersion7(), JobType.Cleaning, "CLN-1", Guid.CreateVersion7(), TimeSpan.FromHours(1), false, 1m, null);
        Assert.Equal(JobStatus.Running, cleaning.Start().Status);
    }
}
