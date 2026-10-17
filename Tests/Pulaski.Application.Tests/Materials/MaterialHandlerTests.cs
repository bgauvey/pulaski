using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Creating, changing and importing materials, and their classes (D-033).</summary>
public class MaterialHandlerTests
{
    private readonly HandlerFixture _f = new();
    private readonly Guid _kg;
    private readonly Guid _lb;

    public MaterialHandlerTests()
    {
        _kg = Admin.AddUom("kg");
        _lb = Admin.AddUom("lb");
    }

    private InMemoryMaterialAdmin Admin => _f.Stores.MaterialAdmin;

    private CreateMaterialHandler Create() => new(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events, _f.Ids);

    private UpdateMaterialHandler Update() => new(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events);

    private ImportMaterialHandler Import() => new(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events, _f.Ids);

    private UpdateMaterialCommand Edit(Guid id, System.Func<UpdateMaterialCommand, UpdateMaterialCommand> change)
    {
        var m = Admin.Find(id)!;
        var v = m.Value;
        return change(new UpdateMaterialCommand(id, m.Version, v.Code, v.Name, v.Description, v.MaterialType, v.MaterialClassId, v.BaseUomId, v.IsLotTracked, v.ShelfLifeDays, v.IsActive));
    }

    [Fact]
    public async Task A_local_material_is_created_with_an_active_unit_and_class()
    {
        var profiles = Admin.AddClass("PROFILE");
        var old = Admin.AddClass("OLD", isActive: false);
        var inactiveUnit = Admin.AddUom("dz", isActive: false);

        var created = await Create().HandleAsync(new CreateMaterialCommand(" PRF-1 ", " Profile 1 ", " ", "finished", profiles.Id, _kg, true, 365), CancellationToken.None);

        var m = Admin.Find(created.Id)!.Value;
        Assert.Equal(("PRF-1", "Profile 1", (string?)null, MaterialOrigin.Local, 365), (m.Code, m.Name, m.Description, m.Origin, m.ShelfLifeDays));
        Assert.Equal(EventTypes.MaterialCreated, Assert.Single(_f.Stores.Events.Appended).Type);
        await Assert.ThrowsAsync<DomainRuleException>(() => Create().HandleAsync(new CreateMaterialCommand("PRF-2", "P", null, "finished", old.Id, _kg, true, null), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Create().HandleAsync(new CreateMaterialCommand("PRF-3", "P", null, "finished", null, inactiveUnit, true, null), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => Create().HandleAsync(new CreateMaterialCommand("PRF-4", "P", null, "gadget", null, _kg, true, null), CancellationToken.None));
        await Assert.ThrowsAsync<ConflictException>(() => Create().HandleAsync(new CreateMaterialCommand("PRF-1", "P", null, "finished", null, _kg, true, null), CancellationToken.None));
    }

    [Fact]
    public async Task Changes_check_the_version_and_the_materials_use_and_a_deactivation_says_what_still_uses_it()
    {
        var created = await Create().HandleAsync(new CreateMaterialCommand("PRF-1", "Profile", null, "finished", null, _kg, true, null), CancellationToken.None);
        Admin.Use[created.Id] = new MaterialUse(InUse: true, HasStockOnHand: true);

        await Assert.ThrowsAsync<DomainRuleException>(() => Update().HandleAsync(Edit(created.Id, c => c with { BaseUomId = _lb }), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Update().HandleAsync(Edit(created.Id, c => c with { IsLotTracked = false }), CancellationToken.None));

        var off = await Update().HandleAsync(Edit(created.Id, c => c with { IsActive = false, Name = "Old profile" }), CancellationToken.None);
        Assert.Equal(["2 open jobs"], off.StillUsedBy);
        Assert.Equal(("Old profile", false), (Admin.Find(created.Id)!.Value.Name, Admin.Find(created.Id)!.Value.IsActive));
        await Assert.ThrowsAsync<ConflictException>(() => Update().HandleAsync(Edit(created.Id, c => c with { ExpectedVersion = 1 }), CancellationToken.None));

        var same = await Update().HandleAsync(Edit(created.Id, c => c), CancellationToken.None);
        Assert.Equal(off.Version, same.Version);
    }

    [Fact]
    public async Task An_import_creates_then_updates_only_the_erp_fields_and_a_repeat_changes_nothing()
    {
        var first = await Import().HandleAsync(new ImportMaterialCommand("SAP", "000100", "1", "PVC-K67", "PVC compound", "kg", "raw"), CancellationToken.None);
        Assert.True(first.Created);
        await Update().HandleAsync(Edit(first.Id, c => c with { ShelfLifeDays = 180 }), CancellationToken.None);

        var renamed = await Import().HandleAsync(new ImportMaterialCommand("SAP", "000100", "2", "PVC-K67", "PVC compound, white", "kg", "raw"), CancellationToken.None);
        Assert.Equal((first.Id, false, true), (renamed.Id, renamed.Created, renamed.Changed));
        var m = Admin.Find(first.Id)!.Value;
        Assert.Equal(("PVC compound, white", 180, "2", MaterialOrigin.Erp), (m.Name, m.ShelfLifeDays, m.ExternalVersion, m.Origin));

        var again = await Import().HandleAsync(new ImportMaterialCommand("SAP", "000100", "2", "PVC-K67", "PVC compound, white", "kg", "raw"), CancellationToken.None);
        Assert.False(again.Changed);
        Assert.Equal(3, _f.Stores.Events.Appended.Count);

        await Assert.ThrowsAsync<DomainRuleException>(() => Update().HandleAsync(Edit(first.Id, c => c with { Name = "Mine" }), CancellationToken.None));
        await Assert.ThrowsAsync<DomainRuleException>(() => Import().HandleAsync(new ImportMaterialCommand("SAP", "000200", null, "X", "X", "bags", "raw"), CancellationToken.None));
        Admin.Use[first.Id] = new MaterialUse(true, false);
        await Assert.ThrowsAsync<DomainRuleException>(() => Import().HandleAsync(new ImportMaterialCommand("SAP", "000100", "3", "PVC-K67", "PVC compound, white", "lb", "raw"), CancellationToken.None));
    }

    [Fact]
    public async Task Classes_keep_their_code_and_can_be_switched_off()
    {
        var created = await new CreateMaterialClassHandler(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new CreateMaterialClassCommand(" resin ", "Resins"), CancellationToken.None);
        Assert.Equal("RESIN", Admin.FindClass(created.Id)!.Value.Code);

        var update = new UpdateMaterialClassHandler(_f.Stores.UnitOfWork, Admin, _f.Stores.Events, _f.Events);
        await update.HandleAsync(new UpdateMaterialClassCommand(created.Id, created.Version, "Resins and compounds", false), CancellationToken.None);
        Assert.Equal(("RESIN", "Resins and compounds", false), (Admin.FindClass(created.Id)!.Value.Code, Admin.FindClass(created.Id)!.Value.Name, Admin.FindClass(created.Id)!.Value.IsActive));
        await Assert.ThrowsAsync<ConflictException>(() => update.HandleAsync(new UpdateMaterialClassCommand(created.Id, created.Version, "X", true), CancellationToken.None));
    }
}
