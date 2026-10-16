using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Web.Components;
using Pulaski.Web.Components.Pages.Processes;
using Pulaski.Web.Services.Processes;

namespace Pulaski.Web.Tests;

public class ProcessPropertiesTests
{
    private static readonly EquipmentDto Ext1 = Line("EXT-01", runs: true);
    private static readonly EquipmentDto Dept = Line("EXT", runs: false);
    private static readonly Guid Profiles = Guid.CreateVersion7();

    private static EquipmentDto Line(string code, bool runs) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = code,
        Name = code,
        Level = "work_center",
        EquipmentRole = "production_line",
        IsSchedulable = true,
        CanRunJob = runs,
        IsActive = true,
    };

    private static MaterialDto Material(string code, Guid? classId = null) => new()
    {
        Id = Guid.CreateVersion7(),
        Code = code,
        Name = code,
        MaterialType = "finished",
        BaseUomId = Guid.CreateVersion7(),
        BaseUomCode = "m",
        MaterialClassId = classId,
        IsActive = true,
    };

    private static ProcessDto Process(bool all = false, IReadOnlyList<ProcessMaterialClassDto>? classes = null, IReadOnlyList<ProcessMaterialDto>? defaults = null) => new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = Guid.CreateVersion7(),
        SiteCode = "PLT1",
        Code = "EXT-PACK",
        Name = "Extrude and Pack",
        IsActive = true,
        MakesAllMaterials = all,
        MakesClasses = classes ?? [],
        DefaultFor = defaults ?? [],
    };

    private static readonly ProcessVersionDto Draft = new() { Id = Guid.CreateVersion7(), ProcessId = Guid.CreateVersion7(), Number = 1, Status = "draft", Definition = new() };

    private static PropertyItem Row(IEnumerable<PropertyItem> items, string key) => items.Single(i => i.Key == key);

    [Fact]
    public void A_process_shows_identity_status_scope_and_defaults_with_editable_rows_where_it_can_change()
    {
        var white = Material("WH", Profiles);
        var resin = Material("RS");
        var process = Process(
            classes: [new ProcessMaterialClassDto(Profiles, "PROFILE", "Profiles")],
            defaults: [new ProcessMaterialDto(white.Id, "WH", "White"), new ProcessMaterialDto(resin.Id, "RS", "Resin")]);

        var items = ProcessProperties.ForProcess(process, Draft, [white, resin]);

        Assert.Equal(PropertyEditor.ReadOnly, Row(items, "code").Editor);
        Assert.Equal(PropertyEditor.Text, Row(items, ProcessProperties.Name).Editor);
        Assert.Equal("false", Row(items, ProcessProperties.AllMaterials).Value);
        Assert.True(Row(items, ProcessProperties.ClassPrefix + Profiles).Removable);
        Assert.False(Row(items, ProcessProperties.DefaultPrefix + white.Id).IsFlagged);
        Assert.True(Row(items, ProcessProperties.DefaultPrefix + resin.Id).IsFlagged);   // not in the scope any more
    }

    [Fact]
    public void A_process_that_makes_nothing_says_so_and_all_materials_lists_no_items()
    {
        Assert.True(Row(ProcessProperties.ForProcess(Process(), Draft, []), "makesNothing").IsFlagged);
        Assert.DoesNotContain(ProcessProperties.ForProcess(Process(all: true), Draft, []), i => i.Key == "makesNothing");
    }

    [Fact]
    public void An_operation_lists_each_line_with_its_share_and_flags_a_total_that_is_not_100()
    {
        var (definition, op) = ProcessEditing.AddOperation(new ProcessDefinitionDto(), Guid.CreateVersion7(), "Extrude", equipmentId: Ext1.Id);
        definition = ProcessEditing.AddEquipment(definition, op, Dept.Id);
        definition = ProcessEditing.SetShare(definition, op, Dept.Id, 40m);

        var items = ProcessProperties.ForOperation(definition.Operations[0], definition, [Ext1, Dept], editable: true);

        var share = Row(items, ProcessProperties.SharePrefix + Dept.Id);
        Assert.Equal((PropertyEditor.Number, "40", "%", true), (share.Editor, share.Value, share.Suffix, share.Removable));
        Assert.StartsWith("Department", share.Description, StringComparison.Ordinal);
        Assert.True(Row(items, "total").IsFlagged);
        Assert.Equal(PropertyEditor.ReadOnly, ProcessProperties.ForOperation(definition.Operations[0], definition, [Ext1, Dept], editable: false)
            .Single(i => i.Key == ProcessProperties.Number).Editor);
    }

    [Fact]
    public void A_link_offers_its_two_types_and_a_lag_in_minutes()
    {
        var (definition, a) = ProcessEditing.AddOperation(new ProcessDefinitionDto(), Guid.CreateVersion7(), "Extrude");
        (definition, var b) = ProcessEditing.AddOperation(definition, Guid.CreateVersion7(), "Pack");
        definition = ProcessEditing.Connect(definition, a, b, Guid.CreateVersion7()).Definition!;
        definition = ProcessEditing.UpdateDependency(definition, definition.Dependencies[0].Id, ProcessEditing.AfterEnd, TimeSpan.FromMinutes(30));

        var items = ProcessProperties.ForLink(definition.Dependencies[0], definition, editable: true);

        Assert.Equal(["After Extrude starts", "After Extrude ends"], Row(items, ProcessProperties.LinkType).Choices.Select(c => c.Label));
        Assert.Equal(("30", "min"), (Row(items, ProcessProperties.Lag).Value, Row(items, ProcessProperties.Lag).Suffix));
    }

    [Fact]
    public void Keys_carry_the_id_after_their_prefix()
    {
        var id = Guid.CreateVersion7();

        Assert.Equal(id, ProcessProperties.IdAfter(ProcessProperties.SharePrefix + id, ProcessProperties.SharePrefix));
        Assert.Null(ProcessProperties.IdAfter("name", ProcessProperties.SharePrefix));
    }
}
