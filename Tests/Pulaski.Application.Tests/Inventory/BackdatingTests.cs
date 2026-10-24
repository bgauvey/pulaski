using Pulaski.Application.Errors;
using Pulaski.Application.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Units;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>Back-dated quantity movements (D-057): they count from an earlier time, are recorded now, and must fit the lot's history.</summary>
public class BackdatingTests
{
    private static readonly DateTimeOffset Now = HandlerFixture.Now;

    private readonly HandlerFixture _f = new();
    private readonly Guid _site = Guid.CreateVersion7();
    private readonly Unit _kg;
    private readonly Unit _m;
    private readonly InventoryLocation _stores;
    private readonly InventoryMaterial _resin;
    private readonly InventoryReason _count;
    private readonly InventoryJob _job;

    public BackdatingTests()
    {
        _kg = _f.Stores.Units.AddUnit("kg", "mass", 1m);
        _m = _f.Stores.Units.AddUnit("m", "length", 1m);
        _stores = Inventory.AddLocation("RM-STORE", _site);
        _resin = Inventory.AddMaterial("PVC-K67", _kg.Id, "kg", shelfLifeDays: 10);
        var profile = Inventory.AddMaterial("PRF-1", _m.Id, "m");
        _count = Inventory.AddReason(ReasonKinds.InventoryAdjustment, "COUNT");
        _job = Inventory.AddJob("WO-1", _site, profile.Id, status: "running", uomId: _m.Id, plannedQty: 1000m);
        Inventory.PlanComponent(_job.Id, _resin.Id);
    }

    private InMemoryInventory Inventory => _f.Stores.Inventory;

