using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services;

/// <summary>A summary tile on the work orders screen; clicking it filters the list to its orders.</summary>
public sealed record WorkOrderTile(string Key, string Label, string Tone, string Help);

/// <summary>
/// Sorts work orders into the tiles. Times are site-local days. An order can be in several tiles
/// (starting today and behind, say); <see cref="OnTrack"/> and <see cref="Behind"/> never overlap.
/// </summary>
public static class WorkOrderTiles
{
    public const string StartingToday = "starting_today";
    public const string FinishingToday = "finishing_today";
    public const string OnTrack = "on_track";
    public const string Behind = "behind";
    public const string NeedAttention = "attention";
    public const string NotOnALine = "not_on_a_line";

    /// <summary>
    /// A projection that starts later than its baseline by less than this is not "behind": an
    /// unstarted job is projected from now, so a few minutes' drift after dispatch is normal.
    /// </summary>
    public static readonly TimeSpan SlipTolerance = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<WorkOrderTile> All { get; } =
    [
        new(StartingToday, "Starting today", "accent", "Open orders whose first job is projected to start today and isn't running yet."),
        new(FinishingToday, "Finishing today", "accent", "Open orders whose last job is projected to finish today."),
        new(OnTrack, "On track", "success", "Open orders on a line, projected to finish by their due date and start when published."),
        new(Behind, "Behind", "danger", "Projected to finish after the due date, or to start more than 15 minutes after the published baseline."),
        new(NeedAttention, "Need attention", "warning", "A job has a schedule warning or overlaps other work."),
        new(NotOnALine, "Not on a line", "muted", "Planned orders with quantity not yet on a line or department."),
    ];

    public static bool IsOpen(WorkOrderProgressDto p) => p.Order.Status is "planned" or "dispatched" or "firmed" or "in_progress";

    public static bool IsBehind(WorkOrderProgressDto p) =>
        IsOpen(p) && (p.FinishSlip > TimeSpan.Zero || p.StartSlip > SlipTolerance);

    /// <summary>The tiles an order counts in.</summary>
    public static IReadOnlySet<string> Of(WorkOrderProgressDto p, DateTimeOffset now, string? timeZone)
    {
        var tiles = new HashSet<string>();
        if (!IsOpen(p))
        {
            return tiles;
        }

        var today = Day(now, timeZone);
        if (p.ProjectedStart is { } start && Day(start, timeZone) == today && !p.IsRunning)
        {
            tiles.Add(StartingToday);
        }

        if (p.ProjectedEnd is { } end && Day(end, timeZone) == today)
        {
            tiles.Add(FinishingToday);
        }

        if (IsBehind(p))
        {
            tiles.Add(Behind);
        }
        else if (p.Jobs.Count > 0)
        {
            tiles.Add(OnTrack);
        }

        if (p.Warnings.Count > 0 || p.HasOverlap)
        {
            tiles.Add(NeedAttention);
        }

        if (p.Order.Status == "planned" && p.PlacedQty < p.Order.PlannedQty)
        {
            tiles.Add(NotOnALine);
        }

        return tiles;
    }

    private static DateOnly Day(DateTimeOffset at, string? timeZone) => DateOnly.FromDateTime(Display.ToSite(at, timeZone).DateTime);
}
