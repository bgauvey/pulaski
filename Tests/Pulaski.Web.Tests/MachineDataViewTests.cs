using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;
using Pulaski.Web.Components;
using Pulaski.Web.Components.Pages.Equipment;
using Pulaski.Web.Services;
using Pulaski.Web.Services.MachineData;

namespace Pulaski.Web.Tests;

/// <summary>Machine data on the equipment screen and the profile export (D-041).</summary>
public class MachineDataViewTests
{
    private static readonly Guid SiteId = Guid.CreateVersion7();

    private static readonly EquipmentDto Line = new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = SiteId,
        Code = "PACK-LINE",
        Name = "Pack line",
        Level = "work_center",
        EquipmentRole = "production_line",
        TracksState = true,
        IsActive = true,
        MicroStopThresholdSeconds = 300,
        Version = 7,
    };

    private static readonly EquipmentDto Filler = Line with { Id = Guid.CreateVersion7(), ParentId = Line.Id, Code = "FILL-01", Level = "work_unit", EquipmentRole = "machine" };

    private static readonly EquipmentDto Conveyor = Filler with { Id = Guid.CreateVersion7(), Code = "CONV-01", TracksState = false };

    private static readonly EquipmentTree Tree = new([Line, Conveyor, Filler]);

    [Fact]
    public void Machine_data_rows_show_only_on_equipment_that_tracks_state()
    {
        var profile = new MachineProfileDto { Id = Guid.CreateVersion7(), SiteId = SiteId, Code = "CP-A", Name = "Case packer A", IsActive = true };
        var elsewhere = profile with { Id = Guid.CreateVersion7(), SiteId = Guid.CreateVersion7(), Code = "CP-B" };

        var items = EquipmentProperties.ForExisting(Line, Tree, profiles: [profile, elsewhere]);
        var choices = items.Single(i => i.Key == EquipmentProperties.MachineProfileKey).Choices;
        Assert.Equal(["None", "CP-A · Case packer A"], choices.Select(c => c.Label));
        Assert.Equal("5", items.Single(i => i.Key == EquipmentProperties.MicroStopKey).Value);
        Assert.DoesNotContain(items, i => i.Key == EquipmentProperties.ConstraintKey);

        Assert.DoesNotContain(EquipmentProperties.ForExisting(Conveyor, Tree), i => EquipmentProperties.MachineDataKeys.Contains(i.Key));
    }

    [Fact]
    public void Minutes_become_seconds_and_a_constraint_line_starts_with_its_first_machine_that_tracks_state()
    {
        var threshold = EquipmentProperties.MachineDataChange(Line, new PropertyChange(EquipmentProperties.MicroStopKey, "1.5"), Tree)!;
        Assert.Equal((90, 7L), (threshold.MicroStopThresholdSeconds, threshold.Version));
        Assert.Null(EquipmentProperties.MachineDataChange(Line, new PropertyChange(EquipmentProperties.MicroStopKey, "soon"), Tree));

        var constraint = EquipmentProperties.MachineDataChange(Line, new PropertyChange(EquipmentProperties.StateDerivationKey, "constraint_machine"), Tree)!;
        Assert.Equal(("constraint_machine", (Guid?)Filler.Id), (constraint.StateDerivation, constraint.ConstraintEquipmentId));

        var series = EquipmentProperties.MachineDataChange(Line with { StateDerivation = "constraint_machine", ConstraintEquipmentId = Filler.Id },
            new PropertyChange(EquipmentProperties.StateDerivationKey, "series"), Tree)!;
        Assert.Equal(("series", (Guid?)null), (series.StateDerivation, series.ConstraintEquipmentId));

        Assert.Equal("2", EquipmentProperties.Minutes(120));
        Assert.Equal("0.5", EquipmentProperties.Minutes(30));
    }

    [Fact]
    public void A_profile_exports_its_active_entries_as_the_import_reads_them()
    {
        var detail = new MachineProfileDetailDto
        {
            Profile = new MachineProfileDto { Id = Guid.CreateVersion7(), SiteId = SiteId, Code = "CP-A", Name = "Case packer A" },
            States =
            [
                new() { RawValue = 2, Name = "Running", PackMlState = 6, PackMlName = "Execute", IsActive = true },
                new() { RawValue = 9, Name = "Old", PackMlState = 2, PackMlName = "Stopped", IsActive = false },
            ],
            Codes = [new() { Kind = "stop_reason", RawValue = 101, Message = "Infeed jam, left", Category = "Mechanical", ReasonCode = "JAM", IsActive = true }],
        };

        Assert.Equal(
            "kind,value,name,packml_state,category,reason\r\nstate,2,Running,Execute,,\r\nstop_reason,101,\"Infeed jam, left\",,Mechanical,JAM\r\n",
            MachineProfileView.ExportCsv(detail));
    }
}
