using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>A schedulable line and its projected timeline.</summary>
public sealed record BoardLine(EquipmentDto Line, LineTimelineDto Timeline)
{
    public Guid Id => Line.Id;

    public string Code => Line.Code;
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

    /// <summary>Planned jobs move in time; firmed jobs only change line (D-019); locked jobs don't move.</summary>
    public bool CanResequence => !IsPlaceholder && !Segment.IsLocked && Segment.Status == "planned";

    public bool CanChangeLine => !IsPlaceholder && !Segment.IsLocked && Segment.Status is "planned" or "firmed";

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

    private ScheduleBoard(
        IReadOnlyList<BoardLine> lines,
        IReadOnlyList<WorkOrderDto> workOrders,
        DateTimeOffset loadedAt)
    {
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

    public static ScheduleBoard Empty { get; } = new([], [], DateTimeOffset.MinValue);

    public IReadOnlyList<BoardLine> Lines { get; }

    public IReadOnlyList<WorkOrderDto> WorkOrders { get; }

    /// <summary>Every segment on every line, each line in timeline order.</summary>
    public IReadOnlyList<BoardItem> Items { get; }

    public IReadOnlyList<BoardOverlap> Overlaps { get; }

    public DateTimeOffset LoadedAt { get; }

    public static ScheduleBoard Build(
        IEnumerable<BoardLine> lines,
        IReadOnlyList<WorkOrderDto> workOrders,
        DateTimeOffset loadedAt) =>
        new([.. lines.OrderBy(l => l.Code, StringComparer.OrdinalIgnoreCase)], workOrders, loadedAt);

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
