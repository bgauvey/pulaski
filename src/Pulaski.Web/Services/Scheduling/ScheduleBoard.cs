using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>
/// A row of the board: a line and its projected timeline, or a group such as a department whose
/// timeline is the windows of the jobs waiting for one of its lines (D-022).
/// <see cref="Indent"/> is its depth below the board's top row, for showing the hierarchy.
/// </summary>
public sealed record BoardLine(EquipmentDto Line, LineTimelineDto Timeline, int Indent = 0)
{
    public Guid Id => Line.Id;

    public string Code => Line.Code;

    public bool IsGroup => Timeline.IsGroup;
}

/// <summary>
/// One segment of a line's timeline on the board: a job, or a changeover placeholder Pulaski put
/// in front of one. A placeholder shares its job's id, so <see cref="Key"/> tells the two apart.
/// </summary>
public sealed record BoardItem(Guid LineId, string LineCode, TimelineSegmentDto Segment)
{
    public string Key => KeyOf(Segment.JobId, Segment.Kind);

    public Guid JobId => Segment.JobId;

    public bool IsPlaceholder => Segment.Kind == "changeover_placeholder";

    /// <summary>A job waiting on a group, drawn as its window (D-022).</summary>
    public bool IsWindow => Segment.Kind == "window";

    /// <summary>Planned to a group: the floor moves it between the group's lines, whatever its plan status (D-022).</summary>
    public bool IsFromGroup => Segment.GroupEquipmentId is not null && !IsPlaceholder;

    /// <summary>
    /// Planned jobs move in time; firmed jobs only change line (D-019); locked jobs don't move.
    /// A job from a group moves on the floor until it starts (D-022); its window doesn't resequence.
    /// </summary>
    public bool CanResequence =>
        !IsPlaceholder && !IsWindow && !Segment.IsLocked && (IsFromGroup ? IsOpenForFloor : Segment.Status == "planned");

    public bool CanChangeLine =>
        !IsPlaceholder && !Segment.IsLocked && (IsFromGroup ? IsOpenForFloor : Segment.Status is "planned" or "firmed");

    private bool IsOpenForFloor => Segment.Status is "planned" or "dispatched" or "firmed";

    /// <summary>The board only shows open jobs, and any open job can be pinned or cancelled.</summary>
    public bool CanPin => !IsPlaceholder;

    public bool CanCancel => !IsPlaceholder;

    /// <summary>Changes whenever anything shown on the card changes, so a refresh can tell what moved.</summary>
    public string Signature =>
        $"{LineId}|{Segment.Status}|{Segment.Version}|{Segment.Start.UtcTicks}|{Segment.End.UtcTicks}|{Segment.IsLocked}";

    public static string KeyOf(Guid jobId, string kind) => $"{kind}:{jobId:N}";
}

/// <summary>Two segments on one line whose times intersect.</summary>
public sealed record BoardOverlap(BoardLine Line, BoardItem First, BoardItem Second, DateTimeOffset Start, DateTimeOffset End)
{
    public TimeSpan Duration => End - Start;

    /// <summary>Identifies the pair across refreshes, so only new overlaps are announced.</summary>
    public string Key => $"{First.Key}~{Second.Key}";

    public string Describe()
    {
        var text = $"{Name(First)} and {Name(Second)} overlap by {ScheduleFormat.Duration(Duration)} on {Line.Code}";
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Name(BoardItem item) =>
        item.IsPlaceholder ? $"the changeover before {item.Segment.JobNumber}" : item.Segment.JobNumber;
}

/// <summary>
/// An immutable snapshot of the schedule: every line's timeline and every work order. Views read
/// it; edits go to the API and the board is rebuilt from a fresh read.
/// </summary>
public sealed class ScheduleBoard
{
    private readonly Dictionary<string, BoardItem> _byKey;
    private readonly Dictionary<Guid, List<BoardItem>> _jobsByWorkOrder;
    private readonly HashSet<string> _overlapping;

    private readonly IReadOnlyDictionary<Guid, Guid?> _parents;

