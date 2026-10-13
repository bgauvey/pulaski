namespace Pulaski.Domain.WorkOrders;

/// <summary>Work order lifecycle (D-003). Values match <c>core.work_order.status</c>.</summary>
public static class WorkOrderStatus
{
    public const string Planned = "planned";
    public const string Released = "released";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
    public const string Closed = "closed";
    public const string Cancelled = "cancelled";

    public static IReadOnlyList<string> All { get; } =
        [Planned, Released, InProgress, Completed, Closed, Cancelled];

    private static readonly Dictionary<string, string[]> Transitions = new(StringComparer.Ordinal)
    {
        [Planned] = [Released, Cancelled],
        [Released] = [Planned, InProgress, Cancelled],
        [InProgress] = [Completed],
        [Completed] = [InProgress, Closed],
        [Closed] = [],
        [Cancelled] = [],
    };

    /// <summary>Statuses a work order may move to from <paramref name="current"/>.</summary>
    public static IReadOnlyList<string> NextStatuses(string current) =>
        Transitions.TryGetValue(current, out var next) ? next : [];

    public static bool CanTransition(string from, string to) => NextStatuses(from).Contains(to);

    /// <exception cref="DomainRuleException">The transition is not allowed.</exception>
    public static void EnsureCanTransition(string from, string to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainRuleException($"A work order cannot move from '{from}' to '{to}'.");
        }
    }
}
