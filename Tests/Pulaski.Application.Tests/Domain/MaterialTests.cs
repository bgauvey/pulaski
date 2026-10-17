using Pulaski.Domain;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Who owns a material's fields, and what its use fixes (D-033).</summary>
public class MaterialTests
{
    private static readonly Guid Kg = Guid.CreateVersion7();
    private static readonly Guid Lb = Guid.CreateVersion7();

    private static MaterialChange Change(Material m) =>
        new(m.Code, m.Name, m.Description, m.MaterialType, m.MaterialClassId, m.BaseUomId, m.IsLotTracked, m.ShelfLifeDays, m.IsActive);

    private static readonly Material Local = Material.CreateLocal(
        Guid.CreateVersion7(), new MaterialChange("PVC-K67", "PVC compound", null, "raw", null, Kg, true, null, true));

    private static readonly Material FromErp = Material.CreateFromErp(Guid.CreateVersion7(), "ERP1", "000100", "1", "PVC-K67", "PVC compound", Kg, "raw");

    [Fact]
    public void A_material_from_the_erp_keeps_its_erp_fields_but_takes_pulaskis_own()
    {
        Assert.Throws<DomainRuleException>(() => FromErp.Edit(Change(FromErp) with { Name = "Renamed" }, MaterialUse.None));
        Assert.Throws<DomainRuleException>(() => FromErp.Edit(Change(FromErp) with { MaterialType = "finished" }, MaterialUse.None));
        Assert.Throws<DomainRuleException>(() => FromErp.Edit(Change(FromErp) with { BaseUomId = Lb }, MaterialUse.None));

        var edited = FromErp.Edit(Change(FromErp) with { ShelfLifeDays = 180, IsLotTracked = false, Description = "Silo resin" }, MaterialUse.None);
        Assert.Equal((180, false, "Silo resin"), (edited.ShelfLifeDays, edited.IsLotTracked, edited.Description));
    }

    [Fact]
    public void Use_fixes_the_base_unit_and_a_local_code_and_stock_fixes_lot_tracking()
    {
        var inUse = new MaterialUse(InUse: true, HasStockOnHand: false);

        Assert.Throws<DomainRuleException>(() => Local.Edit(Change(Local) with { BaseUomId = Lb }, inUse));
        Assert.Throws<DomainRuleException>(() => Local.Edit(Change(Local) with { Code = "PVC-K68" }, inUse));
        Assert.Equal("PVC-K68", Local.Edit(Change(Local) with { Code = "PVC-K68", BaseUomId = Lb }, MaterialUse.None).Code);

        Assert.Throws<DomainRuleException>(() => Local.Edit(Change(Local) with { IsLotTracked = false }, new MaterialUse(true, true)));
        Assert.False(Local.Edit(Change(Local) with { IsLotTracked = false }, inUse).IsLotTracked);
    }

    [Fact]
    public void The_erp_may_rename_its_items_but_not_change_the_base_unit_of_one_in_use()
    {
        var renamed = FromErp.ImportFromErp("2", "PVC-K67A", "PVC compound A", Kg, "intermediate", new MaterialUse(true, true));
        Assert.Equal(("PVC-K67A", "PVC compound A", "intermediate", "2"), (renamed.Code, renamed.Name, renamed.MaterialType, renamed.ExternalVersion));

        Assert.Throws<DomainRuleException>(() => FromErp.ImportFromErp("2", "PVC-K67", "PVC compound", Lb, "raw", new MaterialUse(true, false)));
        Assert.Throws<DomainRuleException>(() => Local.ImportFromErp("2", "PVC-K67", "PVC compound", Kg, "raw", MaterialUse.None));
        Assert.Throws<DomainRuleException>(() => FromErp.ImportFromErp("2", "PVC-K67", "PVC compound", Kg, "nonsense", MaterialUse.None));
    }
}