    private Task<Contracts.Inventory.LotTransactionResponse> ReceiveAsync(string lot, decimal qty, DateTimeOffset? at, string? comment = "Paperwork came late") =>
        new ReceiveLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ReceiveLotCommand(_resin.Id, _stores.Id, lot, qty, _kg.Id, Comment: comment, EffectiveAt: at), CancellationToken.None);

    private Task<Contracts.Inventory.LotTransactionResponse> ConsumeAsync(Guid lot, decimal qty, DateTimeOffset? at) =>
        new ConsumeLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Execution, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new ConsumeLotCommand(lot, _job.Id, qty, _kg.Id, at is null ? null : "Entered late", EffectiveAt: at), CancellationToken.None);

    private Task<Contracts.Inventory.LotTransactionResponse> WriteOffAsync(Guid lot, decimal qty, DateTimeOffset? at) =>
        new WriteOffLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new WriteOffLotCommand(lot, Inventory.Find(lot)!.Version, qty, _kg.Id, _count.Id, "Spill found late", at), CancellationToken.None);

    [Fact]
    public void The_effective_time_is_now_unless_given_and_then_within_31_days_with_a_reason()
    {
        Assert.Equal(Now, Backdating.EffectiveAt(null, Now, null));
        Assert.Equal(Now, Backdating.EffectiveAt(Now.AddSeconds(30), Now, null)); // clock skew counts as now
        Assert.Equal(Now.AddDays(-31), Backdating.EffectiveAt(Now.AddDays(-31), Now, "Late"));

        Assert.Contains("EffectiveAt", Assert.Throws<ValidationException>(() => Backdating.EffectiveAt(Now.AddHours(1), Now, "x")).Errors.Keys);
        Assert.Contains("EffectiveAt", Assert.Throws<ValidationException>(() => Backdating.EffectiveAt(Now.AddDays(-32), Now, "x")).Errors.Keys);
        Assert.Contains("Comment", Assert.Throws<ValidationException>(() => Backdating.EffectiveAt(Now.AddDays(-1), Now, " ")).Errors.Keys);
    }

    [Fact]
    public async Task A_back_dated_receipt_counts_then_is_recorded_now_and_its_expiry_runs_from_then()
    {
        var received = await ReceiveAsync("R-1", 100m, Now.AddDays(-3));

        var row = Assert.Single(Inventory.Events);
        Assert.Equal(Now.AddDays(-3), row.EffectiveAt);
        Assert.Equal(Now.AddDays(7), Inventory.Find(received.LotId)!.Value.ExpiresAt);
        Assert.Equal(Now, _f.Stores.Events.Appended[^1].OccurredAt);
    }

    [Fact]
    public async Task A_back_dated_movement_must_fit_the_lots_history()
    {
        var received = await ReceiveAsync("R-1", 100m, Now.AddDays(-5));
        _f.Time.Advance(TimeSpan.FromMinutes(1));
        await new WriteUpLotHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Units, _f.Stores.Events, _f.Events, _f.Ids, _f.Time)
            .HandleAsync(new WriteUpLotCommand(received.LotId, Inventory.Find(received.LotId)!.Version, 50m, _kg.Id, _count.Id), CancellationToken.None);

        // 150 now, but only 100 three days ago: taking 120 then would have gone 20 below zero.
        var short_ = await Assert.ThrowsAsync<DomainRuleException>(() => WriteOffAsync(received.LotId, 120m, Now.AddDays(-3)));
        Assert.Contains("20 kg short", short_.Message, StringComparison.Ordinal);
        await WriteOffAsync(received.LotId, 80m, Now.AddDays(-3));

        var early = await Assert.ThrowsAsync<DomainRuleException>(() => WriteOffAsync(received.LotId, 5m, Now.AddDays(-6)));
        Assert.Contains("had no stock until", early.Message, StringComparison.Ordinal);

        Inventory.CountedAt[received.LotId] = Now.AddDays(-2);
        var counted = await Assert.ThrowsAsync<DomainRuleException>(() => WriteOffAsync(received.LotId, 1m, Now.AddDays(-3)));
        Assert.Contains("counted", counted.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Back_dated_consumption_goes_into_the_run_going_on_then_and_is_checked_for_expiry_then()
    {
        var earlier = _f.Stores.Execution.OpenRun(_job.Id, _job.EquipmentId, Now.AddDays(-4), _m.Id);
        _f.Stores.Execution.EndRun(earlier.Id, Now.AddDays(-3));
        var current = _f.Stores.Execution.OpenRun(_job.Id, _job.EquipmentId, Now.AddHours(-2), _m.Id);
        var lot = await ReceiveAsync("R-1", 100m, Now.AddDays(-12)); // expired two days ago
        _f.Time.Advance(TimeSpan.FromMinutes(1));

        await Assert.ThrowsAsync<DomainRuleException>(() => ConsumeAsync(lot.LotId, 10m, null));
        await ConsumeAsync(lot.LotId, 10m, Now.AddDays(-3.5));

        var consumed = Inventory.Events[^1];
        Assert.Equal((earlier.Id, Now.AddDays(-3.5)), (consumed.RunId!.Value, consumed.EffectiveAt!.Value));
        var gap = await Assert.ThrowsAsync<DomainRuleException>(() => ConsumeAsync(lot.LotId, 1m, Now.AddDays(-1)));
        Assert.Contains("wasn't running", gap.Message, StringComparison.Ordinal);
        Assert.NotEqual(current.Id, consumed.RunId);
    }

    [Fact]
    public async Task Reversing_a_back_dated_movement_undoes_it_when_it_counted()
    {
        var lot = await ReceiveAsync("R-1", 100m, null);
        _f.Time.Advance(TimeSpan.FromHours(1));
        await WriteOffAsync(lot.LotId, 30m, Now.AddMinutes(30));
        var latest = (await Inventory.GetLatestEventAsync(lot.LotId, CancellationToken.None))!;

        await new ReverseMovementHandler(_f.Stores.UnitOfWork, Inventory, _f.Stores.Events, _f.Events, _f.Ids)
            .HandleAsync(new ReverseMovementCommand(lot.LotId, Inventory.Find(lot.LotId)!.Version, latest.Id, _count.Id), CancellationToken.None);

        Assert.Equal(Now.AddMinutes(30), Inventory.Events[^1].EffectiveAt);
        Assert.Equal(100m, Inventory.Find(lot.LotId)!.Value.Quantity);
    }
}
