using Pulaski.Domain;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Process routes (D-023), using "Extrude and Pack": EXT-01 and EXT-02 extrude, PACK1 packs.</summary>
public class ProcessDefinitionTests
{
    private static readonly Guid Ext1 = Guid.CreateVersion7();
    private static readonly Guid Ext2 = Guid.CreateVersion7();
    private static readonly Guid Pack1 = Guid.CreateVersion7();

    private static ProcessOperation Op(int number, string name, params (Guid Equipment, decimal Share)[] equipment) =>
        new(Guid.CreateVersion7(), number, name, null, 0, 0, [.. equipment.Select(e => new OperationEquipment(e.Equipment, e.Share))]);

    private static ProcessDependency Link(ProcessOperation from, ProcessOperation to, string type = DependencyType.AfterStart) =>
        new(Guid.CreateVersion7(), from.Id, to.Id, type, TimeSpan.Zero);

    private static (ProcessDefinition Definition, ProcessOperation Extrude, ProcessOperation Pack) ExtrudeAndPack()
    {
        var extrude = Op(10, "Extrude", (Ext1, 50m), (Ext2, 50m));
        var pack = Op(30, "Pack", (Pack1, 100m));
        return (new ProcessDefinition([extrude, pack], [Link(extrude, pack)]), extrude, pack);
    }

    private static IEnumerable<string> Codes(IEnumerable<ProcessProblem> problems) => problems.Select(p => p.Code);

    [Fact]
    public void Extrude_and_pack_is_ready_to_release()
    {
        var (definition, extrude, pack) = ExtrudeAndPack();

        Assert.Empty(definition.ReleaseProblems());
        Assert.Equal([extrude.Id, pack.Id], definition.InFlowOrder().Select(o => o.Id));
    }

    [Fact]
    public void A_draft_may_be_incomplete_but_a_release_may_not()
    {
        var extrude = Op(10, "Extrude", (Ext1, 60m), (Ext2, 30m));
        var pack = Op(30, "Pack");
        var orphan = Op(40, "Inspect", (Pack1, 100m));
        var definition = new ProcessDefinition([extrude, pack, orphan], [Link(extrude, pack)]);

        Assert.Empty(definition.StructuralProblems());
        Assert.Equal(["share_total", "no_equipment", "disconnected"], Codes(definition.ReleaseProblems()));
        Assert.Equal(["no_operations"], Codes(ProcessDefinition.Empty.ReleaseProblems()));
    }

    [Fact]
    public void Loops_self_links_duplicates_and_bad_shares_are_malformed()
    {
        var (definition, extrude, pack) = ExtrudeAndPack();

        Assert.Equal(["cycle"], Codes((definition with { Dependencies = [.. definition.Dependencies, Link(pack, extrude)] }).StructuralProblems()));
        Assert.Contains("self_link", Codes((definition with { Dependencies = [Link(pack, pack)] }).StructuralProblems()));
        Assert.Contains("duplicate_link", Codes((definition with { Dependencies = [Link(extrude, pack), Link(extrude, pack)] }).StructuralProblems()));
        Assert.Contains("duplicate_number", Codes((definition with { Operations = [extrude, pack with { Number = 10 }] }).StructuralProblems()));
        Assert.Contains("share_range", Codes((definition with { Operations = [Op(10, "X", (Ext1, 0m)), pack] }).StructuralProblems()));
        Assert.Contains("duplicate_equipment", Codes((definition with { Operations = [Op(10, "X", (Ext1, 50m), (Ext1, 50m)), pack] }).StructuralProblems()));
        Assert.True(definition.WouldLoop(pack.Id, extrude.Id));
        Assert.False(definition.WouldLoop(extrude.Id, pack.Id));
    }

    [Fact]
    public void Operations_with_no_link_between_them_run_in_parallel_and_flow_order_follows_the_numbers()
    {
        var extrude = Op(10, "Extrude", (Ext1, 100m));
        var label = Op(20, "Print labels", (Ext2, 100m));
        var pack = Op(30, "Pack", (Pack1, 100m));
        var definition = new ProcessDefinition([pack, label, extrude], [Link(extrude, pack), Link(label, pack, DependencyType.AfterEnd)]);

        Assert.Empty(definition.ReleaseProblems());
        Assert.Equal([10, 20, 30], definition.InFlowOrder().Select(o => o.Number));
    }

    [Fact]
    public void A_draft_is_edited_then_released_and_changing_it_needs_a_new_draft_with_new_ids()
    {
        var (definition, _, _) = ExtrudeAndPack();
        var draft = ProcessVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7()).SaveDraft(definition);

        var released = draft.Release(DateTimeOffset.UnixEpoch, "First cut");
        var next = released.NextDraft(Guid.CreateVersion7(), Guid.CreateVersion7);

        Assert.Equal((ProcessVersionStatus.Released, "First cut"), (released.Status, released.Note));
        Assert.Throws<DomainRuleException>(() => released.SaveDraft(definition));
        Assert.Equal((2, ProcessVersionStatus.Draft), (next.Number, next.Status));
        Assert.Empty(next.Definition.ReleaseProblems());
        Assert.Empty(next.Definition.Operations.Select(o => o.Id).Intersect(definition.Operations.Select(o => o.Id)));
        Assert.Equal(ProcessVersionStatus.Obsolete, released.MakeObsolete().Status);
    }

    [Fact]
    public void An_incomplete_draft_is_not_released_and_a_malformed_one_is_not_saved()
    {
        var draft = ProcessVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7());
        var (definition, _, pack) = ExtrudeAndPack();

        Assert.Throws<DomainRuleException>(() => draft.Release(DateTimeOffset.UnixEpoch, null));
        Assert.Throws<DomainRuleException>(() => draft.SaveDraft(definition with { Dependencies = [Link(pack, pack)] }));
    }
}

public class ProcessScopeTests
{
    private static readonly Guid Profiles = Guid.CreateVersion7();
    private static readonly Guid White = Guid.CreateVersion7();
    private static readonly Guid Resin = Guid.CreateVersion7();

    [Fact]
    public void A_scope_covers_all_materials_a_class_or_single_items()
    {
        Assert.True(ProcessScope.Of(true, [], []).Covers(Resin, null));
        Assert.True(ProcessScope.Of(false, [Profiles], []).Covers(White, Profiles));
        Assert.False(ProcessScope.Of(false, [Profiles], []).Covers(Resin, null));
        Assert.True(ProcessScope.Of(false, [], [Resin]).Covers(Resin, null));
        Assert.True(ProcessScope.Nothing.IsEmpty);
    }

    [Fact]
    public void All_materials_drops_the_lists_and_repeats_are_dropped()
    {
        Assert.Equal((true, 0, 0), ProcessScope.Of(true, [Profiles], [White]) is var s ? (s.AllMaterials, s.MaterialClassIds.Count, s.MaterialIds.Count) : default);
        Assert.Single(ProcessScope.Of(false, [], [White, White]).MaterialIds);
    }
}
