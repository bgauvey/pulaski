using System.Globalization;
using Pulaski.Contracts.Execution;

namespace Pulaski.Web.Services.Execution;

/// <summary>How the production-by-shift report groups and labels its rows (D-044).</summary>
public static class ProductionByShiftView
{
    /// <summary>One shift (or a day's time outside shifts) with its rows and its totals per unit.</summary>
    public sealed record ShiftGroup(
        DateOnly ProductionDate, string? ScheduleCode, string? ShiftCode, string Label, IReadOnlyList<ShiftProductionRowDto> Rows,
        IReadOnlyList<(string UomCode, decimal GoodQty, decimal ScrapQty)> Totals);

    public static IReadOnlyList<ShiftGroup> Groups(IReadOnlyList<ShiftProductionRowDto> rows) =>
        [.. rows.GroupBy(r => (r.ProductionDate, r.ScheduleCode, r.ShiftCode, r.ShiftStart))
            .Select(g => new ShiftGroup(g.Key.ProductionDate, g.Key.ScheduleCode, g.Key.ShiftCode, Label(g.First()), [.. g],
                [.. g.GroupBy(r => r.UomCode).OrderBy(u => u.Key, StringComparer.Ordinal).Select(u => (u.Key, u.Sum(r => r.GoodQty), u.Sum(r => r.ScrapQty)))]))];

    /// <summary>"Mon 19 Oct · Nights (N, 2X12) 18:00–06:00 · RED", or "Mon 19 Oct · outside shifts".</summary>
    public static string Label(ShiftProductionRowDto row)
    {
        var day = row.ProductionDate.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture);
        if (row.ShiftCode is not { } code)
        {
            return $"{day} · outside shifts";
        }

        var times = row.ShiftStart is { } start && row.ShiftEnd is { } end
            ? $" {start.ToString("HH:mm", CultureInfo.InvariantCulture)}–{end.ToString("HH:mm", CultureInfo.InvariantCulture)}{(end.Date > start.Date ? " +1" : "")}"
            : "";
        return $"{day} · {row.ShiftName ?? code} ({code}{(row.ScheduleCode is { } s ? $", {s}" : "")}){times}{(row.Crew is { } crew ? $" · {crew}" : "")}";
    }

    public static string Qty(decimal value) => value.ToString("#,0.###", CultureInfo.InvariantCulture);
}
