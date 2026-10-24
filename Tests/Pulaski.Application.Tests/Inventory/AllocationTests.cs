using Pulaski.Application.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Allocating lots to jobs and which to use first (D-064).</summary>
public class AllocationTests
{
    private static readonly DateTimeOffset Now = HandlerFixture.Now;

    private static AllocationCandidate C(string label, decimal qty, int? expiresInDays = null, int receivedDaysAgo = 10, string status = LotStatus.Available, decimal others = 0m) =>
        new(Guid.CreateVersion7(), label, status, qty, others, 0m, expiresInDays is { } d ? Now.AddDays(d) : null, Now.AddDays(-receivedDaysAgo));

    [Fact]
    public void The_first_to_expire_goes_first_then_the_oldest_and_every_lot_left_out_says_why()
    {
        var noExpiryOld = C("OLD", 100m, receivedDaysAgo: 30);
        var noExpiryNew = C("NEW", 100m, receivedDaysAgo: 2);
        var soon = C("SOON", 50m, expiresInDays: 5);
        var later = C("LATER", 50m, expiresInDays: 40);
        var tooSoon = C("GONE", 80m, expiresInDays: 1);
        var held = C("HELD", 80m, status: LotStatus.OnHold);
        var taken = C("TAKEN", 40m, others: 40m);

        var (picks, excluded) = LotRecommendation.Recommend(180m, [noExpiryNew, later, held, noExpiryOld, taken, soon, tooSoon], Now.AddDays(3), "kg");

        Assert.Equal([("SOON", 50m), ("LATER", 50m), ("OLD", 80m)], picks.Select(p => (p.Lot.Label, p.Quantity)));
        Assert.Contains("the earliest", picks[0].Reason, StringComparison.Ordinal);
        Assert.StartsWith("No expiry", picks[2].Reason, StringComparison.Ordinal);
        Assert.Contains("before the job needs it", excluded.Single(e => e.Lot.Label == "GONE").Reason, StringComparison.Ordinal);
        Assert.Contains("on hold", excluded.Single(e => e.Lot.Label == "HELD").Reason, StringComparison.Ordinal);
        Assert.Contains("other jobs", excluded.Single(e => e.Lot.Label == "TAKEN").Reason, StringComparison.Ordinal);
        Assert.Contains("Not needed", excluded.Single(e => e.Lot.Label == "NEW").Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void An_allocation_is_used_up_trimmed_and_released_and_says_when_it_no_longer_holds()
    {
        var allocation = new LotAllocation(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            60m, 60m, AllocationStatus.Active, AllocationBasis.Chosen);
        var (part, used) = allocation.UseUp(20m);
        Assert.Equal((40m, 20m, AllocationStatus.Active), (part.Quantity, used, part.Status));
        Assert.Equal(AllocationStatus.Consumed, part.UseUp(100m).Allocation.Status);
        Assert.Equal((100m, 100m), (allocation.TopUp(40m).Quantity, allocation.TopUp(40m).AllocatedQty));
        Assert.Equal(25m, allocation.TrimTo(25m).Quantity);
        Assert.Equal(AllocationStatus.Released, allocation.Release("not needed").Status);
        Assert.Throws<DomainRuleException>(() => allocation.Release(null).Release(null));

        var lot = C("L", 50m, others: 20m);
        Assert.Contains("only 30 kg", LotRecommendation.AllocationProblem(allocation, lot, Now, "kg"), StringComparison.Ordinal);
        Assert.Null(LotRecommendation.AllocationProblem(allocation with { Quantity = 30m }, lot, Now, "kg"));
    }

    [Fact]
    public async Task Consuming_into_another_job_what_is_allocated_is_allowed_with_a_warning_and_the_own_job_uses_its_allocation()
    {
        var allocations = new InMemoryAllocations();
        var site = Guid.CreateVersion7();
        var jobA = Guid.CreateVersion7();
        var jobB = Guid.CreateVersion7();
        var lot = new Lot(Guid.CreateVersion7(), site, Guid.CreateVersion7(), "R-1", LotStatus.Available, 100m, Guid.CreateVersion7(), Guid.CreateVersion7(), false, LotOrigin.Received);
        allocations.Sources[jobA] = new JobAllocationSource(new AllocationJob(jobA, "WO-A", site, "running", null), [], [], []);
        await allocations.InsertAsync(
            new LotAllocation(Guid.CreateVersion7(), site, jobA, lot.MaterialId, lot.Id, 80m, 80m, AllocationStatus.Active, AllocationBasis.Chosen), "x", Now, CancellationToken.None);

        var (_, warning) = await AllocationUse.ConsumeAsync(allocations, lot, jobB, 30m, "op", Now, "kg", CancellationToken.None);
        Assert.Contains("allocated to WO-A", warning!.Message, StringComparison.Ordinal);
        Assert.Null((await AllocationUse.ConsumeAsync(allocations, lot, jobB, 20m, "op", Now, "kg", CancellationToken.None)).Warning);

        var (used, own) = await AllocationUse.ConsumeAsync(allocations, lot, jobA, 50m, "op", Now, "kg", CancellationToken.None);
        Assert.Equal(50m, used);
        Assert.Null(own);
        Assert.Equal(30m, Assert.Single(allocations.All).Quantity);
    }
}
