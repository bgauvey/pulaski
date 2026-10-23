using Pulaski.Contracts.MachineData;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

/// <summary>The Machines screen's helpers (D-050).</summary>
public class MachineViewTests
{
    private static MachineStatusDto M(string code, Guid? parent = null) =>
        new() { EquipmentId = Guid.CreateVersion7(), Code = code, Name = code, ParentId = parent, EquipmentRole = "machine" };

    [Theory]
    [InlineData(45, "45 s")]
    [InlineData(12 * 60, "12 min")]
    [InlineData(3 * 3600 + 5 * 60, "3 h 05 min")]
    [InlineData(2 * 86400 + 4 * 3600, "2 d 4 h")]
    [InlineData(-5, "0 s")]
    public void Durations_read_as_people_say_them(int seconds, string expected) => Assert.Equal(expected, MachineView.Duration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_board_groups_each_line_with_everything_below_it()
    {
        var line = M("LINE");
        var filler = M("FILL", line.EquipmentId);
        var head = M("HEAD", filler.EquipmentId);
        var alone = M("ALONE");
        var orphan = M("ORPHAN", Guid.CreateVersion7());

        var groups = MachineView.Groups([line, filler, head, alone, orphan]);

        Assert.Equal(["LINE", "ALONE", "ORPHAN"], groups.Select(g => g.Head.Code));
        Assert.Equal(["FILL", "HEAD"], groups[0].Below.Select(m => m.Code));
    }

    [Fact]
    public void Categories_have_labels_and_tile_colours()
    {
        Assert.Equal(("Stopped", "is-stopped"), (MachineView.CategoryLabel("stopped_faulted"), MachineView.CategoryClass("stopped_faulted")));
        Assert.Equal(("Not reporting", "is-nosignal"), (MachineView.CategoryLabel(null), MachineView.CategoryClass(null)));
    }

    [Fact]
    public void A_stops_reason_is_the_operators_else_the_mapped_one()
    {
        var stop = new DowntimeDto { Id = Guid.CreateVersion7(), EquipmentId = Guid.CreateVersion7(), EquipmentCode = "F", PackMlName = "Stopped", MappedReasonCode = "JAM", MappedReasonName = "Jam" };
        Assert.Equal("JAM · Jam", MachineView.ReasonText(stop));
        Assert.Equal("OTHER · Other", MachineView.ReasonText(stop with { OperatorReasonCode = "OTHER", OperatorReasonName = "Other" }));
        Assert.Equal("", MachineView.ReasonText(stop with { MappedReasonCode = null }));
    }

    [Fact]
    public void Report_figures_read_as_percentages_and_uses_as_words()
    {
        Assert.Equal(("86.7%", "100.0%", "—"), (MachineView.Pct(86.666m), MachineView.Pct(100m), MachineView.Pct(null)));
        Assert.Equal("Micro-stops", MachineView.UseLabel("micro_stop"));
        Assert.Equal("Non-production mode", MachineView.UseLabel("non_production"));
        Assert.Equal("Worst of its machines", MachineView.DerivationNote("series"));
        Assert.Null(MachineView.DerivationNote("own_tags"));
    }

    [Fact]
    public void Shifts_are_shown_for_the_top_of_each_group()
    {
        Guid line = Guid.CreateVersion7(), machine = Guid.CreateVersion7();
        var figures = new OeeFiguresDto();
        var report = new OeeReportDto
        {
            Equipment =
            [
                new() { EquipmentId = line, Code = "L1", Name = "Line", Figures = figures },
                new() { EquipmentId = machine, Code = "FIL", Name = "Filler", ParentId = line, Depth = 1, Figures = figures },
            ],
            Shifts =
            [
                new() { EquipmentId = line, EquipmentCode = "L1", ShiftCode = "A", Figures = figures },
                new() { EquipmentId = machine, EquipmentCode = "FIL", ShiftCode = "A", Figures = figures },
            ],
        };

        Assert.Equal(["L1"], MachineView.TopShifts(report).Select(s => s.EquipmentCode));
    }
}
