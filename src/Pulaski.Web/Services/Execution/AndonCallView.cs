using Pulaski.Contracts.Andon;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Execution;

/// <summary>How a call for help reads on the station, the line boards and the Andon page (#157, #158).</summary>
public static class AndonCallView
{
    public static string Title(Text l, int count) => count == 1 ? l["A call for help"] : l["Calls for help: {0}", count];

    /// <summary>
    /// Where a call stands: "waiting 4 min", "waiting 12 min: escalated" while nobody has come, "Lee Ong is on it" once acknowledged (the
    /// user's name, else who history says).
    /// </summary>
    public static string State(Text l, AndonCallDto call, DateTimeOffset now) => call.AcknowledgedBy is { } who
        ? l["{0} is on it", call.AcknowledgedByName ?? who]
        : call.IsEscalated
            ? l["waiting {0}: escalated", LineDisplayView.Duration(call.RaisedAt, now)]
            : l["waiting {0}", LineDisplayView.Duration(call.RaisedAt, now)];

    /// <summary>Who called and from where: "Dana Operator at ST-EXT01", or just who.</summary>
    public static string Caller(Text l, AndonCallDto call) =>
        call.RaisedFrom is { } from ? l["{0} at {1}", call.RaisedByName ?? call.RaisedBy, from] : call.RaisedByName ?? call.RaisedBy;

    /// <summary>The time a resolved call took to answer and to resolve: "answered in 3 min, resolved in 25 min".</summary>
    public static string? Took(Text l, AndonCallDto call) => call is { AcknowledgedAt: { } ack, ResolvedAt: { } done }
        ? l["answered in {0}, resolved in {1}", LineDisplayView.Duration(call.RaisedAt, ack), LineDisplayView.Duration(call.RaisedAt, done)]
        : null;

    /// <summary>A time in seconds as a duration: "45 s", "12 min", "1 h 05 min"; "—" when there is none.</summary>
    public static string Time(int? seconds) => seconds is { } s ? LineDisplayView.Duration(DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(s)) ?? "—" : "—";

    /// <summary>The periods the response times offer, in days.</summary>
    public static readonly IReadOnlyList<int> Periods = [7, 30, 90];
}
