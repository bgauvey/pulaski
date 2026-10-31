using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>The new order form's batch hint (#85).</summary>
public class OrderBatchHintTests
{
    private static ProcessOrderCheckDto Check(params OrderEquipmentBatchDto[] equipment) =>
        new() { ProcessId = Guid.CreateVersion7(), MaterialId = Guid.CreateVersion7(), Equipment = equipment };

    [Fact]
    public void Lines_with_their_own_size_are_named_with_their_shares_and_the_rest_use_the_materials()
    {
        var check = Check(new(10, "MIX-1", 60m, false, 200m), new(10, "MIX-2", 40m, false, null), new(20, "PACK", 100m, true, null));

        Assert.Equal(["op 10: MIX-1 200 kg (60%), MIX-2 50 kg (40%)", "op 20: PACK 50 kg until a line is chosen (100%)"],
            OrderBatchHint.Operations(check, 50m, "kg"));
    }

    [Fact]
    public void With_every_line_on_the_materials_size_the_plain_hint_is_enough()
    {
        Assert.Empty(OrderBatchHint.Operations(Check(new(10, "MIX-1", 100m, false, null), new(20, "MIX-2", 100m, false, 50m)), 50m, "kg"));
        Assert.Empty(OrderBatchHint.Operations(null, 50m, "kg"));
    }
}
