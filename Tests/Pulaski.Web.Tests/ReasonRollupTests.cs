using Pulaski.Contracts.MachineData;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

/// <summary>Rolling the OEE report's reasons up to a group level (#86).</summary>
public class ReasonRollupTests
{
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly ReasonGroupDto Mechanical = new(Guid.CreateVersion7(), "MECH", "Mechanical");
    private static readonly ReasonGroupDto Feed = new(Guid.CreateVersion7(), "FEED", "Feed");

    private static UtilizationRowDto Down(string code, decimal hours, int stops, params ReasonGroupDto[] groups) => new()
    {
        EquipmentId = Line,
        EquipmentCode = "L1",
        Use = "down",
        ReasonCodeId = Guid.CreateVersion7(),
        ReasonCode = code,
        ReasonName = code,
        Hours = hours,
        Stops = stops,
        SharePct = hours * 10m,
        ReasonGroups = groups,
    };

    private static OeeReportDto Report() => new()
    {
        Equipment = [new() { EquipmentId = Line, Code = "L1", Name = "L1", Depth = 0, Figures = new OeeFiguresDto() }],
        Utilization =
        [
            Down("JAM", 2m, 3, Mechanical, Feed),
            Down("BELT", 1m, 1, Mechanical, Feed),
            Down("MOTOR", 1.5m, 1, Mechanical),
            Down("POWER", 0.5m, 1),
            new() { EquipmentId = Line, EquipmentCode = "L1", Use = "down", Hours = 0.25m, Stops = 2 },
        ],
    };

    [Fact]
    public void Reasons_add_up_to_the_group_at_the_level_chosen_and_shallower_ones_stay_themselves()
    {
        var report = Report();
        Assert.Equal(2, ReasonRollup.Levels(report));
        Assert.Equal([(0, "Each reason"), (1, "Top groups"), (2, "Groups, level 2")], ReasonRollup.Choices(report));

        var top = ReasonRollup.Apply(report.Utilization, 1);
        Assert.Equal([("MECH", 4.5m, 5), ("POWER", 0.5m, 1), (null, 0.25m, 2)], top.Select(r => (r.ReasonCode, r.Hours, r.Stops)));

        var second = ReasonRollup.Apply(report.Utilization, 2);
        Assert.Equal([("FEED", 3m), ("MOTOR", 1.5m), ("POWER", 0.5m), (null, 0.25m)], second.Select(r => (r.ReasonCode, r.Hours)));
        Assert.Equal(["MECH"], second[0].ReasonGroups.Select(g => g.Code));
        Assert.Equal(5, ReasonRollup.Apply(report.Utilization, 0).Count);
    }

    [Fact]
    public void The_pareto_ranks_the_groups_when_rolled_up()
    {
        var rows = StopPareto.Rows(Report(), false, false, false, level: 1);

        Assert.Equal(["MECH · Mechanical", "POWER · POWER", "Uncoded"], rows.Select(r => r.Label));
        Assert.Equal(100m, rows[^1].CumulativePct);
    }
}
