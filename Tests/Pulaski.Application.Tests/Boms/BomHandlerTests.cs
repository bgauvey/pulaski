using Pulaski.Application.Abstractions;
using Pulaski.Application.Boms;
using Pulaski.Application.Units;
using Pulaski.Contracts.Boms;
using Pulaski.Domain;
using Pulaski.Domain.Boms;
using Pulaski.Domain.Processes;

namespace Pulaski.Application.Tests.Boms;

/// <summary>Creating, drafting and releasing bills of materials (D-025).</summary>
public class BomHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _metre = Guid.CreateVersion7();
    private readonly Guid _kg = Guid.CreateVersion7();
    private readonly Guid _profile = Guid.CreateVersion7();
    private readonly Guid _blend = Guid.CreateVersion7();

    public BomHandlerTests()
    {
        _f.Stores.Boms.AddMaterial(_profile, _metre);
        _f.Stores.Boms.AddMaterial(_blend, _kg);
    }

    private CreateBomHandler Create() => new(_f.Stores.UnitOfWork, _f.Stores.Boms, _f.Stores.Processes, _f.Stores.Events, _f.Events, _f.Ids);

    private SaveBomDraftHandler Save() => new(_f.Stores.UnitOfWork, _f.Stores.Boms, _f.Stores.Processes, _f.Stores.Units, _f.Stores.Events, _f.Events);

    private ReleaseBomVersionHandler Release() => new(_f.Stores.UnitOfWork, _f.Stores.Boms, _f.Stores.Processes, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Time);

    private StartBomDraftHandler StartDraft() => new(_f.Stores.UnitOfWork, _f.Stores.Boms, _f.Stores.Events, _f.Events, _f.Ids);

    [Fact]
    public async Task A_bom_starts_with_an_empty_draft_in_the_material_base_unit_and_is_released()
    {
        var created = await Create().HandleAsync(new CreateBomCommand(_profile, null), CancellationToken.None);
        var draft = await DraftAsync(created.Id);
        Assert.Equal((1, BomVersionStatus.Draft, 1m, _metre), (draft.Value.Number, draft.Value.Status, draft.Value.Definition.BaseQty, draft.Value.Definition.BaseUomId));

        var saved = await Save().HandleAsync(
            new SaveBomDraftCommand(created.Id, draft.Value.Id, draft.Version, Definition(Line(10, _blend, 1450m))), CancellationToken.None);
        Assert.Empty(saved.Problems);

        await Release().HandleAsync(new ReleaseBomVersionCommand(created.Id, draft.Value.Id, saved.Version, "First cut"), CancellationToken.None);

        var released = _f.Stores.Boms.FindVersion(draft.Value.Id)!.Value;
        Assert.Equal((BomVersionStatus.Released, "First cut"), (released.Status, released.Note));
        Assert.Contains(released.Id, _f.Stores.Boms.ReleasedRows);
        Assert.Equal(
            [EventTypes.BomCreated, EventTypes.BomDraftSaved, EventTypes.BomVersionReleased],
            _f.Stores.Events.Appended.Select(e => e.Type));
    }

    [Fact]
    public async Task A_material_can_not_be_its_own_component_and_unknown_components_are_refused()
    {
        var created = await Create().HandleAsync(new CreateBomCommand(_profile, null), CancellationToken.None);
        var draft = await DraftAsync(created.Id);

        await Assert.ThrowsAsync<DomainRuleException>(() => Save().HandleAsync(
            new SaveBomDraftCommand(created.Id, draft.Value.Id, draft.Version, Definition(Line(10, _profile, 1m))), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Save().HandleAsync(
            new SaveBomDraftCommand(created.Id, draft.Value.Id, draft.Version, Definition(Line(10, Guid.CreateVersion7(), 1m))), CancellationToken.None));
    }

    [Fact]
    public async Task Only_a_bom_for_a_process_names_operations_and_they_must_be_released()
    {
        var generic = await Create().HandleAsync(new CreateBomCommand(_profile, null), CancellationToken.None);
        var genericDraft = await DraftAsync(generic.Id);
        await Assert.ThrowsAsync<DomainRuleException>(() => Save().HandleAsync(
            new SaveBomDraftCommand(generic.Id, genericDraft.Value.Id, genericDraft.Version, Definition(Line(10, _blend, 1m, op: 10))),
            CancellationToken.None));

        var route = _f.Stores.Processes.AddReleased(Guid.CreateVersion7(), new ProcessDefinition(
            [new ProcessOperation(Guid.CreateVersion7(), 10, "Extrude", null, 0, 0, [new OperationEquipment(Guid.CreateVersion7(), 100m)])], []));
        var forProcess = await Create().HandleAsync(new CreateBomCommand(_profile, route.Process.Id), CancellationToken.None);
        var draft = await DraftAsync(forProcess.Id);

        // A draft may name an operation the process doesn't have yet; release refuses it.
        var saved = await Save().HandleAsync(
            new SaveBomDraftCommand(forProcess.Id, draft.Value.Id, draft.Version, Definition(Line(10, _blend, 1m, op: 20))), CancellationToken.None);
        Assert.Equal(["unknown_operation"], saved.Problems.Select(p => p.Code));
        await Assert.ThrowsAsync<DomainRuleException>(() => Release().HandleAsync(
            new ReleaseBomVersionCommand(forProcess.Id, draft.Value.Id, saved.Version, null), CancellationToken.None));
    }

    [Fact]
    public async Task A_bom_for_a_process_needs_the_process_to_make_the_material()
    {
        var route = _f.Stores.Processes.AddReleased(Guid.CreateVersion7(), ProcessDefinition.Empty, ProcessScope.Nothing);

        await Assert.ThrowsAsync<DomainRuleException>(() => Create().HandleAsync(new CreateBomCommand(_profile, route.Process.Id), CancellationToken.None));
    }

    [Fact]
    public async Task Releasing_again_makes_the_previous_version_obsolete()
    {
        var created = await Create().HandleAsync(new CreateBomCommand(_profile, null), CancellationToken.None);
        var v1 = await DraftAsync(created.Id);
        var saved = await Save().HandleAsync(new SaveBomDraftCommand(created.Id, v1.Value.Id, v1.Version, Definition(Line(10, _blend, 1m))), CancellationToken.None);
        await Release().HandleAsync(new ReleaseBomVersionCommand(created.Id, v1.Value.Id, saved.Version, null), CancellationToken.None);

        var v2 = await StartDraft().HandleAsync(new StartBomDraftCommand(created.Id), CancellationToken.None);
        await Assert.ThrowsAsync<DomainRuleException>(() => StartDraft().HandleAsync(new StartBomDraftCommand(created.Id), CancellationToken.None));
        await Release().HandleAsync(new ReleaseBomVersionCommand(created.Id, v2.Id, v2.Version, null), CancellationToken.None);

        Assert.Equal(BomVersionStatus.Obsolete, _f.Stores.Boms.FindVersion(v1.Value.Id)!.Value.Status);
        Assert.Equal(BomVersionStatus.Released, _f.Stores.Boms.FindVersion(v2.Id)!.Value.Status);
    }

    [Fact]
    public async Task An_item_unit_must_convert_to_the_component_base_unit_before_release()
    {
        var kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        var box = _f.Stores.Units.AddUnit("box", "other", null);
        _f.Stores.Boms.AddMaterial(_blend, kg.Id);
        _f.Stores.Units.AddMaterial(_blend);
        var created = await Create().HandleAsync(new CreateBomCommand(_profile, null), CancellationToken.None);
        var draft = await DraftAsync(created.Id);

        var saved = await Save().HandleAsync(
            new SaveBomDraftCommand(created.Id, draft.Value.Id, draft.Version, Definition(Line(10, _blend, 2m) with { UomId = box.Id })),
            CancellationToken.None);
        var problem = Assert.Single(saved.Problems);
        Assert.Equal("unit_conversion", problem.Code);
        Assert.Contains("box doesn't convert to kg", problem.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<DomainRuleException>(() => Release().HandleAsync(
            new ReleaseBomVersionCommand(created.Id, draft.Value.Id, saved.Version, null), CancellationToken.None));

        // With a conversion for the blend (a box holds 40 kg) it releases.
        await new SetMaterialConversionHandler(_f.Stores.UnitOfWork, _f.Stores.Units, _f.Stores.Events, _f.Events)
            .HandleAsync(new SetMaterialConversionCommand(_blend, box.Id, kg.Id, 40m), CancellationToken.None);
        await Release().HandleAsync(new ReleaseBomVersionCommand(created.Id, draft.Value.Id, saved.Version, null), CancellationToken.None);
    }

    private async Task<Versioned<BomVersion>> DraftAsync(Guid bomId)
    {
        var bom = (await _f.Stores.Boms.GetAsync(bomId, CancellationToken.None))!;
        return _f.Stores.Boms.FindVersion(bom.Versions.Single(v => v.Status == BomVersionStatus.Draft).Id)!;
    }

    private BomDefinitionDto Definition(params BomItemDto[] items) => new() { BaseQty = 1000m, BaseUomId = _metre, Items = items };

    private BomItemDto Line(int line, Guid component, decimal qty, int? op = null) => new()
    {
        Id = Guid.CreateVersion7(),
        LineNo = line,
        ComponentMaterialId = component,
        QtyPer = qty,
        UomId = _kg,
        ConsumptionMethod = ConsumptionMethod.TimeWindow,
        OperationNumber = op,
    };
}
