using Pulaski.Contracts.Maintenance;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.MachineData;

/// <summary>Words for maintenance requests (#127, D-115).</summary>
public static class MaintenanceRequestView
{
    /// <summary>The priorities in the order offered, with their labels.</summary>
    public static IReadOnlyList<(string Code, string Label)> Priorities { get; } =
    [
        ("normal", Term.Mark("Normal")),
        ("high", Term.Mark("High")),
        ("low", Term.Mark("Low")),
    ];

    /// <summary>"Waiting 25 min", "In progress · Lee Ong", "Closed".</summary>
    public static string State(Text l, MaintenanceRequestDto r) => r.Status switch
    {
        "open" => l["Waiting {0}", MachineView.Duration(TimeSpan.FromSeconds(r.WaitedSeconds))],
        "in_progress" => l["In progress · {0}", r.StartedByName ?? r.StartedBy ?? ""],
        _ => l["Closed"],
    };

    /// <summary>The chip's colour: high priority waiting is a warning, waiting a notice, in progress fine.</summary>
    public static string Chip(MaintenanceRequestDto r) => r.Status switch
    {
        "open" when r.Priority == "high" => "chip-danger",
        "open" => "chip-warn",
        "in_progress" => "chip-ok",
        _ => "",
    };

    /// <summary>"Lee Ong" or the actor, and the station when raised there.</summary>
    public static string Raiser(Text l, MaintenanceRequestDto r) =>
        r.RaisedFrom is { } station ? l["{0} at {1}", r.RaisedByName ?? r.RaisedBy, station] : r.RaisedByName ?? r.RaisedBy;

    /// <summary>"From the stop at 14:02 (Jam · Material jam)" when raised from a stop; null otherwise.</summary>
    public static string? Stop(Text l, MaintenanceRequestDto r, string? timeZone) => r.DowntimeStartedAt is { } started
        ? r.DowntimeReason is { } reason
            ? l["From the stop at {0} ({1})", Display.SiteTime(started, timeZone), reason]
            : l["From the stop at {0}", Display.SiteTime(started, timeZone)]
        : null;

    /// <summary>"Took 2 h 10 min" from raised to closed.</summary>
    public static string Took(Text l, MaintenanceRequestDto r) => l["Took {0}", MachineView.Duration(TimeSpan.FromSeconds(r.AgeSeconds))];
}
