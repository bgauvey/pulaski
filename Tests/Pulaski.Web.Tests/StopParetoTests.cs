using Pulaski.Contracts.MachineData;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

/// <summary>The stop Pareto (#21) from the OEE report's rows.</summary>
public class StopParetoTests
{
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid Filler = Guid.CreateVersion7();
    private static readonly Guid Saw = Guid.CreateVersion7();
    private static readonly Guid Jam = Guid.CreateVersion7();
    private static readonly Guid Cleaning = Guid.CreateVersion7();

    private static OeeEquipmentRowDto Equipment(Guid id, string code, int depth) =>
        new() { EquipmentId = id, Code = code, Name = code, Depth = depth, Figures = new OeeFiguresDto() };

    private static UtilizationRowDto Use(Guid equipment, string code, string use, decimal hours, int stops, Guid? reason = null, string? reasonCode = null, bool noSignal = false) =>
        new() { EquipmentId = equipment, EquipmentCode = code, Use = use, Hours = hours, Stops = stops, ReasonCodeId = reason, ReasonCode = reasonCode, ReasonName = reasonCode, NoSignal = noSignal };

    // A line made of a filler (below it, so not counted again) and a saw on its own.
    private static OeeReportDto Report() => new()
    {
        Equipment = [Equipment(Line, "L1", 0), Equipment(Filler, "F1", 1), Equipment(Saw, "S1", 0)],
        Utilization =
        [
            Use(Line, "L1", "run", 30m, 0),
            Use(Line, "L1", "down", 3m, 4, Jam, "JAM"),
            Use(Line, "L1", "down", 1m, 2),
            Use(Line, "L1", "planned", 2m, 1, Cleaning, "CLEAN"),
            Use(Line, "L1", "micro_stop", 0.5m, 9),
            Use(Filler, "F1", "down", 3m, 4, Jam, "JAM"),
            Use(Saw, "S1", "down", 1m, 1, Jam, "JAM"),
            Use(Saw, "S1", "down", 2m, 1, noSignal: true),
        ],
        Shifts =
        [
            new() { EquipmentId = Line, EquipmentCode = "L1", ShiftCode = "B", ShiftName = "Back", Figures = new OeeFiguresDto() },
            new() { EquipmentId = Line, EquipmentCode = "L1", ShiftCode = "A", ShiftName = "Early", Figures = new OeeFiguresDto() },
            new() { EquipmentId = Saw, EquipmentCode = "S1", ShiftCode = "A", ShiftName = "Early", Figures = new OeeFiguresDto() },
        ],
    };

    [Fact]
    public void Down_time_is_ranked_by_hours_across_the_top_equipment_with_uncoded_and_no_signal_kept()
    {
        var rows = StopPareto.Rows(Report(), includePlanned: false, includeMicroStops: false, byStops: false);

        Assert.Equal(["JAM · JAM", "No signal", "Uncoded"], rows.Select(r => r.Label));
        Assert.Equal((4m, 5), (rows[0].Hours, rows[0].Stops));
        Assert.Equal(["L1", "S1"], rows[0].EquipmentCodes);
        Assert.Equal([(57.1m, 57.1m), (28.6m, 85.7m), (14.3m, 100m)], rows.Select(r => (r.SharePct, r.CumulativePct)));
    }

    [Fact]
    public void Planned_stops_and_micro_stops_join_when_asked_and_stops_can_rank_instead_of_hours()
    {
        var rows = StopPareto.Rows(Report(), includePlanned: true, includeMicroStops: true, byStops: true);

        // No signal and cleaning tie on stops and hours: a named reason comes first.
        Assert.Equal(["Micro-stops", "JAM · JAM", "Uncoded", "CLEAN · CLEAN", "No signal"], rows.Select(r => r.Label));
        Assert.Equal(100m, rows[^1].CumulativePct);
        Assert.Equal([("A", "Early"), ("B", "Back")], StopPareto.Shifts(Report()).Select(s => (s.Code, s.Name)));
        Assert.Empty(StopPareto.Rows(new OeeReportDto(), true, true, false));
    }
}
