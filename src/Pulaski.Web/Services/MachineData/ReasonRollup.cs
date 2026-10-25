using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>
/// Rolls the OEE report's reason rows up to a group level (#86): level 1 totals by top group, 2 by the groups below those, and so on; 0
/// keeps each reason. A reason filed less deep than the level stays as itself; time without a reason (uncoded, no signal) is never rolled
/// up. Rows of one equipment and use with the same group add up.
/// </summary>
public static class ReasonRollup
{
    /// <summary>How many group levels the report's reasons are filed under, at most.</summary>
    public static int Levels(OeeReportDto report) => report.Utilization.Select(u => u.ReasonGroups.Count).DefaultIfEmpty(0).Max();

    public static IReadOnlyList<UtilizationRowDto> Apply(IEnumerable<UtilizationRowDto> rows, int level)
    {
        if (level <= 0)
        {
            return [.. rows];
        }

        return [.. rows
            .Select(u => u.ReasonGroups.Count >= level && u.ReasonGroups[level - 1] is var g
                ? u with { ReasonCodeId = g.Id, ReasonCode = g.Code, ReasonName = g.Name, ReasonGroups = [.. u.ReasonGroups.Take(level - 1)] }
                : u)
            .GroupBy(u => (u.EquipmentId, u.Use, u.ReasonCodeId, NoSignal: u.ReasonCodeId is null && u.NoSignal))
            .Select(g => g.First() with { Hours = g.Sum(u => u.Hours), SharePct = g.Sum(u => u.SharePct), Stops = g.Sum(u => u.Stops) })
            .OrderByDescending(u => u.Hours)];
    }

    /// <summary>The choices: each reason, then each group level the report has.</summary>
    public static IReadOnlyList<(int Level, string Label)> Choices(OeeReportDto report) =>
        [(0, "Each reason"), .. Enumerable.Range(1, Levels(report)).Select(l => (l, l == 1 ? "Top groups" : $"Groups, level {l}"))];
}
