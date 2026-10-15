using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services.Scheduling;

public sealed record ScheduleToast(Guid Id, string Kind, string Text, DateTimeOffset Expires);

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
            Sites = [.. _equipment.Where(e => e.Level == "site").OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase)];
            SiteId ??= Sites.FirstOrDefault(s => _equipment.Any(e => e.SiteId == s.Id && e.IsSchedulable))?.Id
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

    public void StartDrag(BoardItem item) => (DraggingItem, DraggingWorkOrder) = (item, null);

    public void StartDrag(WorkOrderDto workOrder) => (DraggingItem, DraggingWorkOrder) = (null, workOrder);

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
            var lines = _equipment.Where(e => e.SiteId == siteId && e.IsSchedulable && e.IsActive).ToList();
            var workOrders = api.ListWorkOrdersAsync(ct: ct);
            var timelines = await Task.WhenAll(lines.Select(l => api.GetLineTimelineAsync(l.Id, _horizonDays, ct)));
            var board = ScheduleBoard.Build(
                lines.Zip(timelines, (line, timeline) => new BoardLine(line, timeline)),
                [.. (await workOrders).Where(w => w.SiteId == siteId)],
                Now);

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

    /// <summary>Puts a job on <paramref name="lineId"/> before <paramref name="beforeJobId"/> (null = end of line).</summary>
    public Task MoveJobAsync(BoardItem item, Guid lineId, Guid? beforeJobId)
    {
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

    public Task PinJobAsync(BoardItem item, bool isLocked, DateTimeOffset? earliestStart)
    {
        var message = (isLocked, earliestStart) switch
        {
            (true, _) when !item.Segment.IsLocked => $"{item.Segment.JobNumber} locked.",
            (false, _) when item.Segment.IsLocked => $"{item.Segment.JobNumber} unlocked.",
            (_, null) => $"{item.Segment.JobNumber} no longer waits for a start time.",
            (_, { } at) => $"{item.Segment.JobNumber} starts no earlier than {ScheduleFormat.When(at, TimeZone)}.",
        };

        return RunAsync(item.Key, message, () => api.PinJobAsync(
            item.JobId, new PinJobRequest { Version = item.Segment.Version ?? 0, IsLocked = isLocked, EarliestStart = earliestStart }));
    }

    /// <summary>
    /// Holds a job until a site-local day (the calendar's drag). The plan is a sequence, so a job
    /// can only be held later, never pulled ahead of the jobs before it; dropping it on or before
    /// the day it already starts just clears any hold.
    /// </summary>
    public Task HoldJobUntilAsync(BoardItem item, DateOnly day)
    {
        var (dayStart, _) = ScheduleLayout.DayRange(day, TimeZone);
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

    public Task CancelJobAsync(BoardItem item, bool replace, string? reason) =>
        RunAsync(
            item.Key,
            replace ? $"{item.Segment.JobNumber} cancelled and replaced for the rest of its quantity." : $"{item.Segment.JobNumber} cancelled.",
            () => api.CancelJobAsync(item.JobId, new CancelJobRequest { Version = item.Segment.Version ?? 0, Replace = replace, Reason = reason }));

    /// <summary>
    /// What placing the order's unplaced quantity on a line would do, projected by the API and
    /// saved nowhere: the Gantt shows it under the line while the order is dragged over it.
    /// </summary>
    /// <exception cref="ApiException">The order can't go there, e.g. the line has no capability for its material.</exception>
    public Task<JobPlacementPreviewDto> PreviewPlacementAsync(WorkOrderDto workOrder, Guid lineId, Guid? beforeJobId, CancellationToken ct) =>
        api.PreviewPlacementAsync(lineId, workOrder.Id, Board.UnplacedQty(workOrder), beforeJobId, Board.NextJobNumber(workOrder), ct);

    /// <summary>Cuts a job from a planned order onto a line (the unscheduled tray's drag).</summary>
    public Task PlaceWorkOrderAsync(WorkOrderDto workOrder, Guid lineId, Guid? beforeJobId, decimal qty)
    {
        var number = Board.NextJobNumber(workOrder);
        var line = Board.FindLine(lineId)?.Code ?? "the line";
        return RunAsync(
            ScheduleBoard.WorkOrderKey(workOrder.Id),
            $"{number} created on {line} for {ScheduleFormat.Qty(qty, workOrder.UomCode)}.",
            () => api.CreateJobAsync(new CreateJobRequest
            {
                LineId = lineId,
                JobNumber = number,
                JobType = "production",
                WorkOrderId = workOrder.Id,
                PlannedQty = qty,
                BeforeJobId = beforeJobId,
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
                    () => api.MoveWorkOrderAsync(workOrder, status));
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
    private async Task RunAsync(string key, string success, Func<Task> send)
    {
        _saving.Add(key);
        try
        {
            await send();
            Toast("success", success);
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
