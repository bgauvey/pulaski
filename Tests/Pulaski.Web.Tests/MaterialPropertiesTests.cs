using Pulaski.Contracts.Materials;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Web.Components;
using Pulaski.Web.Services.Materials;

namespace Pulaski.Web.Tests;

/// <summary>Which material fields can be edited, and the updates edits make (D-033).</summary>
public class MaterialPropertiesTests
{
    private static readonly UomDto Kg = new() { Id = Guid.CreateVersion7(), Code = "kg", Name = "Kilogram", Dimension = "mass" };
    private static readonly MaterialClassDto Resins = new() { Id = Guid.CreateVersion7(), Code = "RESIN", Name = "Resins", IsActive = true };
    private static readonly MaterialClassDto Retired = new() { Id = Guid.CreateVersion7(), Code = "OLD", Name = "Old", IsActive = false };

    private static readonly MaterialDto Local = new()
    {
        Id = Guid.CreateVersion7(),
        Code = "PP-H100",
        Name = "Polypropylene",
        MaterialType = "raw",
        BaseUomId = Kg.Id,
        BaseUomCode = "kg",
        IsActive = true,
        Version = 7,
    };

    private static readonly MaterialDto FromErp = Local with { Origin = "erp", ExternalSystem = "ERP1", ExternalKey = "10042", ExternalVersion = "3" };

    private static PropertyEditor EditorOf(IReadOnlyList<PropertyItem> items, string key) => items.Single(i => i.Key == key).Editor;

    private static IReadOnlyList<PropertyItem> For(MaterialDto m, MaterialUsageDto usage) => MaterialProperties.For(m, usage, [Resins, Retired], [Kg]);

    [Fact]
    public void An_unused_local_material_can_change_every_field()
    {
        var items = For(Local, new MaterialUsageDto());

        foreach (var key in new[] { "code", "name", "description", "type", "class", "baseUom", "lotTracked", "shelfLife", "active" })
        {
            Assert.NotEqual(PropertyEditor.ReadOnly, EditorOf(items, key));
        }

        Assert.Equal("Pulaski", items.Single(i => i.Key == "origin").Value);
    }

    [Fact]
    public void The_erp_owns_code_name_type_and_base_unit()
    {
        var items = For(FromErp, new MaterialUsageDto());

        foreach (var key in new[] { "code", "name", "type", "baseUom" })
        {
            Assert.Equal(PropertyEditor.ReadOnly, EditorOf(items, key));
            Assert.Contains("ERP1", items.Single(i => i.Key == key).Description, StringComparison.Ordinal);
        }

        foreach (var key in new[] { "description", "class", "lotTracked", "shelfLife", "active" })
        {
            Assert.NotEqual(PropertyEditor.ReadOnly, EditorOf(items, key));
        }

        Assert.Equal("ERP1 · 10042 · version 3", items.Single(i => i.Key == "origin").Value);
    }

    [Fact]
    public void Use_fixes_code_and_unit_and_stock_on_hand_fixes_lot_tracking()
    {
        var inUse = For(Local, new MaterialUsageDto { InUse = true, OpenJobs = 1 });
        Assert.Equal((PropertyEditor.ReadOnly, PropertyEditor.ReadOnly), (EditorOf(inUse, "code"), EditorOf(inUse, "baseUom")));
        Assert.NotEqual(PropertyEditor.ReadOnly, EditorOf(inUse, "lotTracked"));

        var stocked = For(Local, new MaterialUsageDto { InUse = true, LotsOnHand = 2, QuantityOnHand = 40m });
        Assert.Equal(PropertyEditor.ReadOnly, EditorOf(stocked, "lotTracked"));
    }

    [Fact]
    public void Inactive_classes_are_offered_only_to_materials_already_in_them()
    {
        static IEnumerable<string> Classes(IReadOnlyList<PropertyItem> items) => items.Single(i => i.Key == "class").Choices.Select(c => c.Value);

        Assert.Equal(["", Resins.Id.ToString()], Classes(For(Local, new MaterialUsageDto())));
        Assert.Contains(Retired.Id.ToString(), Classes(For(Local with { MaterialClassId = Retired.Id }, new MaterialUsageDto())));
    }

    [Fact]
    public void An_edit_sends_the_whole_material_with_one_field_changed()
    {
        var renamed = MaterialProperties.ApplyChange(Local, new PropertyChange("name", "  PP homopolymer "))!;
        Assert.Equal(("PP homopolymer", "PP-H100", 7L, Kg.Id), (renamed.Name, renamed.Code, renamed.Version, renamed.BaseUomId));

        Assert.Null(MaterialProperties.ApplyChange(Local, new PropertyChange("name", "  ")));
        Assert.Null(MaterialProperties.ApplyChange(Local, new PropertyChange("origin", "x")));
        Assert.Null(MaterialProperties.ApplyChange(Local with { Description = "x" }, new PropertyChange("description", " "))!.Description);
        Assert.Null(MaterialProperties.ApplyChange(Local with { MaterialClassId = Resins.Id }, new PropertyChange("class", ""))!.MaterialClassId);
        Assert.Equal(90, MaterialProperties.ApplyChange(Local, new PropertyChange("shelfLife", "90"))!.ShelfLifeDays);
        Assert.Null(MaterialProperties.ApplyChange(Local with { ShelfLifeDays = 90 }, new PropertyChange("shelfLife", ""))!.ShelfLifeDays);
        Assert.False(MaterialProperties.ApplyChange(Local, new PropertyChange("active", "false"))!.IsActive);
        Assert.False(MaterialProperties.ApplyChange(Local, new PropertyChange("lotTracked", "false"))!.IsLotTracked);
    }

    [Fact]
    public void The_list_groups_by_class_name_with_unclassed_materials_last()
    {
        var a = Local with { Code = "B-2", MaterialClassCode = "RESIN", MaterialClassName = "Resins" };
        var b = Local with { Code = "A-1", MaterialClassCode = "RESIN", MaterialClassName = "Resins" };
        var c = Local with { Code = "C-3", MaterialClassCode = "ADD", MaterialClassName = "Additives" };
        var d = Local with { Code = "D-4" };

        var groups = MaterialProperties.Group([a, d, b, c]);

        Assert.Equal(["Additives", "Resins", "No class"], groups.Select(g => g.Label));
        Assert.Equal(["A-1", "B-2"], groups[1].Items.Select(m => m.Code));
    }
}
