using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Containers inside containers (#76): no cycles, no deeper than five, and moving alone takes one out.</summary>
public class NestedContainerTests
{
    private static readonly Guid Site = Guid.CreateVersion7();
    private static readonly Guid Stores = Guid.CreateVersion7();
    private static readonly Guid Dock = Guid.CreateVersion7();

    private static Container Make(string code, Guid? location = null) =>
        Container.Create(Guid.CreateVersion7(), Site, code, ContainerKinds.Box, location ?? Stores, HandlerFixture.Now, "test");

    [Fact]
    public void A_box_goes_on_a_pallet_at_the_pallets_location_and_comes_off_where_it_is()
    {
        var pallet = Make("PAL-1", Dock);
        var box = Make("BOX-1");

        var packed = box.PackInto(pallet, [], height: 0);
        Assert.Equal((pallet.Id, Dock), (packed.ParentId!.Value, packed.LocationId));
        Assert.Throws<DomainRuleException>(() => packed.PackInto(pallet, [], 0));

        var off = packed.TakeOut();
        Assert.Equal(((Guid?)null, Dock), (off.ParentId, off.LocationId));
        Assert.Throws<DomainRuleException>(() => off.TakeOut());

        // Moved alone, it leaves the pallet.
        Assert.Null(packed.MoveTo(Stores).ParentId);
    }

    [Fact]
    public void A_container_never_goes_inside_itself_or_deeper_than_five()
    {
        var pallet = Make("PAL-1");
        var box = Make("BOX-1").PackInto(pallet, [], 0);

        Assert.Throws<DomainRuleException>(() => pallet.PackInto(pallet, [], 0));
        var cycle = Assert.Throws<DomainRuleException>(() => pallet.PackInto(box, [pallet.Id], 1));
        Assert.Contains("inside PAL-1", cycle.Message, StringComparison.Ordinal);

        // Into a container already four deep, with one level below it, would be six.
        var deep = Assert.Throws<DomainRuleException>(() => Make("BOX-2").PackInto(Make("BOX-3"), [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], height: 1));
        Assert.Contains("6 deep", deep.Message, StringComparison.Ordinal);
        Make("BOX-4").PackInto(Make("BOX-5"), [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()], height: 0);

        Assert.Throws<DomainRuleException>(() => pallet.Retire(lotsInside: 0, containersInside: 1));
    }
}
