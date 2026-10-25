using Pulaski.Domain;
using Pulaski.Domain.Materials;
using Pulaski.Domain.WorkOrders;

namespace Pulaski.Application.Tests.Domain;

/// <summary>ERP quantities that aren't whole batches (D-066).</summary>
public class ErpBatchRuleTests
{
    private static readonly Batching Fifty = Batching.Create(50m, 2m, BatchRule.Near);

    [Fact]
    public void Whole_batches_and_continuous_materials_are_planned_as_sent()
    {
        Assert.Equal(new ErpQuantity(1000m, null, null), ErpBatchRule.Apply(1000m, Fifty, ErpBatchRule.Reject, "WO", "kg"));
        Assert.Equal(new ErpQuantity(1025m, null, null), ErpBatchRule.Apply(1025m, null, ErpBatchRule.Reject, "WO", "kg"));
    }

    [Fact]
    public void Each_rule_says_what_it_did_and_reject_names_the_whole_quantities()
    {
        var refused = Assert.Throws<DomainRuleException>(() => ErpBatchRule.Apply(1025m, Fifty, ErpBatchRule.Reject, "WO-1", "kg"));
        Assert.Contains("Send 1,000 or 1,050 kg", refused.Message, StringComparison.Ordinal);

        Assert.Equal((1050m, BatchAdjustment.RoundedUp), Pick(ErpBatchRule.Apply(1025m, Fifty, ErpBatchRule.RoundUp, "WO", "kg")));
        Assert.Equal((1000m, BatchAdjustment.RoundedDown), Pick(ErpBatchRule.Apply(1025m, Fifty, ErpBatchRule.RoundDown, "WO", "kg")));
        Assert.Equal((1025m, BatchAdjustment.Held), Pick(ErpBatchRule.Apply(1025m, Fifty, ErpBatchRule.Hold, "WO", "kg")));
        Assert.Throws<DomainRuleException>(() => ErpBatchRule.Apply(30m, Fifty, ErpBatchRule.RoundDown, "WO", "kg"));

        // The material's own rule beats the site's; with neither, reject.
        Assert.Equal(ErpBatchRule.Hold, ErpBatchRule.For(ErpBatchRule.Hold, ErpBatchRule.RoundUp));
        Assert.Equal(ErpBatchRule.RoundUp, ErpBatchRule.For(null, ErpBatchRule.RoundUp));
        Assert.Equal(ErpBatchRule.Reject, ErpBatchRule.For(null, null));
    }

    private static (decimal, string?) Pick(ErpQuantity q) => (q.Quantity, q.Adjustment);
}
