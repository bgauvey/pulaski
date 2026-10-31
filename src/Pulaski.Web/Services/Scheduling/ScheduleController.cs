using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services.Scheduling;

public sealed record ScheduleToast(Guid Id, string Kind, string Text, DateTimeOffset Expires);

/// <summary>What an order dropped on a group should be planned with: the group and a suggested window (D-022).</summary>
public sealed record PlacementPreset(Guid WorkOrderId, Guid TargetId, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// State behind the schedule page: the current <see cref="ScheduleBoard"/>, the edits the planner
/// makes from any view, and what changed since the last read. Every edit is sent straight to the
/// API (no save step) and the board is re-read, because times are projected server-side (D-014).
/// Not thread-safe: the page calls it from the Blazor dispatcher only.
/// </summary>
public sealed class ScheduleController(PulaskiApiClient api, TimeProvider time)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ToastLife = TimeSpan.FromSeconds(6);

    private readonly List<ScheduleToast> _toasts = [];
    private readonly Dictionary<Guid, string> _pendingStatus = [];
    private IReadOnlyList<EquipmentDto> _equipment = [];
    private int _horizonDays = 28;

    public ScheduleBoard Board { get; private set; } = ScheduleBoard.Empty;

    public IReadOnlyList<EquipmentDto> Sites { get; private set; } = [];

    /// <summary>Whether the board may show a site: where the user may see the schedule (#43). Every site by default.</summary>
    public Func<Guid, bool> ShowsSite { get; init; } = _ => true;

    public Guid? SiteId { get; private set; }

    public string? TimeZone => Sites.FirstOrDefault(s => s.Id == SiteId)?.TimeZone;

    /// <summary>Item and work order keys that changed on the last refresh, for a brief highlight.</summary>
    public IReadOnlySet<string> Changed { get; private set; } = new HashSet<string>();

    /// <summary>Keys with an edit in flight; their cards show a saving state.</summary>
    public IReadOnlySet<string> Saving => _saving;

    private readonly HashSet<string> _saving = [];

    public IReadOnlyList<ScheduleToast> Toasts => _toasts;

    public ApiException? LoadError { get; private set; }

    public bool IsLoaded { get; private set; }

    public bool IsRefreshing { get; private set; }

    public DateTimeOffset Now => time.GetUtcNow();

    /// <summary>
    /// Raised after an edit finishes or a toast is added, so the page re-renders even when the
    /// edit started in one of its views.
    /// </summary>
    public event Action? StateChanged;

    /// <summary>The status to show for an order: the one it is being moved to while that is in flight.</summary>
    public string StatusOf(WorkOrderDto workOrder) => _pendingStatus.GetValueOrDefault(workOrder.Id, workOrder.Status);

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            _equipment = await api.ListEquipmentAsync(ct: ct);
            Sites = [.. _equipment.Where(e => e.Level == "site" && ShowsSite(e.Id)).OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)];
            SiteId ??= Sites.FirstOrDefault(s => _equipment.Any(e => e.SiteId == s.Id && OnBoard(e)))?.Id
                ?? (Sites.Count > 0 ? Sites[0].Id : null);
            await RefreshAsync(ct);
        }
        catch (ApiException ex)
        {
            LoadError = ex;
        }
    }

    public Task SelectSiteAsync(Guid siteId, CancellationToken ct = default)
    {
        SiteId = siteId;
        Board = ScheduleBoard.Empty;
        return RefreshAsync(ct);
    }

    /// <summary>
    /// Reads far enough ahead to cover <paramref name="until"/> (the API caps the horizon at a
    /// year). True when the horizon grew, so the board needs a fresh read.
    /// </summary>
    public bool EnsureHorizon(DateTimeOffset until)
    {
        var days = Math.Clamp((int)Math.Ceiling((until - Now).TotalDays) + 1, 28, 366);
        if (days <= _horizonDays)
        {
            return false;
        }

        _horizonDays = days;
        return true;
    }

    /// <summary>The job card being dragged, if any.</summary>
    public BoardItem? DraggingItem { get; private set; }

    /// <summary>The work order card being dragged, if any (Kanban, or the unscheduled tray).</summary>
    public WorkOrderDto? DraggingWorkOrder { get; private set; }

    /// <summary>While true the page holds back its refresh, so the board doesn't change under the pointer.</summary>
    public bool IsDragging => DraggingItem is not null || DraggingWorkOrder is not null;

    /// <summary>Whether the user may plan jobs at a site (<c>schedule.plan</c>); everywhere by default.</summary>
    public Func<Guid, bool> CanPlanAt { get; init; } = _ => true;

    /// <summary>Whether the user may dispatch, firm or cancel work orders at a site (<c>work_orders.manage</c>); everywhere by default.</summary>
    public Func<Guid, bool> CanManageOrdersAt { get; init; } = _ => true;

    /// <summary>
    /// The user may look at the site's schedule but not plan there (D-037, #47): jobs don't drag, the drawer leaves out what
    /// changes them, and an edit that gets here anyway says why instead of going to the API.
    /// </summary>
    public bool ReadOnly => SiteId is not { } site || !CanPlanAt(site);

    /// <summary>The user may not move the site's work orders between statuses: the Kanban doesn't drag, the drawer has no status buttons.</summary>
    public bool OrdersReadOnly => SiteId is not { } site || !CanManageOrdersAt(site);

    public const string ReadOnlyMessage = "You can view the schedule, but planning needs permission to plan jobs at this site.";

    public const string OrdersReadOnlyMessage = "You can view work orders, but dispatching, firming or cancelling them needs permission to manage work orders at this site.";

    public void StartDrag(BoardItem item)
    {
        if (!ReadOnly)
        {
            (DraggingItem, DraggingWorkOrder) = (item, null);
        }
    }

    // A work order drags onto a line (planning) on the Gantt, or to another status (managing orders) on the Kanban.
    public void StartDrag(WorkOrderDto workOrder)
    {
        if (!ReadOnly || !OrdersReadOnly)
        {
            (DraggingItem, DraggingWorkOrder) = (null, workOrder);
        }
    }

    public void EndDrag() => (DraggingItem, DraggingWorkOrder) = (null, null);

    /// <summary>Re-reads every line and work order. Announces overlaps that weren't there before.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (SiteId is not { } siteId)
        {
            IsLoaded = true;
            return;
        }

        IsRefreshing = true;
        try
        {
            // In the equipment tree's order, so a department sits above its lines.
            var lines = _equipment.Where(e => e.SiteId == siteId && e.IsActive && OnBoard(e)).ToList();
            var top = lines.Count > 0 ? lines.Min(l => l.Depth) : 0;
            var workOrders = api.ListWorkOrdersAsync(ct: ct);
            var timelines = await Task.WhenAll(lines.Select(l => api.GetLineTimelineAsync(l.Id, _horizonDays, ct)));
            var board = ScheduleBoard.Build(
                lines.Zip(timelines, (line, timeline) => new BoardLine(line, timeline, line.Depth - top)),
                [.. (await workOrders).Where(w => w.SiteId == siteId)],
                Now,
                _equipment.ToDictionary(e => e.Id, e => e.ParentId));

            var previous = Board;
            Board = board;
            Changed = board.ChangedSince(previous);
            LoadError = null;

            if (IsLoaded && !ReferenceEquals(previous, ScheduleBoard.Empty))
            {
                foreach (var overlap in board.NewOverlapsSince(previous))
                {
                    Toast("danger", $"New overlap. {overlap.Describe()}.");
                }
            }

            IsLoaded = true;
        }
        catch (ApiException ex)
        {
            LoadError = ex;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    /// <summary>Lines and the groups that plan for them (D-022).</summary>
    private static bool OnBoard(EquipmentDto equipment) => equipment.IsSchedulable || equipment.CanRunJob;

    /// <summary>
    /// Puts a job on <paramref name="lineId"/> before <paramref name="beforeJobId"/> (null = end of
    /// line). A job planned to a group is moved by assignment instead (D-022).
    /// </summary>
    public Task MoveJobAsync(BoardItem item, Guid lineId, Guid? beforeJobId)
    {
        if (item.IsFromGroup)
        {
            return AssignJobAsync(item, lineId, beforeJobId);
        }

        // Dropped where it already is: nothing to send.
        if (beforeJobId == item.JobId
            || (lineId == item.LineId && beforeJobId == ScheduleLayout.NextJobAfter(Board.ItemsOn(lineId), item.JobId)))
        {
            return Task.CompletedTask;
        }

        var where = beforeJobId is { } before && Board.Find(before) is { } next
            ? $"before {next.Segment.JobNumber}"
            : "at the end";
        var line = Board.FindLine(lineId)?.Code ?? "the line";
        var message = item.Segment.Status == "firmed"
            ? $"{item.Segment.JobNumber} moved to {line}; it keeps its firmed start."
            : $"{item.Segment.JobNumber} moved to {line}, {where}.";

        return RunAsync(item.Key, message, () => api.SequenceJobAsync(
            item.JobId, new SequenceJobRequest { Version = item.Segment.Version ?? 0, LineId = lineId, BeforeJobId = beforeJobId }));
    }

    /// <summary>
    /// The floor's move of a job planned to a group (D-022): onto <paramref name="targetId"/>, a line
    /// under the group (before <paramref name="beforeJobId"/>, or at the end), or back to the group.
    /// </summary>
    public Task AssignJobAsync(BoardItem item, Guid targetId, Guid? beforeJobId)
    {
        var target = Board.FindLine(targetId);
        var number = item.Segment.JobNumber;
        if (targetId == item.LineId
            && (target?.IsGroup == true || beforeJobId == ScheduleLayout.NextJobAfter(Board.ItemsOn(targetId), item.JobId)))
        {
            return Task.CompletedTask;
        }

        var message = target switch
        {
            { IsGroup: true } => $"{number} is back on {target.Code}, waiting for a line.",
            _ when item.IsWindow => $"{number} assigned to {target?.Code ?? "the line"}.",
            _ => $"{number} moved to {target?.Code ?? "the line"}.",
        };

        return RunAsync(item.Key, message, () => api.AssignJobAsync(
            item.JobId,
            new AssignJobRequest { Version = item.Segment.Version ?? 0, TargetId = targetId, BeforeJobId = target?.IsGroup == true ? null : beforeJobId }));
    }

    /// <summary>Sets the lock and the earliest start, keeping the job's finish-by.</summary>
    public Task PinJobAsync(BoardItem item, bool isLocked, DateTimeOffset? earliestStart) =>
        PinJobAsync(item, isLocked, earliestStart, item.Segment.LatestEnd);

    public Task PinJobAsync(BoardItem item, bool isLocked, DateTimeOffset? earliestStart, DateTimeOffset? latestEnd)
    {
        var s = item.Segment;
        var message = (isLocked, earliestStart, latestEnd) switch
        {
            (true, _, _) when !s.IsLocked => $"{s.JobNumber} locked.",
            (false, _, _) when s.IsLocked => $"{s.JobNumber} unlocked.",
            _ when latestEnd != s.LatestEnd && item.IsWindow =>
                $"{s.JobNumber}'s window is now {ScheduleFormat.When(earliestStart ?? Now, TimeZone)} to {ScheduleFormat.When(latestEnd, TimeZone)}.",
            _ when latestEnd != s.LatestEnd => latestEnd is null
                ? $"{s.JobNumber} no longer has a finish-by time."
                : $"{s.JobNumber} must finish by {ScheduleFormat.When(latestEnd, TimeZone)}.",
            (_, null, _) => $"{s.JobNumber} no longer waits for a start time.",
            (_, { } at, _) => $"{s.JobNumber} starts no earlier than {ScheduleFormat.When(at, TimeZone)}.",
        };

        return RunAsync(item.Key, message, () => api.PinJobAsync(
            item.JobId,
            new PinJobRequest { Version = s.Version ?? 0, IsLocked = isLocked, EarliestStart = earliestStart, LatestEnd = latestEnd }));
    }

    /// <summary>
    /// Holds a job until a site-local day (the calendar's drag). The plan is a sequence, so a job
    /// can only be held later, never pulled ahead of the jobs before it; dropping it on or before
    /// the day it already starts just clears any hold.
    /// </summary>
    public Task HoldJobUntilAsync(BoardItem item, DateOnly day)
    {
        var (dayStart, _) = ScheduleLayout.DayRange(day, TimeZone);
        if (item.IsWindow)
        {
            Toast("info", $"{item.Segment.JobNumber} waits for a line in its window; change the window in its editor.");
            return Task.CompletedTask;
        }

        if (dayStart > item.Segment.Start && dayStart > Now)
        {
            return PinJobAsync(item, item.Segment.IsLocked, dayStart);
        }

        if (item.Segment.EarliestStart is not null)
        {
            return PinJobAsync(item, item.Segment.IsLocked, null);
        }

        Toast("info", $"{item.Segment.JobNumber} already starts as early as its line allows. Change the sequence on the Gantt to run it sooner.");
        return Task.CompletedTask;
    }

    public Task CancelJobAsync(BoardItem item, bool replace, string? reason, bool exact = false, bool roundDown = false) =>
        RunAsync(
            item.Key,
            !replace ? $"{item.Segment.JobNumber} cancelled."
                : roundDown ? $"{item.Segment.JobNumber} cancelled and replaced for the whole batches left; the order may end short."
                : $"{item.Segment.JobNumber} cancelled and replaced for {(exact ? "exactly " : "")}the rest of its quantity.",
            async () => (await api.CancelJobAsync(item.JobId, new CancelJobRequest
            {
                Version = item.Segment.Version ?? 0,
                Replace = replace,
                ReplaceExact = exact,
                RoundDown = roundDown,
                Reason = reason,
            })).Note);

    /// <summary>
    /// What placing the order's unplaced quantity on a line would do, projected by the API and
    /// saved nowhere: the Gantt shows it under the line while the order is dragged over it.
    /// </summary>
    /// <exception cref="ApiException">The order can't go there, e.g. the line has no capability for its material.</exception>
    public Task<JobPlacementPreviewDto> PreviewPlacementAsync(WorkOrderDto workOrder, Guid lineId, Guid? beforeJobId, CancellationToken ct) =>
        api.PreviewPlacementAsync(lineId, workOrder.Id, Board.UnplacedQty(workOrder), beforeJobId, Board.NextJobNumber(workOrder), ct);

    /// <summary>
    /// Cuts a job from a planned order onto a line (the unscheduled tray's drag), or plans it to a
    /// group with a window, <paramref name="earliestStart"/> to <paramref name="latestEnd"/> (D-022).
    /// </summary>
    public Task PlaceWorkOrderAsync(
        WorkOrderDto workOrder,
        Guid lineId,
        Guid? beforeJobId,
        decimal qty,
        DateTimeOffset? earliestStart = null,
        DateTimeOffset? latestEnd = null)
    {
        var number = Board.NextJobNumber(workOrder);
        var target = Board.FindLine(lineId);
        var message = target?.IsGroup == true
            ? $"{number} planned to {target.Code} for {ScheduleFormat.Qty(qty, workOrder.UomCode)}, {ScheduleFormat.When(earliestStart ?? Now, TimeZone)} to {ScheduleFormat.When(latestEnd, TimeZone)}."
            : $"{number} created on {target?.Code ?? "the line"} for {ScheduleFormat.Qty(qty, workOrder.UomCode)}.";
        return RunAsync(
            ScheduleBoard.WorkOrderKey(workOrder.Id),
            message,
            () => api.CreateJobAsync(new CreateJobRequest
            {
                LineId = lineId,
                JobNumber = number,
                JobType = "production",
                WorkOrderId = workOrder.Id,
                PlannedQty = qty,
                BeforeJobId = target?.IsGroup == true ? null : beforeJobId,
                EarliestStart = earliestStart,
                LatestEnd = latestEnd,
            }));
    }

    /// <summary>Dispatch, un-dispatch, firm or a plain status change (the Kanban's drag).</summary>
    public Task MoveWorkOrderAsync(WorkOrderDto workOrder, string status)
    {
        if (workOrder.Status == status)
        {
            return Task.CompletedTask;
        }

        if (!workOrder.NextStatuses.Contains(status))
        {
            Toast("warning", $"{workOrder.OrderNumber} can't go from {Display.Label(workOrder.Status).ToLowerInvariant()} to {Display.Label(status).ToLowerInvariant()}.");
            return Task.CompletedTask;
        }

        return MoveAsync();

        // The card goes to its new column straight away; the refresh after the edit confirms it or puts it back.
        async Task MoveAsync()
        {
            _pendingStatus[workOrder.Id] = status;
            try
            {
                await RunAsync(
                    ScheduleBoard.WorkOrderKey(workOrder.Id),
                    $"{workOrder.OrderNumber} is now {Display.Label(status).ToLowerInvariant()}.",
                    () => api.MoveWorkOrderAsync(workOrder, status),
                    orders: true);
            }
            finally
            {
                _pendingStatus.Remove(workOrder.Id);
            }
        }
    }

    public void Toast(string kind, string text)
    {
        _toasts.Add(new ScheduleToast(Guid.CreateVersion7(), kind, text, Now + ToastLife));
        StateChanged?.Invoke();
    }

    public void Dismiss(Guid toastId) => _toasts.RemoveAll(t => t.Id == toastId);

    /// <summary>Drops expired toasts; true when any went.</summary>
    public bool ExpireToasts() => _toasts.RemoveAll(t => t.Expires <= Now) > 0;

    /// <summary>Sends one edit, then re-reads the board whatever happened: after a conflict the planner needs the current state.</summary>
    private Task RunAsync(string key, string success, Func<Task> send, bool orders = false) =>
        RunAsync(key, success, async () =>
        {
            await send();
            return null;
        }, orders);

    // The server's note, when it sends one (a partial batch, an order left short), is added to the success message.
    private async Task RunAsync(string key, string success, Func<Task<string?>> send, bool orders = false)
    {
        if (orders ? OrdersReadOnly : ReadOnly)
        {
            Toast("warning", orders ? OrdersReadOnlyMessage : ReadOnlyMessage);
            return;
        }

        _saving.Add(key);
        try
        {
            var note = await send();
            Toast("success", note is null ? success : $"{success} {note}");
        }
        catch (ApiException ex)
        {
            Toast(ex.IsConflict ? "warning" : "danger", ex.IsConflict
                ? "Someone else changed this first; the board now shows their change."
                : ex.Detail ?? ex.Title);
        }
        finally
        {
            await RefreshAsync();
            _saving.Remove(key);
            StateChanged?.Invoke();
        }
    }
}
