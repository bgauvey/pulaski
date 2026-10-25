using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>One reason in the stop Pareto (#21): its time and stops, its share of the whole and the running total, most first.</summary>
public sealed record ParetoRow(
    string Use, string? ReasonCode, string? ReasonName, bool NoSignal, decimal Hours, int Stops, IReadOnlyList<string> EquipmentCodes, decimal SharePct, decimal CumulativePct)
{
    /// <summary>What the row is, as the table names it: the reason, else Uncoded, No signal or Micro-stops.</summary>
    public string Label => ReasonCode is not null ? $"{ReasonCode} · {ReasonName}"
        : Use == "micro_stop" ? "Micro-stops"
        : NoSignal ? "No signal"
        : "Uncoded";
}

/// <summary>
/// The stop Pareto (#21), from the OEE report's "where the time went" rows, so it classifies time exactly as the report does and follows
/// its period, equipment and shift. It adds up the top equipment in the report (a line, not also each of its machines, whose time it is
/// made of), so nothing counts twice. Down time always; planned stops and micro-stops when asked.
/// </summary>
public static class StopPareto
{
    public static IReadOnlyList<ParetoRow> Rows(OeeReportDto report, bool includePlanned, bool includeMicroStops, bool byStops)
    {
        var tops = report.Equipment.Where(e => e.Depth == 0).Select(e => e.EquipmentId).ToHashSet();
        var grouped = report.Utilization
            .Where(u => tops.Contains(u.EquipmentId) && (u.Use == "down" || (includePlanned && u.Use == "planned") || (includeMicroStops && u.Use == "micro_stop")))
            .GroupBy(u => (u.Use, u.ReasonCodeId, NoSignal: u.ReasonCodeId is null && u.NoSignal))
            .Select(g => (
                g.Key.Use, g.First().ReasonCode, g.First().ReasonName, g.Key.NoSignal,
                Hours: g.Sum(u => u.Hours), Stops: g.Sum(u => u.Stops),
                Equipment: (IReadOnlyList<string>)[.. g.Select(u => u.EquipmentCode).Distinct().Order(StringComparer.Ordinal)]))
            .Where(r => r.Hours > 0 || r.Stops > 0)
            .OrderByDescending(r => byStops ? r.Stops : r.Hours)
            .ThenByDescending(r => byStops ? r.Hours : r.Stops)
            .ThenBy(r => r.ReasonCode ?? "￿", StringComparer.Ordinal)
            .ToList();

        decimal Measure((string Use, string? ReasonCode, string? ReasonName, bool NoSignal, decimal Hours, int Stops, IReadOnlyList<string> Equipment) r) =>
            byStops ? r.Stops : r.Hours;
        var total = grouped.Sum(Measure);
        var running = 0m;
        var rows = new List<ParetoRow>();
        foreach (var r in grouped)
        {
            running += Measure(r);
            rows.Add(new ParetoRow(r.Use, r.ReasonCode, r.ReasonName, r.NoSignal, r.Hours, r.Stops, r.Equipment,
                total > 0 ? Math.Round(Measure(r) / total * 100m, 1) : 0m,
                total > 0 ? Math.Round(running / total * 100m, 1) : 0m));
        }

        return rows;
    }

    /// <summary>The shifts the report's rows name, by code, for choosing one; none for equipment without shifts.</summary>
    public static IReadOnlyList<(string Code, string? Name)> Shifts(OeeReportDto report) =>
        [.. report.Shifts.Where(s => s.ShiftCode is not null)
            .GroupBy(s => s.ShiftCode!)
            .Select(g => (g.Key, g.First().ShiftName))
            .OrderBy(s => s.Key, StringComparer.Ordinal)];
}
