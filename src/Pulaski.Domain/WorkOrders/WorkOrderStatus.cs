namespace Pulaski.Domain.WorkOrders;

/// <summary>
/// Work order lifecycle (D-003, D-018): plan, dispatch (publish the baseline), firm (frozen in
/// time), execute, complete. Values match <c>core.work_order.status</c>.
/// </summary>
public static class WorkOrderStatus
{
    public const string Planned = "planned";
    public const string Dispatched = "dispatched";
    public const string Firmed = "firmed";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
        [Planned, Dispatched, Firmed, InProgress, Completed, Closed, Cancelled];

    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        [Planned] = [Dispatched, Cancelled],
        [Dispatched] = [Planned, Firmed, Cancelled],
        [Firmed] = [InProgress, Cancelled],
        [InProgress] = [Completed],
        [Completed] = [InProgress, Closed],
        [Closed] = [],
        [Cancelled] = [],
    };

    /// <summary>Statuses a work order may move to from <paramref name="current"/>.</summary>
    public static IReadOnlyList<string> NextStatuses(string current) =>
        Transitions.TryGetValue(current, out var next) ? next : [];

    public static bool CanTransition(string from, string to) => NextStatuses(from).Contains(to);

    /// <summary>
    /// Dispatch, un-dispatch and firm also change the order's jobs, so they have their own
    /// commands; a plain status change may not make them.
    /// </summary>
    public static bool IsPlanningTransition(string from, string to) =>
        (from, to) is (Planned, Dispatched) or (Dispatched, Planned) or (Dispatched, Firmed);

    /// <exception cref="DomainRuleException">The transition is not allowed.</exception>
    public static void EnsureCanTransition(string from, string to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainRuleException($"A work order cannot move from '{from}' to '{to}'.");
        }
    }
}