    private ScheduleBoard(
        IReadOnlyList<BoardLine> lines,
        IReadOnlyList<WorkOrderDto> workOrders,
        DateTimeOffset loadedAt,
        IReadOnlyDictionary<Guid, Guid?> parents)
    {
        _parents = parents;
        Lines = lines;
        WorkOrders = workOrders;
        LoadedAt = loadedAt;
        Items = [.. lines.SelectMany(l => l.Timeline.Segments.Select(s => new BoardItem(l.Id, l.Code, s)))];
        _byKey = Items.ToDictionary(i => i.Key);
        _jobsByWorkOrder = Items
            .Where(i => !i.IsPlaceholder && i.Segment.WorkOrderId is not null)
            .GroupBy(i => i.Segment.WorkOrderId!.Value)
            .ToDictionary(g => g.Key, g => g.ToList());

        Overlaps = [.. lines.SelectMany(line => line.Timeline.Overlaps
            .Select(o => (Overlap: o, First: Find(o.FirstJobId, o.FirstKind), Second: Find(o.SecondJobId, o.SecondKind)))
            .Where(x => x.First is not null && x.Second is not null)
            .Select(x => new BoardOverlap(line, x.First!, x.Second!, x.Overlap.Start, x.Overlap.End)))];
        _overlapping = [.. Overlaps.SelectMany(o => new[] { o.First.Key, o.Second.Key })];
    }

    public static ScheduleBoard Empty { get; } = new([], [], DateTimeOffset.MinValue, new Dictionary<Guid, Guid?>());

    public IReadOnlyList<BoardLine> Lines { get; }

    public IReadOnlyList<WorkOrderDto> WorkOrders { get; }

    /// <summary>Every segment on every line, each line in timeline order.</summary>
    public IReadOnlyList<BoardItem> Items { get; }

    public IReadOnlyList<BoardOverlap> Overlaps { get; }

    public DateTimeOffset LoadedAt { get; }

    /// <summary>
    /// Rows stay in the order given (the equipment tree's). <paramref name="parents"/> maps
    /// equipment to its parent, for telling which lines sit under a group; without it every row is
    /// taken as a top-level row.
    /// </summary>
    public static ScheduleBoard Build(
        IEnumerable<BoardLine> lines,
        IReadOnlyList<WorkOrderDto> workOrders,
        DateTimeOffset loadedAt,
        IReadOnlyDictionary<Guid, Guid?>? parents = null) =>
        new([.. lines], workOrders, loadedAt, parents ?? new Dictionary<Guid, Guid?>());

