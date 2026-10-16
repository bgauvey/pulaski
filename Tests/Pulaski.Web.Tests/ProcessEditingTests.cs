using Pulaski.Contracts.Processes;
using Pulaski.Web.Services.Processes;

namespace Pulaski.Web.Tests;

public class ProcessEditingTests
{
    private static readonly Guid Ext1 = Guid.CreateVersion7();
    private static readonly Guid Ext2 = Guid.CreateVersion7();
    private static readonly Guid Ext3 = Guid.CreateVersion7();
    private static readonly Guid Pack = Guid.CreateVersion7();

    private static (ProcessDefinitionDto Definition, Guid Extrude, Guid Pack) ExtrudeAndPack()
    {
        var (definition, extrude) = ProcessEditing.AddOperation(new ProcessDefinitionDto(), Guid.CreateVersion7(), "Extrude", equipmentId: Ext1);
        definition = ProcessEditing.AddEquipment(definition, extrude, Ext2);
        (definition, var pack) = ProcessEditing.AddOperation(definition, Guid.CreateVersion7(), "Pack", equipmentId: Pack);
        definition = ProcessEditing.Connect(definition, extrude, pack, Guid.CreateVersion7()).Definition!;
        return (definition, extrude, pack);
    }

    [Fact]
    public void Operations_are_numbered_in_tens_and_placed_to_the_right()
    {
        var (definition, extrude, pack) = ExtrudeAndPack();

        Assert.Equal([10, 20], definition.Operations.Select(o => o.Number));
        Assert.True(definition.Operations.Single(o => o.Id == pack).X > definition.Operations.Single(o => o.Id == extrude).X);
        Assert.Equal(30, ProcessEditing.NextNumber(definition with { Operations = [.. definition.Operations, definition.Operations[0] with { Number = 25 }] }));
    }

    [Fact]
    public void Adding_or_removing_equipment_splits_the_operation_evenly_to_100()
    {
        var (definition, extrude, _) = ExtrudeAndPack();
        Assert.Equal([50m, 50m], definition.Operations[0].Equipment.Select(e => e.SharePercent));

        definition = ProcessEditing.AddEquipment(definition, extrude, Ext3);
        Assert.Equal([33.33m, 33.33m, 33.34m], definition.Operations[0].Equipment.Select(e => e.SharePercent));
        Assert.Equal(100m, ProcessEditing.ShareTotal(definition.Operations[0]));
        Assert.Equal(3, ProcessEditing.AddEquipment(definition, extrude, Ext3).Operations[0].Equipment.Count);

        definition = ProcessEditing.RemoveEquipment(definition, extrude, Ext1);
        Assert.Equal([(Ext2, 50m), (Ext3, 50m)], definition.Operations[0].Equipment.Select(e => (e.EquipmentId, e.SharePercent)));
    }

    [Fact]
    public void Links_refuse_loops_repeats_and_self_and_go_when_an_operation_goes()
    {
        var (definition, extrude, pack) = ExtrudeAndPack();

        Assert.NotNull(ProcessEditing.Connect(definition, pack, extrude, Guid.CreateVersion7()).Refusal);
        Assert.NotNull(ProcessEditing.Connect(definition, extrude, pack, Guid.CreateVersion7()).Refusal);
        Assert.NotNull(ProcessEditing.Connect(definition, pack, pack, Guid.CreateVersion7()).Refusal);
        Assert.Empty(ProcessEditing.RemoveOperation(definition, pack).Dependencies);
    }

    [Fact]
    public void Auto_arrange_puts_each_operation_one_column_after_what_it_follows()
    {
        var (definition, extrude, pack) = ExtrudeAndPack();
        (definition, var label) = ProcessEditing.AddOperation(definition, Guid.CreateVersion7(), "Label", x: 999, y: 999);
        definition = ProcessEditing.Connect(definition, label, pack, Guid.CreateVersion7()).Definition!;

        var arranged = ProcessEditing.AutoArrange(definition).Operations.ToDictionary(o => o.Id);

        Assert.Equal(arranged[extrude].X, arranged[label].X);
        Assert.True(arranged[label].Y > arranged[extrude].Y);
        Assert.Equal(arranged[extrude].X + ProcessEditing.NodeWidth + ProcessEditing.ColumnGap, arranged[pack].X);
    }

    [Fact]
    public void A_link_reads_as_its_type_and_lag()
    {
        var link = new ProcessDependencyDto(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), ProcessEditing.AfterEnd, TimeSpan.FromMinutes(30));

        Assert.Equal("after end +30m", ProcessEditing.Describe(link));
        Assert.Equal("after start", ProcessEditing.Describe(link with { Type = ProcessEditing.AfterStart, Lag = TimeSpan.Zero }));
    }
}
