using System.Globalization;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>Labels and layout for the Machines screen (D-041).</summary>
public static class MachineView
{
    /// <summary>The label of an OEE category (D-005).</summary>
    public static string CategoryLabel(string? category) => category switch
    {
        "producing" => "Producing",
        "waiting_external" => "Waiting",
        "held_internal" => "Held",
        "stopped_faulted" => "Stopped",
        "ready_transitioning" => "Ready",
        "no_signal" => "No signal",
        _ => "Not reporting",
    };

    /// <summary>The CSS modifier colouring a tile for its category, matching the design system's state tokens.</summary>
    public static string CategoryClass(string? category) => category switch
    {
        "producing" => "is-producing",
        "waiting_external" => "is-waiting",
        "held_internal" => "is-held",
        "stopped_faulted" => "is-stopped",
        "ready_transitioning" => "is-ready",
        _ => "is-nosignal",
    };

    /// <summary>A length of time as people say it: <c>45 s</c>, <c>12 min</c>, <c>3 h 05 min</c>, <c>2 d 4 h</c>.</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
            : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes:00} min"
            : $"{(int)span.TotalDays} d {span.Hours} h";
    }

    public static string UnitModeLabel(int? mode) => mode switch
    {
        null or 1 => "Production",
        2 => "Maintenance",
        3 => "Manual",
        _ => $"Mode {mode}",
    };

    /// <summary>Equipment grouped for the board: each top tile (nothing above it tracks state) with the tiles below it, in tree order.</summary>
    public static IReadOnlyList<(MachineStatusDto Head, IReadOnlyList<MachineStatusDto> Below)> Groups(IReadOnlyList<MachineStatusDto> machines)
    {
        var ids = machines.Select(m => m.EquipmentId).ToHashSet();
        var children = machines.Where(m => m.ParentId is { } p && ids.Contains(p)).ToLookup(m => m.ParentId!.Value);
        List<MachineStatusDto> Descend(Guid id) => [.. children[id].SelectMany(c => (IEnumerable<MachineStatusDto>)[c, .. Descend(c.EquipmentId)])];
        return [.. machines.Where(m => m.ParentId is not { } p || !ids.Contains(p)).Select(m => (m, (IReadOnlyList<MachineStatusDto>)Descend(m.EquipmentId)))];
    }

    /// <summary>How a line's state was worked out, for its tile.</summary>
    public static string? DerivationNote(MachineStatusDto machine) => machine.StateDerivation switch
    {
        "series" => "Worst of its machines",
        "parallel" => "Best of its machines",
        "constraint_machine" => "Its constraint machine",
        _ => null,
    };

    public static string Count(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>A stop's reason as the list shows it: the operator's, else the mapped one, else none.</summary>
    public static string ReasonText(DowntimeDto stop) =>
        stop.OperatorReasonCode is { } op ? $"{op} · {stop.OperatorReasonName}"
        : stop.MappedReasonCode is { } mapped ? $"{mapped} · {stop.MappedReasonName}"
        : "";
}
