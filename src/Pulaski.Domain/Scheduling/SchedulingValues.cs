namespace Pulaski.Domain.Scheduling;

/// <summary>Job types (D-009). Values match <c>core.job.job_type</c>.</summary>
public static class JobType
{
    public const string Production = "production";
    public const string Changeover = "changeover";
    public const string Cleaning = "cleaning";
    public const string Trial = "trial";
    public const string Maintenance = "maintenance";

    public static IReadOnlyList<string> All { get; } = [Production, Changeover, Cleaning, Trial, Maintenance];
}

/// <summary>Job lifecycle values. Values match <c>core.job.status</c>.</summary>
public static class JobStatus
{
    public const string Planned = "planned";
    public const string Scheduled = "scheduled";
    public const string Dispatched = "dispatched";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Completed = "completed";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    /// <summary>Statuses with work still ahead of them; these are the jobs on a line's timeline.</summary>
    public static IReadOnlyList<string> Open { get; } = [Planned, Scheduled, Dispatched, Running, Paused];

    /// <summary>Jobs the planner may still move or dispatch.</summary>
    public static IReadOnlyList<string> Resequenceable { get; } = [Planned, Scheduled];

    /// <summary>Jobs that have not started, so publish may (re)baseline them.</summary>
    public static IReadOnlyList<string> Publishable { get; } = [Planned, Scheduled, Dispatched];
}

/// <summary>How changeovers get onto the timeline (D-015). Values match <c>core.scheduling_policy.changeover_mode</c>.</summary>
public static class ChangeoverMode
{
    /// <summary>Pulaski inserts a placeholder wherever the material changes.</summary>
    public const string Auto = "auto";

    /// <summary>Planners place changeover jobs; Pulaski warns where one is missing.</summary>
    public const string Scheduler = "scheduler";

    public static IReadOnlyList<string> All { get; } = [Auto, Scheduler];
}

/// <summary>Which rate a run standard is using (D-016). Values match <c>core.run_standard.rate_source</c>.</summary>
public static class RateSource
{
    public const string Calculated = "calculated";
    public const string Modified = "modified";
}

/// <summary>How a calculated rate was derived. Values match <c>core.run_standard.calculation_basis</c>.</summary>
public static class CalculationBasis
{
    public const string IdealRate = "ideal_rate";
    public const string RunHistory = "run_history";

    public static IReadOnlyList<string> All { get; } = [IdealRate, RunHistory];
}
