using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Tests.Domain;

public class TimelineProjectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid Metre = Guid.CreateVersion7();
    private static readonly Guid Kilogram = Guid.CreateVersion7();
    private static readonly MaterialRef White = new(Guid.CreateVersion7(), null);
    private static readonly MaterialRef Black = new(Guid.CreateVersion7(), null);

    // 1000 m/h on both materials; 45 min to go white -> black, 90 min back.
    private static readonly RunRate[] Rates =
    [
        new(White.Id, Metre, 1000m, RateSource.Calculated),
        new(Black.Id, Metre, 1000m, RateSource.Modified),
    ];

    private static readonly ChangeoverMatrix Matrix = new(Line,
    [
        new(Guid.CreateVersion7(), null, White.Id, null, Black.Id, null, TimeSpan.FromMinutes(45)),
        new(Guid.CreateVersion7(), null, Black.Id, null, White.Id, null, TimeSpan.FromMinutes(90)),
    ]);

    private static PlannedJob Production(
        MaterialRef material,
        decimal qty,
        string status = JobStatus.Planned,
        decimal good = 0m,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? earliest = null,
        DateTimeOffset? due = null,
        Guid? uom = null) =>
        new(Guid.CreateVersion7(), JobType.Production, status, material, qty, uom ?? Metre, good, startedAt, earliest, null, due);

    private static PlannedJob NonProduction(string type, TimeSpan? duration, string status = JobStatus.Planned, DateTimeOffset? startedAt = null) =>
        new(Guid.CreateVersion7(), type, status, null, null, null, 0m, startedAt, null, duration, null);

    private static LineTimeline Project(
        IReadOnlyList<PlannedJob> jobs,
        string mode = ChangeoverMode.Auto,
        MaterialRef? previous = null,
        WorkCalendar? calendar = null,
        IReadOnlyList<RunRate>? rates = null) =>
        TimelineProjector.Project(new LinePlan(
            Line, Now, mode, previous, jobs, rates ?? Rates, Matrix, calendar ?? WorkCalendar.AlwaysOpen));

    private static IEnumerable<string> Codes(TimelineSegment segment) => segment.Warnings.Select(w => w.Code);

    [Fact]
    public void Jobs_run_back_to_back_at_the_run_standard_rate()
    {
        var a = Production(White, 2000m);
        var b = Production(White, 500m);

        var timeline = Project([a, b], previous: White);

        Assert.Collection(
            timeline.Segments,
            s => Assert.Equal((a.JobId, Now, Now.AddHours(2)), (s.JobId, s.Start, s.End)),
            s => Assert.Equal((b.JobId, Now.AddHours(2), Now.AddHours(2.5)), (s.JobId, s.Start, s.End)));
        Assert.Equal(RateSource.Calculated, timeline.Segments[0].Rate!.Source);
    }

    [Fact]
    public void Line_without_a_shift_calendar_is_flagged_once()
    {
        var timeline = Project([Production(White, 10m)]);

        Assert.Equal([ScheduleWarningCode.NoShiftCalendar], timeline.Warnings.Select(w => w.Code));
        Assert.Empty(Project([], calendar: WorkCalendar.Create([], [])).Warnings);
    }

    [Fact]
    public void Running_job_goes_first_keeps_its_actual_start_and_finishes_its_remaining_quantity()
    {
        var queued = Production(White, 1000m);
        var running = Production(White, 3000m, JobStatus.Running, good: 2500m, startedAt: Now.AddHours(-3));

        var timeline = Project([queued, running], previous: Black);

        var first = timeline.Segments[0];
        Assert.Equal(running.JobId, first.JobId);
        Assert.Equal(Now.AddHours(-3), first.Start);
        Assert.Equal(Now.AddMinutes(30), first.End);
        Assert.Equal(500m, first.RemainingQty);

        // No changeover into a job already running; none after it either, same material.
        Assert.Equal(2, timeline.Segments.Count);
        Assert.Equal(Now.AddMinutes(30), timeline.Segments[1].Start);
    }

    [Fact]
    public void Overproduced_running_job_ends_now()
    {
        var running = Production(White, 100m, JobStatus.Running, good: 150m, startedAt: Now.AddHours(-1));

        var segment = Project([running]).Segments.Single();

        Assert.Equal((0m, Now), (segment.RemainingQty, segment.End));
    }

    [Fact]
    public void Auto_mode_inserts_a_sized_placeholder_wherever_the_material_changes()
    {
        var w = Production(White, 1000m);
        var b = Production(Black, 1000m);
        var w2 = Production(White, 1000m);

        var segments = Project([w, b, w2], previous: White).Segments;

        Assert.Equal(
            [SegmentKind.Job, SegmentKind.ChangeoverPlaceholder, SegmentKind.Job, SegmentKind.ChangeoverPlaceholder, SegmentKind.Job],
            segments.Select(s => s.Kind));

        var toBlack = segments[1];
        Assert.Equal((b.JobId, Now.AddHours(1), Now.AddHours(1.75)), (toBlack.JobId, toBlack.Start, toBlack.End));
        Assert.Equal(White, toBlack.FromMaterial);
        Assert.Equal(ChangeoverSource.Rule, toBlack.Changeover!.Source);

        Assert.Equal(TimeSpan.FromMinutes(90), segments[3].Work);
        Assert.Equal(Now.AddHours(1.75 + 1 + 1.5 + 1), segments[4].End);
    }

    [Fact]
    public void First_job_changes_over_from_what_the_line_last_ran()
    {
        var segments = Project([Production(White, 1000m)], previous: Black).Segments;

        Assert.Equal(SegmentKind.ChangeoverPlaceholder, segments[0].Kind);
        Assert.Equal(Now.AddMinutes(90), segments[1].Start);
    }

    [Fact]
    public void Unknown_previous_material_means_no_changeover()
    {
        Assert.Single(Project([Production(Black, 1000m)]).Segments);
    }

    [Fact]
    public void Planner_placed_changeover_suppresses_the_placeholder_and_is_sized_from_the_matrix()
    {
        var w = Production(White, 1000m);
        var co = NonProduction(JobType.Changeover, duration: null);
        var b = Production(Black, 1000m);

        var segments = Project([w, co, b], previous: White).Segments;

        Assert.Equal([w.JobId, co.JobId, b.JobId], segments.Select(s => s.JobId));
        Assert.All(segments, s => Assert.Equal(SegmentKind.Job, s.Kind));
        Assert.Equal(TimeSpan.FromMinutes(45), segments[1].Work);
    }

    [Fact]
    public void Changeover_job_with_its_own_duration_keeps_it()
    {
        var co = NonProduction(JobType.Changeover, TimeSpan.FromMinutes(20));

        var segments = Project([Production(White, 1000m), co, Production(Black, 1000m)], previous: White).Segments;

        Assert.Equal(TimeSpan.FromMinutes(20), segments[1].Work);
    }

    [Fact]
    public void Changeover_job_with_nothing_to_size_from_is_flagged()
    {
        var segment = Project([NonProduction(JobType.Changeover, null)]).Segments.Single();

        Assert.Contains(ScheduleWarningCode.NoDuration, Codes(segment));
        Assert.Equal(TimeSpan.Zero, segment.Work);
    }

    [Fact]
    public void Scheduler_mode_never_inserts_placeholders_and_warns_where_one_is_missing()
    {
        var w = Production(White, 1000m);
        var b = Production(Black, 1000m);
        var co = NonProduction(JobType.Changeover, null);
        var w2 = Production(White, 1000m);

        var segments = Project([w, b, co, w2], ChangeoverMode.Scheduler, previous: White).Segments;

        Assert.All(segments, s => Assert.Equal(SegmentKind.Job, s.Kind));
        Assert.Contains(ScheduleWarningCode.MissingChangeover, Codes(segments[1]));
        Assert.DoesNotContain(ScheduleWarningCode.MissingChangeover, Codes(segments[3]));
        Assert.Equal(TimeSpan.FromMinutes(90), segments[2].Work);
        Assert.Equal(Now.AddHours(2), segments[1].End);   // the missing changeover takes no time
    }

    [Fact]
    public void Cleaning_between_jobs_does_not_hide_a_planned_changeover()
    {
        var co = NonProduction(JobType.Changeover, TimeSpan.FromMinutes(45));
        var clean = NonProduction(JobType.Cleaning, TimeSpan.FromMinutes(30));
        var b = Production(Black, 1000m);

        var segments = Project([co, clean, b], ChangeoverMode.Scheduler, previous: White).Segments;

        Assert.Empty(segments[2].Warnings);
        Assert.Equal(Now.AddMinutes(75), segments[2].Start);
    }

    [Fact]
    public void Late_job_is_flagged_against_the_work_order_due_date()
    {
        var onTime = Production(White, 1000m, due: Now.AddHours(1));
        var late = Production(White, 1000m, due: Now.AddHours(1.5));

        var segments = Project([onTime, late], previous: White).Segments;

        Assert.Empty(segments[0].Warnings);
        var warning = Assert.Single(segments[1].Warnings);
        Assert.Equal(ScheduleWarningCode.Late, warning.Code);
        Assert.Contains("00:30:00", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_rate_or_unit_mismatch_is_flagged_and_takes_no_time()
    {
        var other = new MaterialRef(Guid.CreateVersion7(), null);
        var noRate = Production(other, 1000m);
        var wrongUnit = Production(White, 1000m, uom: Kilogram);

        var segments = Project([noRate, wrongUnit]).Segments;

        Assert.Contains(ScheduleWarningCode.NoRunStandard, Codes(segments[0]));
        Assert.Contains(ScheduleWarningCode.UomMismatch, Codes(segments[^1]));
        Assert.Equal(Now, segments[^1].End);
    }

    [Fact]
    public void A_job_in_another_unit_is_sized_through_its_conversion_to_the_rate_unit()
    {
        // 6562 ft at 1000 m/h: 1 ft = 0.3048 m, so 2000.0976 m, just over two hours.
        var feet = Production(White, 6562m, uom: Guid.CreateVersion7()) with { ToRateUnit = 0.3048m };

        var segment = Assert.Single(Project([feet], previous: White).Segments);

        Assert.DoesNotContain(ScheduleWarningCode.UomMismatch, Codes(segment));
        Assert.Equal(Now.AddSeconds(7201), segment.End);
    }

    [Fact]
    public void Earliest_start_holds_a_job_and_the_line_waits()
    {
        var held = Production(White, 1000m, earliest: Now.AddHours(5));
        var after = Production(White, 1000m);

        var segments = Project([held, after], previous: White).Segments;

        Assert.Equal((Now.AddHours(5), Now.AddHours(6)), (segments[0].Start, segments[0].End));
        Assert.Equal(Now.AddHours(6), segments[1].Start);
    }

    [Fact]
    public void Earliest_start_in_the_past_changes_nothing()
    {
        var segment = Project([Production(White, 1000m, earliest: Now.AddDays(-1))], previous: White).Segments.Single();

        Assert.Equal(Now, segment.Start);
    }

    [Fact]
    public void Work_and_changeovers_accrue_only_inside_shifts()
    {
        // Shifts 06:00–14:00 today and tomorrow; it is 08:00 now.
        var calendar = WorkCalendar.Create(
            [new TimeWindow(Now.AddHours(-2), Now.AddHours(6)), new TimeWindow(Now.AddHours(22), Now.AddHours(30))], []);

        var w = Production(White, 5000m);   // 5 h: 08:00–13:00
        var b = Production(Black, 2000m);   // 45 min changeover 13:00–13:45, then 15 min today + 1 h 45 tomorrow

        var segments = Project([w, b], previous: White, calendar: calendar).Segments;

        Assert.Equal(Now.AddHours(5), segments[0].End);
        Assert.Equal(Now.AddHours(5.75), segments[1].End);
        Assert.Equal(Now.AddHours(22 + 1.75), segments[2].End);
        Assert.All(segments, s => Assert.Empty(s.Warnings));
    }

    [Fact]
    public void Work_past_the_generated_shifts_is_flagged()
    {
        var calendar = WorkCalendar.Create([new TimeWindow(Now, Now.AddHours(1))], []);

        var segment = Project([Production(White, 3000m)], previous: White, calendar: calendar).Segments.Single();

        Assert.Contains(ScheduleWarningCode.BeyondCalendar, Codes(segment));
        Assert.Equal(Now.AddHours(3), segment.End);
    }

    [Fact]
    public void Running_maintenance_projects_its_remaining_planned_time()
    {
        var maintenance = NonProduction(JobType.Maintenance, TimeSpan.FromHours(4), JobStatus.Running, startedAt: Now.AddHours(-1));

        var segment = Project([maintenance]).Segments.Single();

        Assert.Equal((Now.AddHours(-1), Now.AddHours(3)), (segment.Start, segment.End));
    }

    [Fact]
    public void Maintenance_without_a_duration_is_flagged()
    {
        var segment = Project([NonProduction(JobType.Maintenance, null)]).Segments.Single();

        Assert.Contains(ScheduleWarningCode.NoDuration, Codes(segment));
    }

    [Fact]
    public void Quantities_round_up_to_whole_seconds()
    {
        var rates = new[] { new RunRate(White.Id, Metre, 7m, RateSource.Calculated) };

        var segment = Project([Production(White, 1m)], previous: White, rates: rates).Segments.Single();

        Assert.Equal(TimeSpan.FromSeconds(515), segment.Work);   // 3600 / 7 = 514.28...
    }

    [Fact]
    public void Projection_is_deterministic_and_linear_for_a_long_queue()
    {
        var jobs = Enumerable.Range(0, 5000)
            .Select(i => Production(i % 3 == 0 ? Black : White, 100m + i))
            .ToList();

        var first = Project(jobs, previous: White);
        var second = Project(jobs, previous: White);

        Assert.Equal(first.Segments.Select(s => (s.JobId, s.Start, s.End)), second.Segments.Select(s => (s.JobId, s.Start, s.End)));
        Assert.True(first.Segments.Zip(first.Segments.Skip(1)).All(p => p.First.End <= p.Second.Start));
    }

    [Fact]
    public void Firmed_job_holds_its_start_and_the_next_job_follows_it()
    {
        var a = Production(White, 1000m);
        var firmed = Production(White, 1000m, JobStatus.Firmed) with { FirmedStart = Now.AddHours(3) };
        var b = Production(White, 500m);

        var timeline = Project([a, firmed, b], previous: White);

        Assert.Collection(
            timeline.Segments,
            s => Assert.Equal((Now, Now.AddHours(1)), (s.Start, s.End)),
            s => Assert.Equal((Now.AddHours(3), Now.AddHours(4)), (s.Start, s.End)),
            s => Assert.Equal((Now.AddHours(4), Now.AddHours(4.5)), (s.Start, s.End)));
        Assert.All(timeline.Segments, s => Assert.Empty(s.Warnings));
    }

    [Fact]
    public void Work_running_into_a_firmed_job_is_flagged_not_moved_past_it()
    {
        var a = Production(White, 4000m);
        var firmed = Production(White, 1000m, JobStatus.Firmed) with { FirmedStart = Now.AddHours(3) };

        var segment = Project([a, firmed], previous: White).Segments[1];

        Assert.Equal((Now.AddHours(3), Now.AddHours(4)), (segment.Start, segment.End));
        Assert.Equal(ScheduleWarningCode.FirmedConflict, Assert.Single(segment.Warnings).Code);
    }

    [Fact]
    public void Work_running_into_a_firmed_job_is_reported_as_an_overlap()
    {
        var a = Production(White, 4000m);
        var firmed = Production(White, 1000m, JobStatus.Firmed) with { FirmedStart = Now.AddHours(3) };

        var timeline = Project([a, firmed], previous: White);

        var overlap = Assert.Single(timeline.Overlaps);
        Assert.Equal((a.JobId, firmed.JobId), (overlap.FirstJobId, overlap.SecondJobId));
        Assert.Equal((Now.AddHours(3), Now.AddHours(4)), (overlap.Start, overlap.End));
    }

    [Fact]
    public void A_changeover_placeholder_running_into_a_firmed_job_is_an_overlap_too()
    {
        var firmed = Production(Black, 1000m, JobStatus.Firmed) with { FirmedStart = Now.AddMinutes(30) };

        var timeline = Project([firmed], previous: White);

        var overlap = Assert.Single(timeline.Overlaps);
        Assert.Equal((SegmentKind.ChangeoverPlaceholder, firmed.JobId), (overlap.FirstKind, overlap.FirstJobId));
        Assert.Equal((SegmentKind.Job, firmed.JobId), (overlap.SecondKind, overlap.SecondJobId));
        Assert.Equal((Now.AddMinutes(30), Now.AddMinutes(45)), (overlap.Start, overlap.End));
    }

    [Fact]
    public void Back_to_back_and_zero_length_segments_do_not_overlap()
    {
        var a = Production(White, 1000m);
        var unknown = Production(White, 1000m, uom: Kilogram);
        var b = Production(White, 1000m);

        Assert.Empty(Project([a, unknown, b], previous: White).Overlaps);
    }

    [Fact]
    public void Firmed_start_in_the_past_starts_now_and_is_flagged()
    {
        var firmed = Production(White, 1000m, JobStatus.Firmed) with { FirmedStart = Now.AddHours(-2) };

        var segment = Project([firmed], previous: White).Segments.Single();

        Assert.Equal((Now, Now.AddHours(1)), (segment.Start, segment.End));
        Assert.Equal(ScheduleWarningCode.FirmedConflict, Assert.Single(segment.Warnings).Code);
    }
}
