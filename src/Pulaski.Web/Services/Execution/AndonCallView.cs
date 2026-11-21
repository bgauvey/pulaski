using Pulaski.Contracts.Andon;

namespace Pulaski.Web.Services.Execution;

/// <summary>How a call for help reads on the station, the line boards and the Andon page (#157, #158).</summary>
public static class AndonCallView
{
    public static string Title(int count) => count == 1 ? "A call for help" : $"{count} calls for help";

    /// <summary>
    /// Where a call stands: "waiting 4 min", "waiting 12 min: escalated" while nobody has come, "Lee Ong is on it" once acknowledged (the
    /// user's name, else who history says).
    /// </summary>
    public static string State(AndonCallDto call, DateTimeOffset now) => call.AcknowledgedBy is { } who
        ? $"{call.AcknowledgedByName ?? who} is on it"
        : $"waiting {LineDisplayView.Duration(call.RaisedAt, now)}{(call.IsEscalated ? ": escalated" : "")}";

    /// <summary>Who called and from where: "Dana Operator at ST-EXT01", or just who.</summary>
    public static string Caller(AndonCallDto call) =>
        (call.RaisedByName ?? call.RaisedBy) + (call.RaisedFrom is { } from ? $" at {from}" : "");

    /// <summary>The time a resolved call took to answer and to resolve: "answered in 3 min, resolved in 25 min".</summary>
    public static string? Took(AndonCallDto call) => call is { AcknowledgedAt: { } ack, ResolvedAt: { } done }
        ? $"answered in {LineDisplayView.Duration(call.RaisedAt, ack)}, resolved in {LineDisplayView.Duration(call.RaisedAt, done)}"
        : null;
}