    /// <summary>True when <paramref name="lineId"/> sits somewhere below <paramref name="groupId"/>.</summary>
    public bool IsUnder(Guid groupId, Guid lineId)
    {
        for (var at = lineId; _parents.TryGetValue(at, out var parent) && parent is { } up; at = up)
        {
            if (up == groupId)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Group windows that could run on <paramref name="lineId"/>, with the estimate for that line:
    /// drawn as ghosts on the line's row until the floor assigns them (D-022).
    /// </summary>
    public IReadOnlyList<(BoardItem Window, CandidateLineDto Candidate)> GhostsOn(Guid lineId) =>
        [.. Items
            .Where(i => i.IsWindow)
            .SelectMany(i => i.Segment.Candidates.Where(c => c.LineId == lineId).Select(c => (i, c)))];

    /// <summary>
    /// Whether a drag of <paramref name="item"/> may end on <paramref name="target"/>. A job from a
    /// group goes to its group or to a line under it that could run it; any other job goes to a
    /// line, never a group: resequenced on its own line, moved to another (D-019, D-022).
    /// </summary>
    public bool CanDrop(BoardItem item, BoardLine target)
    {
        if (item.IsFromGroup)
        {
            var group = item.Segment.GroupEquipmentId!.Value;
            if (target.Id == group)
            {
                return item.CanChangeLine && !item.IsWindow;
            }

            return item.CanChangeLine
                && !target.IsGroup
                && IsUnder(group, target.Id)
                && (!item.IsWindow || item.Segment.Candidates.Any(c => c.LineId == target.Id));
        }

        return !target.IsGroup && (item.LineId == target.Id ? item.CanResequence : item.CanChangeLine);
    }

    /// <summary>Where the floor may put a job from a group: the lines under the group that could run it, then the group itself.</summary>
    public IReadOnlyList<BoardLine> AssignTargets(BoardItem item) =>
        item.IsFromGroup
            ? [.. Lines.Where(l => l.Id != item.LineId && CanDrop(item, l))]
            : [];

    public BoardItem? Find(Guid jobId, string kind = "job") => _byKey.GetValueOrDefault(BoardItem.KeyOf(jobId, kind));

    public BoardLine? FindLine(Guid lineId) => Lines.FirstOrDefault(l => l.Id == lineId);

    public WorkOrderDto? FindWorkOrder(Guid? id) => id is null ? null : WorkOrders.FirstOrDefault(w => w.Id == id);

    public IReadOnlyList<BoardItem> ItemsOn(Guid lineId) => [.. Items.Where(i => i.LineId == lineId)];

    /// <summary>The order's open jobs, earliest first.</summary>
    public IReadOnlyList<BoardItem> JobsFor(Guid workOrderId) =>
        _jobsByWorkOrder.TryGetValue(workOrderId, out var jobs) ? [.. jobs.OrderBy(j => j.Segment.Start)] : [];

    public bool IsOverlapping(BoardItem item) => _overlapping.Contains(item.Key);

    public bool IsOverlapping(WorkOrderDto workOrder) => JobsFor(workOrder.Id).Any(IsOverlapping);

    public IEnumerable<BoardOverlap> OverlapsFor(BoardItem item) =>
        Overlaps.Where(o => o.First.Key == item.Key || o.Second.Key == item.Key);

    /// <summary>Quantity of a planned order not yet on any line; zero once the order has left planning.</summary>
    public decimal UnplacedQty(WorkOrderDto workOrder) =>
        workOrder.Status != "planned"
            ? 0m
            : Math.Max(0m, workOrder.PlannedQty - JobsFor(workOrder.Id).Sum(j => j.Segment.PlannedQty ?? 0m));

    /// <summary>Planned orders that still have quantity to place on a line.</summary>
    public IReadOnlyList<WorkOrderDto> Unscheduled() =>
        [.. WorkOrders.Where(w => UnplacedQty(w) > 0m).OrderBy(w => w.DueAt ?? DateTimeOffset.MaxValue).ThenBy(w => w.OrderNumber)];

    /// <summary>Job number for the next job cut from an order: ORDER-01, ORDER-02, ...</summary>
    public string NextJobNumber(WorkOrderDto workOrder)
    {
        var taken = Items.Select(i => i.Segment.JobNumber).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var n = JobsFor(workOrder.Id).Count + 1; ; n++)
        {
            var number = $"{workOrder.OrderNumber}-{n:00}";
            if (!taken.Contains(number))
            {
                return number;
            }
        }
    }

    /// <summary>Keys of items that are new or changed since <paramref name="previous"/>, and work order ids likewise.</summary>
    public IReadOnlySet<string> ChangedSince(ScheduleBoard previous)
    {
        if (ReferenceEquals(previous, Empty))
        {
            return new HashSet<string>();
        }

        var changed = Items
            .Where(i => previous._byKey.GetValueOrDefault(i.Key)?.Signature != i.Signature)
            .Select(i => i.Key)
            .ToHashSet();

        var before = previous.WorkOrders.ToDictionary(w => w.Id, w => w.Version);
        foreach (var workOrder in WorkOrders)
        {
            if (!before.TryGetValue(workOrder.Id, out var version) || version != workOrder.Version)
            {
                changed.Add(WorkOrderKey(workOrder.Id));
            }
        }

        return changed;
    }

    /// <summary>Overlaps that were not on <paramref name="previous"/>.</summary>
    public IReadOnlyList<BoardOverlap> NewOverlapsSince(ScheduleBoard previous)
    {
        var known = previous.Overlaps.Select(o => o.Key).ToHashSet();
        return [.. Overlaps.Where(o => !known.Contains(o.Key))];
    }

    public static string WorkOrderKey(Guid id) => $"wo:{id:N}";
}
