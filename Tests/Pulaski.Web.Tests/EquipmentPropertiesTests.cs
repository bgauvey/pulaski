using Pulaski.Contracts.EquipmentModel;
using Pulaski.Web.Components;
using Pulaski.Web.Components.Pages.Equipment;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

public class EquipmentPropertiesTests
{
    private static readonly IReadOnlyList<EquipmentTypeDto> Types =
    [
        new("enterprise", "enterprise"),
        new("site", "site"),
        new("area", "area"),
        new("work_center", "production_line"),
        new("work_center", "storage_zone"),
        new("work_unit", "machine"),
    ];

    private static readonly EquipmentDto Line = new()
    {
        Id = Guid.CreateVersion7(),
        Code = "EXT-01",
        Name = "Extrusion line 1",
        Description = "North bay",
        Level = "work_center",
        EquipmentRole = "production_line",
        TracksState = true,
        IsSchedulable = true,
        IsActive = true,
        Version = 42,
    };

    [Fact]
    public void Lots_can_be_indistinguishable_only_where_inventory_is_held()
    {
        var silo = Line with
        {
            Code = "SILO-1",
            EquipmentRole = "storage_zone",
            TracksState = false,
            IsSchedulable = false,
            HoldsInventory = true,
            ComminglesLots = true,
            SingleMaterial = true,
        };

        Assert.True(EquipmentProperties.ApplyChange(silo, new PropertyChange("comminglesLots", "true"))!.ComminglesLots);
        var dropped = EquipmentProperties.ApplyChange(silo, new PropertyChange("holdsInventory", "false"))!;
        Assert.Equal((false, false, false), (dropped.HoldsInventory, dropped.ComminglesLots, dropped.SingleMaterial));
        Assert.False(EquipmentProperties.ApplyChange(silo, new PropertyChange("singleMaterial", "false"))!.SingleMaterial);
        Assert.Contains(EquipmentProperties.ForExisting(silo, new EquipmentTree([silo])), i => i.Key == "singleMaterial");
        Assert.DoesNotContain(EquipmentProperties.ForExisting(Line, new EquipmentTree([Line])), i => i.Key == "singleMaterial");
        Assert.Equal(true, EquipmentProperties.ApplyChange(silo, new PropertyChange("name", "Silo"))!.ComminglesLots);
    }

    [Theory]
    [InlineData("name", "  Line one  ", "Line one")]
    [InlineData("description", "   ", null)]
    public void Text_edit_becomes_an_update_that_keeps_everything_else(string key, string value, string? expected)
    {
        var request = EquipmentProperties.ApplyChange(Line, new PropertyChange(key, value));

        Assert.NotNull(request);
        Assert.Equal(42, request.Version);
        Assert.Equal(expected, key == "name" ? request.Name : request.Description);
        Assert.True(request.TracksState);
        Assert.True(request.IsSchedulable);
        Assert.True(request.IsActive);
    }

    [Fact]
    public void Boolean_edit_sets_only_that_flag()
    {
        var request = EquipmentProperties.ApplyChange(Line, new PropertyChange("isActive", "false"));

        Assert.NotNull(request);
        Assert.False(request.IsActive);
        Assert.Equal("Extrusion line 1", request.Name);
        Assert.Equal("North bay", request.Description);
    }

    [Theory]
    [InlineData("code")]
    [InlineData("level")]
    [InlineData("id")]
    public void Fixed_properties_produce_no_update(string key) =>
        Assert.Null(EquipmentProperties.ApplyChange(Line, new PropertyChange(key, "x")));

    [Fact]
    public void Existing_equipment_only_edits_name_description_capabilities_active_and_shifts()
    {
        var items = EquipmentProperties.ForExisting(Line, new EquipmentTree([Line]));

        var editable = items.Where(i => i.Editor != PropertyEditor.ReadOnly).Select(i => i.Key);
        Assert.Equal(["name", "description", "tracksState", "isSchedulable", "canRunJob", "holdsInventory", "isActive",
            "machineProfile", "microStop", "stateDerivation", "calendar"], editable);
    }

    [Fact]
    public void Children_take_levels_below_their_parent()
    {
        var site = Line with { Level = "site", EquipmentRole = "site" };

        Assert.Equal(["enterprise"], EquipmentProperties.LevelsUnder(null, Types));
        Assert.Equal(["area", "work_center", "work_unit"], EquipmentProperties.LevelsUnder(site, Types));
    }

    [Fact]
    public void Nothing_can_go_below_the_lowest_level()
    {
        var machine = Line with { Level = "work_unit", EquipmentRole = "machine" };

        Assert.Null(EquipmentProperties.NewDraft(machine, Types));
    }

    [Fact]
    public void Changing_a_draft_role_resets_capability_defaults()
    {
        var area = Line with { Level = "area", EquipmentRole = "area" };
        var draft = EquipmentProperties.NewDraft(area, Types)!;

        Assert.Equal("work_center", draft.Level);
        Assert.Equal("production_line", draft.Role);
        Assert.True(draft.TracksState);
        Assert.True(draft.IsSchedulable);

        EquipmentProperties.ApplyChange(draft, new PropertyChange("role", "storage_zone"), Types);

        Assert.False(draft.TracksState);
        Assert.False(draft.IsSchedulable);
        Assert.True(draft.HoldsInventory);
    }

    [Fact]
    public void Draft_sends_time_zone_only_for_a_site()
    {
        var draft = EquipmentProperties.NewDraft(null, Types)!;
        EquipmentProperties.ApplyChange(draft, new PropertyChange("timeZone", "America/Chicago"), Types);
        EquipmentProperties.ApplyChange(draft, new PropertyChange("code", " ENT "), Types);

        var request = draft.ToRequest();

        Assert.Equal("enterprise", request.Level);
        Assert.Equal("ENT", request.Code);
        Assert.Null(request.TimeZone);
    }
}
