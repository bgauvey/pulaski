using Pulaski.Domain;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Bills of materials (D-025), using PRF-5100-BR per 1000 m: blend and capstock at Extrude (10), end caps at Pack (30).</summary>
public class BomTests
{
    private static readonly Guid Metre = Guid.CreateVersion7();
    private static readonly Guid Kg = Guid.CreateVersion7();
    private static readonly Guid Each = Guid.CreateVersion7();
    private static readonly Guid Blend = Guid.CreateVersion7();
    private static readonly Guid Cap = Guid.CreateVersion7();
    private static readonly Guid EndCap = Guid.CreateVersion7();

    private static BomItem Item(int line, Guid component, decimal qty, Guid uom, int? op = null, decimal scrap = 0m) =>
        new(Guid.CreateVersion7(), line, component, qty, uom, scrap, ConsumptionMethod.Backflush, op);

    private static BomDefinition ProfileBom() => new(1000m, Metre,
    [
        Item(10, Blend, 1450m, Kg, op: 10, scrap: 0.02m),
        Item(20, Cap, 90m, Kg, op: 10),
        Item(30, EndCap, 84m, Each, op: 30),
    ]);

    /// <summary>EXT-01 and EXT-02 share Extrude 60/40; PACK1 packs it all.</summary>
    private static IReadOnlyList<OperationJob> Jobs(decimal qty)
    {
        var extrude = new ProcessOperation(Guid.CreateVersion7(), 10, "Extrude", null, 0, 0,
            [new OperationEquipment(Guid.CreateVersion7(), 60m), new OperationEquipment(Guid.CreateVersion7(), 40m)]);
        var pack = new ProcessOperation(Guid.CreateVersion7(), 30, "Pack", null, 0, 0, [new OperationEquipment(Guid.CreateVersion7(), 100m)]);
        var link = new ProcessDependency(Guid.CreateVersion7(), extrude.Id, pack.Id, DependencyType.AfterStart, TimeSpan.Zero);
        return ProcessJobPlan.For(new ProcessDefinition([extrude, pack], [link]), "WO-1", qty, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Each_item_goes_to_the_jobs_of_its_operation_by_their_share()
    {
        var components = BomExplosion.For(ProfileBom(), 2400m, Metre, Jobs(2400m));

        // Blend: 2400 / 1000 × 1450 × 1.02 = 3549.6 kg, split 60/40 between the extruders.
        Assert.Equal(
            [(0, Blend, 2129.76m), (1, Blend, 1419.84m), (0, Cap, 129.6m), (1, Cap, 86.4m), (2, EndCap, 201.6m)],
            components.Select(c => (c.JobIndex, c.Item.ComponentMaterialId, c.PlannedQty)));
    }

    [Fact]
    public void An_item_without_an_operation_goes_to_the_first_operation()
    {
        var bom = new BomDefinition(1000m, Metre, [Item(10, Blend, 1000m, Kg)]);

        var components = BomExplosion.For(bom, 1000m, Metre, Jobs(1000m));

        Assert.Equal([0, 1], components.Select(c => c.JobIndex));
        Assert.Equal(1000m, components.Sum(c => c.PlannedQty));
    }

    [Fact]
    public void Shares_round_down_and_the_last_job_takes_the_remainder()
    {
        var bom = new BomDefinition(3m, Metre, [Item(10, Blend, 1m, Kg, op: 10)]);

        var components = BomExplosion.For(bom, 1m, Metre, Jobs(1m));

        Assert.Equal(0.333333m, components.Sum(c => c.PlannedQty));
        Assert.Equal([0.199999m, 0.133334m], components.Select(c => c.PlannedQty));
    }

    [Fact]
    public void The_order_must_be_in_the_bom_base_unit_and_name_real_operations()
    {
        Assert.Throws<DomainRuleException>(() => BomExplosion.For(ProfileBom(), 10m, Kg, Jobs(10m)));

        var wrongOperation = new BomDefinition(1m, Metre, [Item(10, Blend, 1m, Kg, op: 20)]);
        var refused = Assert.Throws<DomainRuleException>(() => BomExplosion.For(wrongOperation, 10m, Metre, Jobs(10m)));
        Assert.Contains("operation 20", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_draft_may_be_incomplete_but_not_malformed()
    {
        var draft = BomVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7(), Metre);
        Assert.Equal(["no_items"], draft.Definition.ReleaseProblems().Select(p => p.Code));

        var malformed = new BomDefinition(0m, Metre, [Item(10, Blend, 0m, Kg, scrap: 1m), Item(10, Cap, 1m, Kg) with { ConsumptionMethod = "magic" }]);
        Assert.Equal(
            ["base_qty", "qty_per", "scrap_factor", "consumption_method", "duplicate_line"],
            malformed.StructuralProblems().Select(p => p.Code));
        Assert.Throws<DomainRuleException>(() => draft.SaveDraft(malformed));
    }

    [Fact]
    public void A_released_version_is_fixed_and_the_next_draft_copies_it_with_new_item_ids()
    {
        var draft = BomVersion.FirstDraft(Guid.CreateVersion7(), Guid.CreateVersion7(), Metre).SaveDraft(ProfileBom());
        var released = draft.Release(DateTimeOffset.UnixEpoch, "First cut");

        Assert.Throws<DomainRuleException>(() => released.SaveDraft(ProfileBom()));

        var next = released.NextDraft(Guid.CreateVersion7(), Guid.CreateVersion7);
        Assert.Equal((2, BomVersionStatus.Draft), (next.Number, next.Status));
        Assert.Equal(released.Definition.Items.Select(i => i.LineNo), next.Definition.Items.Select(i => i.LineNo));
        Assert.Empty(next.Definition.Items.Select(i => i.Id).Intersect(released.Definition.Items.Select(i => i.Id)));
        Assert.Equal(BomVersionStatus.Obsolete, released.MakeObsolete().Status);
    }
}
