using Pulaski.Domain.Processes;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Process links in the projection (D-046): a job waits for the jobs it follows on other lines.</summary>
public class LinkedProjectionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Metre = Guid.CreateVersion7();
    private static readonly MaterialRef Profile = new(Guid.CreateVersion7(), null);
    private static readonly Guid Extruder = Guid.CreateVersion7();
    private static readonly Guid Packer = Guid.CreateVersion7();

    // 1000 m an hour everywhere, and no changeovers: one material.
    private static LinePlan Plan(Guid line, params PlannedJob[] jobs) => new(
        line, Now, ChangeoverMode.Auto, Profile, jobs, [new RunRate(Profile.Id, Metre, 1000m, RateSource.Calculated)],
        new ChangeoverMatrix(line, []), WorkCalendar.AlwaysOpen);

    private static PlannedJob Job(decimal metres, string status = JobStatus.Planned, DateTimeOffset? firmed = null, DateTimeOffset? startedAt = null) =>
        new(Guid.CreateVersion7(), JobType.Production, status, Profile, metres, Metre, 0m, startedAt, null, null, null, firmed);

    private static JobLink Link(PlannedJob job, PlannedJob from, string type = DependencyType.AfterEnd, double lagMinutes = 0, Guid? linkId = null) =>
        new(linkId ?? Guid.CreateVersion7(), job.JobId, from.JobId, type, TimeSpan.FromMinutes(lagMinutes), "10 Extrude");

    private static TimelineSegment Segment(IReadOnlyDictionary<Guid, LineTimeline> timelines, PlannedJob job) =>
        timelines.Values.SelectMany(t => t.Segments).Single(s => s.JobId == job.JobId && s.Kind == SegmentKind.Job);

    [Fact]
    public void A_job_waits_for_the_end_of_the_jobs_it_follows_on_another_line_plus_the_lag()
    {
        var extrude = Job(4000);
        var pack = Job(1000);

        var timelines = LinkedProjection.Project([Plan(Extruder, extrude), Plan(Packer, pack)], [Link(pack, extrude, lagMinutes: 30)], new Dictionary<Guid, KnownJobTimes>());

        Assert.Equal((Now.AddHours(4.5), Now.AddHours(5.5)), (Segment(timelines, pack).Start, Segment(timelines, pack).End));
        Assert.Equal(Now.AddHours(4.5), Segment(timelines, pack).LinkReadyAt);
        Assert.Empty(Segment(timelines, pack).Warnings);
    }

    [Fact]
    public void After_start_waits_for_the_first_of_several_jobs_and_after_end_for_the_last()
    {
        // Operation 10 runs on two extruders: one free now, one busy for 2 h first.
        var busy = Job(2000);
        var extrudeA = Job(3000);
        var extrudeB = Job(1000);
        var second = Guid.CreateVersion7();
        var packStart = Job(500);
        var packEnd = Job(500);
        var link = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();

        var timelines = LinkedProjection.Project(
            [Plan(Extruder, extrudeA), Plan(second, busy, extrudeB), Plan(Packer, packStart, packEnd)],
            [
                Link(packStart, extrudeA, DependencyType.AfterStart, linkId: link), Link(packStart, extrudeB, DependencyType.AfterStart, linkId: link),
                Link(packEnd, extrudeA, linkId: other), Link(packEnd, extrudeB, linkId: other),
            ],
            new Dictionary<Guid, KnownJobTimes>());

        Assert.Equal(Now, Segment(timelines, packStart).Start);
        Assert.Equal(Now.AddHours(3), Segment(timelines, packEnd).Start);
    }

    [Fact]
    public void Ended_and_started_jobs_count_with_their_actual_times()
    {
        var extrude = Guid.CreateVersion7();
        var pack = Job(1000);
        var known = new Dictionary<Guid, KnownJobTimes> { [extrude] = new(Now.AddHours(-5), Now.AddHours(2)) };

        var timelines = LinkedProjection.Project(
            [Plan(Packer, pack)], [new JobLink(Guid.CreateVersion7(), pack.JobId, extrude, DependencyType.AfterEnd, TimeSpan.Zero, "10 Extrude")], known);

        Assert.Equal(Now.AddHours(2), Segment(timelines, pack).Start);
    }

    [Fact]
    public void A_firmed_or_running_job_keeps_its_time_and_a_firmed_one_says_the_link_isnt_met()
    {
        var extrude = Job(4000);
        var firmed = Job(1000, JobStatus.Firmed, firmed: Now.AddHours(1));
        var running = Job(1000, JobStatus.Running, startedAt: Now.AddHours(-1));

        var timelines = LinkedProjection.Project(
            [Plan(Extruder, extrude), Plan(Packer, firmed), Plan(Guid.CreateVersion7(), running)],
            [Link(firmed, extrude), Link(running, extrude)],
            new Dictionary<Guid, KnownJobTimes>());

        Assert.Equal(Now.AddHours(1), Segment(timelines, firmed).Start);
        Assert.Contains(Segment(timelines, firmed).Warnings, w => w.Code == ScheduleWarningCode.LinkNotMet);
        Assert.Equal(Now.AddHours(-1), Segment(timelines, running).Start);
        Assert.Empty(Segment(timelines, running).Warnings);
    }

    [Fact]
    public void A_job_sequenced_before_the_work_it_waits_for_is_reported_as_a_cycle()
    {
        // On one line, Pack (20) is sequenced before Extrude (10) and waits for it to end: it can never be placed.
        var pack = Job(1000);
        var extrude = Job(1000);

        var timelines = LinkedProjection.Project([Plan(Extruder, pack, extrude)], [Link(pack, extrude)], new Dictionary<Guid, KnownJobTimes>());

        Assert.Contains(Segment(timelines, pack).Warnings, w => w.Code == ScheduleWarningCode.LinkCycle);
    }

    [Fact]
    public void Downstream_work_pushed_later_pushes_what_comes_after_it_on_its_line()
    {
        var extrude = Job(3000);
        var pack = Job(1000);
        var next = Job(1000);

        var timelines = LinkedProjection.Project([Plan(Extruder, extrude), Plan(Packer, pack, next)], [Link(pack, extrude)], new Dictionary<Guid, KnownJobTimes>());

        Assert.Equal(Now.AddHours(4), Segment(timelines, next).Start);
    }
}
