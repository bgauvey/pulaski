using System.Globalization;
using Pulaski.Contracts.Execution;

namespace Pulaski.Web.Services.Execution;

/// <summary>What the floor and job screens show and offer (D-035), kept apart from the markup so it can be tested.</summary>
public static class FloorView
{
    /// <summary>How often the floor board re-reads the lines.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(15);

    /// <summary>"1 h 05 min" since the run started.</summary>
    public static string Elapsed(DateTimeOffset since, DateTimeOffset now)
    {
        var span = now - since;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min" : $"{(int)span.TotalMinutes} min";
    }

    /// <summary>How much more the run can take before the rate cap; null when there is no cap.</summary>
    public static decimal? RoomLeft(decimal? cap, decimal good, decimal scrap) =>
        cap is { } c ? Math.Max(0m, c - good - scrap) : null;

    /// <summary>Percent of the cap used, 0 to 100, for the bar; null without a cap.</summary>
    public static int? CapPercent(decimal? cap, decimal good, decimal scrap) =>
        cap is { } c && c > 0 ? (int)Math.Min(100m, Math.Round((good + scrap) / c * 100m)) : null;

    /// <summary>Percent of the planned quantity made so far, 0 to 100.</summary>
    public static int Progress(decimal? planned, decimal good) =>
        planned is { } p && p > 0 ? (int)Math.Min(100m, Math.Round(good / p * 100m)) : 0;

    /// <summary>The run a report goes into by default: the open one, or else the latest.</summary>
    public static Guid? DefaultRun(IReadOnlyList<JobRunDto> runs) =>
        runs.FirstOrDefault(r => r.EndedAt is null)?.Id ?? (runs.Count > 0 ? runs[^1].Id : null);

    /// <summary>Whether a job on the board can be started now: it is ready and its line is free (a department's job always needs a line chosen).</summary>
    public static bool CanStart(BoardJobDto job, LineBoardRowDto line) => job.IsReady && line.Running is null;

    /// <summary>The actions a job's page offers for its status.</summary>
    public static JobActions ActionsFor(JobExecutionDto job) => new(
        CanStart: job.Status is "paused" || (job.IsReady && (job.JobType == "production"
            ? job.Status is "dispatched" or "firmed"
            : job.Status is "planned" or "dispatched" or "firmed")),
        CanPause: job.Status == "running",
        CanComplete: job.Status is "running" or "paused",
        CanReopen: job.Status == "completed" && job.WorkOrderStatus != "closed",
        CanReport: job.JobType == "production" && job.Status is ("running" or "paused" or "completed") && job.Runs.Count > 0);

    public static string Qty(decimal? value) =>
        value is { } v ? Math.Round(v, 3).ToString("#,0.###", CultureInfo.InvariantCulture) : "—";
}

/// <summary>What can be done with a job right now.</summary>
public sealed record JobActions(bool CanStart, bool CanPause, bool CanComplete, bool CanReopen, bool CanReport);
