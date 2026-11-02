using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
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

    [Fact]
    public void Preferences_order_lots_expiring_the_same_day_but_never_beat_an_earlier_expiry()
    {
        var day = Now.Date.AddDays(20);
        AllocationCandidate At(string label, decimal qty, double hours, bool nearLine = false, bool inContainer = false, int receivedDaysAgo = 10) =>
            new(Guid.CreateVersion7(), label, LotStatus.Available, qty, 0m, 0m, new DateTimeOffset(day.AddHours(hours), TimeSpan.Zero), Now.AddDays(-receivedDaysAgo), nearLine, inContainer);
        var first = new AllocationCandidate(Guid.CreateVersion7(), "FIRST", LotStatus.Available, 10m, 0m, 0m, Now.AddDays(5), Now.AddDays(-1));
        var store = At("STORE", 500m, 1, receivedDaysAgo: 30);
        var line = At("LINE", 500m, 20, nearLine: true);
        var crate = At("CRATE", 500m, 10, inContainer: true);
        var part = At("PART", 40m, 5);

        string[] Order(params string[] preferences) =>
            [.. LotRecommendation.Recommend(2000m, [store, line, crate, part, first], Now, "kg", preferences).Picks.Select(p => p.Lot.Label)];

        Assert.Equal(["FIRST", "STORE", "CRATE", "LINE", "PART"], Order());
        Assert.Equal(["FIRST", "LINE", "STORE", "CRATE", "PART"], Order(AllocationPreferences.NearLine));
        Assert.Equal(["FIRST", "CRATE", "LINE", "STORE", "PART"], Order(AllocationPreferences.InContainer, AllocationPreferences.NearLine));
        Assert.Equal(["FIRST", "PART", "STORE", "CRATE", "LINE"], Order(AllocationPreferences.PartLotsFirst));

        var whole = LotRecommendation.Recommend(450m, [part, store, line], Now, "kg", [AllocationPreferences.WholeLot]).Picks;
        Assert.Equal("STORE", whole[0].Lot.Label);
        Assert.Contains("covers the rest alone", whole[0].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Approved_alternates_cover_at_their_ratio_what_the_component_cant_and_count_as_the_component()
    {
        var site = Guid.CreateVersion7();
        var job = new AllocationJob(Guid.CreateVersion7(), "WO-1", site, "planned", null);
        var resin = Guid.CreateVersion7();
        var kg = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        var third = Guid.CreateVersion7();
        var own = new AllocationCandidate(Guid.CreateVersion7(), "OWN", LotStatus.Available, 60m, 0m, 0m, null, Now.AddDays(-3));
        var alt = new AllocationCandidate(Guid.CreateVersion7(), "ALT", LotStatus.Available, 500m, 0m, 0m, null, Now.AddDays(-3));
        var spare = new AllocationCandidate(Guid.CreateVersion7(), "SPARE", LotStatus.Available, 500m, 0m, 0m, null, Now.AddDays(-3));
        var source = new JobAllocationSource(
            job,
            [new AllocationNeedRow(resin, "RESIN", "Resin", kg, "kg", 100m, kg, 0m)],
            [],
            [new AllocationLot(resin, own, "RM"), new AllocationLot(second, alt, "RM"), new AllocationLot(third, spare, "RM")],
            [new AllocationAlternate(resin, third, "RESIN3", kg, "kg", 1m, 2), new AllocationAlternate(resin, second, "RESIN2", kg, "kg", 1.5m, 1)]);
        var units = new[] { new Pulaski.Domain.Units.Unit(kg, "kg", "mass", 1m) };

        var plan = Assert.Single(AllocationPlanner.Plan(source, units, [], Now));
        Assert.Equal([("OWN", 60m), ("ALT", 60m)], plan.Picks.Select(p => (p.Pick.Lot.Label, p.Pick.Quantity)));
        Assert.Equal(100m, plan.Suggested);
        Assert.StartsWith("Alternate for RESIN (1.5 kg per kg)", plan.Picks[1].Pick.Reason, StringComparison.Ordinal);

        // Allocated, 60 kg of RESIN2 covers 40 kg of the component.
        var allocation = new LotAllocation(Guid.CreateVersion7(), site, job.Id, second, alt.LotId, 60m, 60m, AllocationStatus.Active, AllocationBasis.Recommended,
            ForMaterialId: resin, Ratio: 1.5m);
        Assert.Equal((resin, 40m), (allocation.ComponentId, allocation.Covers));
        var covered = Assert.Single(AllocationPlanner.Plan(source with { Allocations = [new HeldAllocation(allocation, "WO-1")] }, units, [], Now));
        Assert.Equal((40m, 60m), (covered.Allocated, covered.Remaining));
    }

    [Fact]
    public async Task Under_hard_reservation_consuming_what_another_job_holds_is_refused_and_names_the_job()
    {
        var allocations = new InMemoryAllocations();
        var site = Guid.CreateVersion7();
        var jobA = Guid.CreateVersion7();
        var lot = new Lot(Guid.CreateVersion7(), site, Guid.CreateVersion7(), "R-1", LotStatus.Available, 100m, Guid.CreateVersion7(), Guid.CreateVersion7(), false, LotOrigin.Received);
        allocations.Sources[jobA] = new JobAllocationSource(new AllocationJob(jobA, "WO-A", site, "running", null), [], [], []);
        allocations.SetSettings(AllocationSettings.Default(site) with { HardReservation = true });
        await allocations.InsertAsync(
            new LotAllocation(Guid.CreateVersion7(), site, jobA, lot.MaterialId, lot.Id, 80m, 80m, AllocationStatus.Active, AllocationBasis.Chosen), "x", Now, CancellationToken.None);

        // What no one holds can still be taken; more than that is refused.
        Assert.Null((await AllocationUse.ConsumeAsync(allocations, lot, Guid.CreateVersion7(), 20m, "op", Now, "kg", CancellationToken.None)).Warning);
        var refused = await Assert.ThrowsAsync<DomainRuleException>(() =>
            AllocationUse.ConsumeAsync(allocations, lot, Guid.CreateVersion7(), 30m, "op", Now, "kg", CancellationToken.None));
        Assert.Contains("10 kg of Lot R-1 is reserved for WO-A", refused.Message, StringComparison.Ordinal);
        Assert.Equal(20m, (await AllocationUse.ConsumeAsync(allocations, lot, jobA, 20m, "op", Now, "kg", CancellationToken.None)).Used);
    }

    [Fact]
    public async Task Settings_take_known_preferences_once_each_and_refuse_a_stale_version()
    {
        var fixture = new HandlerFixture();
        var site = Guid.CreateVersion7();
        var handler = new SetAllocationSettingsHandler(fixture.Stores.UnitOfWork, fixture.Stores.Allocations, fixture.Stores.Events, fixture.Events);

        await Assert.ThrowsAsync<ValidationException>(() => handler.HandleAsync(new SetAllocationSettingsCommand(site, 0, false, false, ["nearest"]), CancellationToken.None));
        await Assert.ThrowsAsync<ValidationException>(() => handler.HandleAsync(
            new SetAllocationSettingsCommand(site, 0, false, false, [AllocationPreferences.NearLine, AllocationPreferences.NearLine]), CancellationToken.None));

        var saved = await handler.HandleAsync(new SetAllocationSettingsCommand(site, 0, true, false, [AllocationPreferences.WholeLot]), CancellationToken.None);
        Assert.True((await fixture.Stores.Allocations.GetSettingsAsync(site, CancellationToken.None)).KeepCurrent);
        await Assert.ThrowsAsync<ConflictException>(() => handler.HandleAsync(new SetAllocationSettingsCommand(site, 0, false, false, []), CancellationToken.None));
        await handler.HandleAsync(new SetAllocationSettingsCommand(site, saved.Version, false, true, []), CancellationToken.None);
        Assert.True((await fixture.Stores.Allocations.GetSettingsAsync(site, CancellationToken.None)).HardReservation);
    }

    [Fact]
    public async Task The_background_refresh_does_the_sites_due_marks_each_and_logs_rather_than_stops_on_one_that_fails()
    {
        var fixture = new HandlerFixture();
        var (good, bad) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        fixture.Stores.Allocations.DueSites.AddRange([bad, good]);
        var refreshed = new List<Guid>();
        var refresher = new AllocationAutoRefresh(fixture.Stores.UnitOfWork, fixture.Stores.Allocations, new StubRefresh(bad, refreshed), fixture.Time);

        Assert.Equal(2, await refresher.RefreshDueAsync(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), CancellationToken.None));
        Assert.Equal([good], refreshed);
        Assert.Equal(Now, (await fixture.Stores.Allocations.GetSettingsAsync(bad, CancellationToken.None)).RefreshedAt);
        Assert.Empty(fixture.Stores.Allocations.DueSites);
    }

    private sealed class StubRefresh(Guid failing, List<Guid> refreshed) : ICommandHandler<RefreshSiteAllocationsCommand, Contracts.Inventory.AllocationChangeResponse>
    {
        public Task<Contracts.Inventory.AllocationChangeResponse> HandleAsync(RefreshSiteAllocationsCommand command, CancellationToken cancellationToken)
        {
            if (command.SiteId == failing)
            {
                throw new DomainRuleException("Something about that site.");
            }

            refreshed.Add(command.SiteId);
            return Task.FromResult(new Contracts.Inventory.AllocationChangeResponse());
        }
    }
}
