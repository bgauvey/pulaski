using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>Labels and the CSV export for the machine profile screens (D-050).</summary>
public static class MachineProfileView
{
    public const string State = "state";

    /// <summary>The columns of a profile file, as the import reads them.</summary>
    public static IReadOnlyList<string> CsvColumns { get; } = ["kind", "value", "name", "packml_state", "category", "reason"];

    public static IReadOnlyList<string> CodeKinds { get; } = ["stop_reason", "alarm", "warning"];

    /// <summary>The PackML states by number (D-005), for choosing what a state value means.</summary>
    public static IReadOnlyList<string> PackMlNames { get; } =
    [
        "Undefined", "Clearing", "Stopped", "Starting", "Idle", "Suspended", "Execute", "Stopping", "Aborting",
        "Aborted", "Holding", "Held", "Unholding", "Suspending", "Unsuspending", "Resetting", "Completing", "Complete",
    ];

    public static string KindLabel(string kind) => kind switch
    {
        State => "State",
        "stop_reason" => "Stop reason",
        "alarm" => "Alarm",
        "warning" => "Warning",
        _ => kind,
    };

    public static string ClassLabel(string? utilizationClass) => utilizationClass switch
    {
        "runtime" => "Runtime",
        "downtime" => "Downtime",
        "planned" => "Planned",
        _ => "",
    };

    /// <summary>A reason as a choice shows it: <c>JAM · Jam (downtime)</c>.</summary>
    public static string ReasonLabel(ReasonCodeDto reason) => $"{reason.Code} · {reason.Name} ({ClassLabel(reason.UtilizationClass).ToLowerInvariant()})";

    /// <summary>The profile's active states and codes as a file the import reads back unchanged.</summary>
    public static string ExportCsv(MachineProfileDetailDto profile) =>
        Csv.Write(CsvColumns,
        [
            .. profile.States.Where(s => s.IsActive)
                .Select(s => (IReadOnlyList<object?>)[State, s.RawValue, s.Name, s.PackMlName, null, s.ReasonCode]),
            .. profile.Codes.Where(c => c.IsActive)
                .Select(c => (IReadOnlyList<object?>)[c.Kind, c.RawValue, c.Message, null, c.Category, c.ReasonCode]),
        ]);
}
