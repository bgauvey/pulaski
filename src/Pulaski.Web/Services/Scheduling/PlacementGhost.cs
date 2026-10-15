using Pulaski.Contracts.Scheduling;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>A segment drawn in a line's drop lane: the would-be job (and its changeover), or a job it pushes later.</summary>
public sealed record GhostSegment(TimelineSegmentDto Segment, bool IsNew, TimeSpan Delay)
{
    public string Key => $"{Segment.Kind}:{Segment.JobId:N}";
}

/// <summary>
/// What dropping an order on a line at a slot would do, from the API's placement preview: where
/// the new job lands, how long it takes with its changeover, and which jobs it pushes back.
/// <see cref="Error"/> is set instead when the order can't go on that line.
/// </summary>
public sealed record PlacementGhost(
    Guid LineId,
    Guid? BeforeJobId,
    IReadOnlyList<GhostSegment> Segments,
    IReadOnlyList<JobDelayDto> Delayed,
    IReadOnlyList<ScheduleWarningDto> Warnings,
    bool IsOverlapping,
    string? Error = null,
    bool IsLoading = false,
    string? Note = null)
{
    /// <summary>No preview, just a word about what the drop will do (e.g. on a group, which takes a window).</summary>
    public static PlacementGhost Hint(Guid lineId, string note) => new(lineId, null, [], [], [], false, Note: note);

    public static PlacementGhost Loading(Guid lineId, Guid? beforeJobId) => new(lineId, beforeJobId, [], [], [], false, IsLoading: true);

    public static PlacementGhost Refused(Guid lineId, Guid? beforeJobId, string error) => new(lineId, beforeJobId, [], [], [], false, error);

    public static PlacementGhost From(Guid lineId, Guid? beforeJobId, JobPlacementPreviewDto preview)
    {
        var id = preview.PreviewJobId;
        var delays = preview.Delayed.ToDictionary(d => d.JobId, d => d.Delay);
        var segments = preview.Timeline.Segments
            .Where(s => s.JobId == id || delays.ContainsKey(s.JobId))
            .Select(s => new GhostSegment(s, s.JobId == id, s.JobId == id ? TimeSpan.Zero : delays[s.JobId]))
            .ToList();
        var job = segments.FirstOrDefault(s => s.IsNew && s.Segment.Kind == "job")?.Segment;

        return new PlacementGhost(
            lineId,
            beforeJobId,
            segments,
            preview.Delayed,
            job?.Warnings ?? [],
            preview.Timeline.Overlaps.Any(o => o.FirstJobId == id || o.SecondJobId == id));
    }

    /// <summary>The new job's own segment.</summary>
    public TimelineSegmentDto? Job => Segments.FirstOrDefault(s => s.IsNew && s.Segment.Kind == "job")?.Segment;

    /// <summary>The changeover Pulaski would put in front of it, if the material changes.</summary>
    public TimelineSegmentDto? Changeover => Segments.FirstOrDefault(s => s.IsNew && s.Segment.Kind != "job")?.Segment;

    public bool IsLate => Warnings.Any(w => w.Code == "late");

    /// <summary>'Thu 15 Oct 01:03 → Thu 15 Oct 08:12 · 7h 9m with a 45m changeover · pushes 2 jobs back, up to 7h 9m'.</summary>
    public string Summary(string? timeZone)
    {
        if (Error is not null)
        {
            return $"Can't place here: {Error}";
        }

        if (Note is not null)
        {
            return Note;
        }

        if (Job is not { } job)
        {
            return "Working out where it lands…";
        }

        var start = Changeover?.Start ?? job.Start;
        var parts = new List<string> { $"{ScheduleFormat.When(start, timeZone)} → {ScheduleFormat.When(job.End, timeZone)}" };
        parts.Add(job.End == job.Start
            ? "duration unknown (no run standard)"
            : Changeover is { } changeover
                ? $"{ScheduleFormat.Duration(job.End - job.Start)} with a {ScheduleFormat.Duration(changeover.End - changeover.Start)} changeover"
                : ScheduleFormat.Duration(job.End - job.Start));

        if (Delayed.Count > 0)
        {
            var most = ScheduleFormat.Duration(Delayed.Max(d => d.Delay));
            parts.Add(Delayed.Count == 1 ? $"pushes {Delayed[0].JobNumber} back {most}" : $"pushes {Delayed.Count} jobs back, up to {most}");
        }

        if (IsLate)
        {
            parts.Add("finishes after its due date");
        }

        if (IsOverlapping)
        {
            parts.Add("overlaps a firmed job");
        }

        return string.Join(" · ", parts);
    }
}
