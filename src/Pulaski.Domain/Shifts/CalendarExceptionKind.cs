namespace Pulaski.Domain.Shifts;

/// <summary>
/// Why time is taken out of the calendar (D-043). Values match <c>core.calendar_exception.kind</c>. The kind is a label:
/// every kind removes the time from planning alike.
/// </summary>
public static class CalendarExceptionKind
{
    public const string Holiday = "holiday";
    public const string NoDemand = "no_demand";
    public const string PlannedShutdown = "planned_shutdown";
    public const string Other = "other";

    public static IReadOnlyList<string> All { get; } = [Holiday, NoDemand, PlannedShutdown, Other];

    /// <summary>The longest window one exception may take out, so a typing slip can't close a line for years.</summary>
    public static readonly TimeSpan MaxLength = TimeSpan.FromDays(366);

    public const int MaxNoteLength = 500;
}
