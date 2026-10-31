using Pulaski.Domain;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Batches, whole-batch planning and partial batches (D-036).</summary>
public class BatchingTests
{
    private static readonly Batching Near = Batching.Create(50m, 2m, BatchRule.Near);
    private static readonly Batching Exact = Batching.Create(50m, 2m, BatchRule.Exact);

    [Fact]
    public void Plans_are_whole_batches_and_a_refusal_names_the_nearest()
    {
        Assert.Equal((2, 3, (int?)null), (Near.WholeBatches(100m), Near.WholeBatches(150m), Near.WholeBatches(125m)));

        var ex = Assert.Throws<DomainRuleException>(() => Near.EnsureWholeBatches(125m, "Work order WO-1", "kg"));
        Assert.Contains("125 kg is 2.5 batches of 50 kg", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Plan 100 or 150 kg.", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Plan at least 50 kg.", Assert.Throws<DomainRuleException>(() => Near.EnsureWholeBatches(20m, "Job J-1", "kg")).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(49.3, true)]
    [InlineData(50.9, true)]
    [InlineData(49, true)]
    [InlineData(48.9, false)]
    [InlineData(40, false)]
    [InlineData(51.1, false)]
    public void A_near_batch_may_differ_by_the_tolerance(double weight, bool isBatch) =>
        Assert.Equal(isBatch, Near.IsBatch((decimal)weight));

    [Fact]
    public void An_exact_batch_is_the_batch_size()
    {
        Assert.True(Exact.IsBatch(50m));
        Assert.False(Exact.IsBatch(49.3m));
        Assert.Contains("exactly 50 kg", Assert.Throws<DomainRuleException>(() => Exact.EnsureBatch(49.3m, "kg")).Message, StringComparison.Ordinal);
        Assert.Contains("49 to 51 kg", Assert.Throws<DomainRuleException>(() => Near.EnsureBatch(40m, "kg")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_partial_batch_is_less_than_a_batch()
    {
        Near.EnsurePartial(40m, "kg");
        Exact.EnsurePartial(49.3m, "kg");
        Assert.Throws<DomainRuleException>(() => Near.EnsurePartial(49.3m, "kg"));
        Assert.Throws<DomainRuleException>(() => Exact.EnsurePartial(0m, "kg"));
    }

    [Fact]
    public void Shares_are_whole_batches_and_replacements_round_up()
    {
        Assert.Equal([3, 2], Batching.Share(5, [60m, 40m]));
        Assert.Equal([2, 1, 1], Batching.Share(4, [50m, 25m, 25m]));
        Assert.Equal([1, 1], Batching.Share(2, [90m, 10m]));
        Assert.Throws<DomainRuleException>(() => Batching.Share(1, [50m, 50m]));
        Assert.Equal((100m, 50m, 100m), (Near.RoundUp(75m), Near.RoundUp(50m), Near.RoundUp(50.5m)));
    }

    [Fact]
    public void The_rule_defaults_from_the_type_and_only_exact_partials_wait()
    {
        Assert.Equal((BatchRule.Exact, BatchRule.Near), (BatchRule.DefaultFor("finished"), BatchRule.DefaultFor("intermediate")));
        Assert.Throws<DomainRuleException>(() => Batching.Create(0m, 2m, BatchRule.Near));
        Assert.Throws<DomainRuleException>(() => Batching.Create(50m, 60m, BatchRule.Near));

        var at = new DateTimeOffset(2026, 10, 17, 8, 0, 0, TimeSpan.Zero);
        PartialBatch Report(Batching b) => PartialBatch.Report(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 30m, Guid.CreateVersion7(), b, at, "op");

        var accepted = Report(Near);
        Assert.Equal(PartialBatchStatus.Accepted, accepted.Status);
        Assert.Throws<DomainRuleException>(() => accepted.Approve(at, "qa", null));

        var pending = Report(Exact);
        Assert.Equal(PartialBatchStatus.Approved, pending.Approve(at, "qa", "ok").Status);
        var rejected = pending.Reject(at, "qa", Guid.CreateVersion7(), null);
        Assert.Equal(PartialBatchStatus.Rejected, rejected.Status);
        Assert.Throws<DomainRuleException>(() => rejected.Approve(at, "qa", null));
    }

    [Fact]
    public void A_line_changes_the_size_and_tolerance_not_the_rule_and_shares_of_one_size_work_as_before()
    {
        var material = Batching.Create(50m, 2m, BatchRule.Near);
        var line = material.OnLine(200m);
        Assert.Equal((200m, 2m, BatchRule.Near), (line.Size, line.TolerancePct, line.Rule));
        Assert.Equal(material, material.OnLine(null));

        // A line's own tolerance (#85): 46 kg is a batch there, not on the material's 2%.
        var loose = material.OnLine(null, 10m);
        Assert.Equal((50m, 10m), (loose.Size, loose.TolerancePct));
        Assert.True(loose.IsBatch(46m));
        Assert.False(material.IsBatch(46m));
        Assert.Equal((100m, 0m, 50m), (material.RoundDown(125m), material.RoundDown(49m), material.RoundDown(50m)));
        Assert.Equal(4, line.WholeBatches(800m));
        Assert.Null(line.WholeBatches(850m));

        Assert.Equal([3, 2], Batching.ShareBySize(250m, [(60m, 50m), (40m, 50m)], "WO", "kg"));
        Assert.Contains("Plan 200 or 250", Assert.Throws<DomainRuleException>(() => Batching.ShareBySize(225m, [(60m, 50m), (40m, 50m)], "WO", "kg")).Message, StringComparison.Ordinal);
        Assert.Equal(1000m, Batching.ShareBySize(1000m, [(50m, 50m), (50m, 200m)], "WO", "kg").Zip([50m, 200m], (c, size) => c * size).Sum());
    }

    [Fact]
    public void A_capabilitys_batch_size_must_be_positive()
    {
        var change = new CapabilityChange(100m, Guid.CreateVersion7(), null, null, false, DateTimeOffset.UnixEpoch, null, BatchSize: 0m);
        Assert.Throws<DomainRuleException>(() => Capability.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), change));
        Assert.Equal(200m, Capability.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), change with { BatchSize = 200m }).BatchSize);
    }
}
