using System.Globalization;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>A process value tag's readings in a period, summed up (#26): the latest, the lowest, the highest and the time-weighted mean.</summary>
public sealed record ProcessValueSummary(int Readings, int Lost, ProcessValuePointDto? Latest, double? Min, double? Max, double? Mean);

/// <summary>How the Machines screen shows process values (#26).</summary>
public static class ProcessValueView
{
    /// <summary>
    /// The summary of <paramref name="points"/> (oldest first) up to <paramref name="until"/>. The mean weighs each good reading by how long it
    /// held, till the next reading or <paramref name="until"/>, since readings come on change, not at a steady rate.
    /// </summary>
    public static ProcessValueSummary Summarize(IReadOnlyList<ProcessValuePointDto> points, DateTimeOffset until)
    {
        var good = points.Where(p => p.Good && p.Value is not null).ToList();
        double weighted = 0, held = 0;
        for (var i = 0; i < points.Count; i++)
        {
            if (points[i] is { Good: true, Value: { } value } point)
            {
                var end = i + 1 < points.Count ? points[i + 1].At : until;
                var seconds = Math.Max(0, (end - point.At).TotalSeconds);
                weighted += value * seconds;
                held += seconds;
            }
        }

        return new ProcessValueSummary(
            points.Count,
            points.Count - good.Count,
            points.Count > 0 ? points[^1] : null,
            good.Count > 0 ? good.Min(p => p.Value!.Value) : null,
            good.Count > 0 ? good.Max(p => p.Value!.Value) : null,
            held > 0 ? weighted / held : good.Count > 0 ? good.Average(p => p.Value!.Value) : null);
    }

    /// <summary>A reading for the screen: up to four significant decimals, a dash for none.</summary>
    public static string Value(double? value) => value is { } v ? v.ToString("0.####", CultureInfo.InvariantCulture) : "—";
}
