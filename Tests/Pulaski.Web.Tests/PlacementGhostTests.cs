using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Scheduling;

namespace Pulaski.Web.Tests;

public class PlacementGhostTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid NewJob = Guid.CreateVersion7();

    private static TimelineSegmentDto Segment(Guid id, string number, double start, double end, string kind = "job", IReadOnlyList<ScheduleWarningDto>? warnings = null) => new()
    {
        Kind = kind,
        JobId = id,
        JobNumber = number,
        JobType = "production",
        Status = "planned",
        Start = T0.AddHours(start),
        End = T0.AddHours(end),
        Warnings = warnings ?? [],
    };

    private static JobPlacementPreviewDto Preview(IReadOnlyList<TimelineSegmentDto> segments, IReadOnlyList<JobDelayDto>? delayed = null, IReadOnlyList<TimelineOverlapDto>? overlaps = null) => new()
    {
        PreviewJobId = NewJob,
        Timeline = new LineTimelineDto { LineId = Line, LineCode = "EXT-01", LineName = "Line", ChangeoverMode = "auto", Segments = segments, Overlaps = overlaps ?? [] },
        Delayed = delayed ?? [],
    };

    [Fact]
    public void Keeps_the_new_job_its_changeover_and_the_jobs_it_pushes_but_not_the_rest()
    {
        var before = Guid.CreateVersion7();
        var pushed = Guid.CreateVersion7();
        var ghost = PlacementGhost.From(Line, pushed, Preview(
        [
            Segment(before, "A", 0, 2),
            Segment(NewJob, "WO-9-01", 2, 2.75, kind: "changeover_placeholder"),
            Segment(NewJob, "WO-9-01", 2.75, 5),
            Segment(pushed, "B", 5, 6),
        ],
        [new JobDelayDto(pushed, "B", TimeSpan.FromHours(3))]));

        Assert.Equal(["changeover_placeholder:WO-9-01", "job:WO-9-01", "job:B"], ghost.Segments.Select(s => $"{s.Segment.Kind}:{s.Segment.JobNumber}"));
        Assert.Equal(TimeSpan.FromHours(3), ghost.Segments[2].Delay);
        Assert.Equal(T0.AddHours(2.75), ghost.Job!.Start);
        Assert.Equal(
            "Wed 14 Oct 08:00 → Wed 14 Oct 11:00 · 2h 15m with a 45m changeover · pushes B back 3h",
            ghost.Summary(null));
    }

    [Fact]
    public void Summary_flags_lateness_and_an_overlap_with_a_firmed_job()
    {
        var firmed = Guid.CreateVersion7();
        var ghost = PlacementGhost.From(Line, null, Preview(
            [Segment(NewJob, "N", 0, 4, warnings: [new ScheduleWarningDto("late", "Late.")]), Segment(firmed, "F", 3, 5)],
            overlaps: [new TimelineOverlapDto(NewJob, "job", firmed, "job", T0.AddHours(3), T0.AddHours(4))]));

        Assert.True(ghost.IsLate);
        Assert.True(ghost.IsOverlapping);
        Assert.EndsWith("finishes after its due date · overlaps a firmed job", ghost.Summary(null), StringComparison.Ordinal);
    }

    [Fact]
    public void A_refused_slot_says_why_and_a_job_without_a_rate_says_its_length_is_unknown()
    {
        Assert.Equal("Can't place here: PRF is not set up to run on EXT-02.", PlacementGhost.Refused(Line, null, "PRF is not set up to run on EXT-02.").Summary(null));
        Assert.Contains("duration unknown", PlacementGhost.From(Line, null, Preview([Segment(NewJob, "N", 1, 1)])).Summary(null), StringComparison.Ordinal);
    }
}
