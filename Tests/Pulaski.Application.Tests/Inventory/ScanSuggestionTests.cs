using Pulaski.Application.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>A scan at a line offers what fits, the likely action first (#135).</summary>
public class ScanSuggestionTests
{
    private static readonly Guid Line = Guid.CreateVersion7();
    private static readonly Guid Feed = Guid.CreateVersion7();
    private static readonly Guid Job = Guid.CreateVersion7();
    private static readonly Guid Resin = Guid.CreateVersion7();
    private static readonly Guid Kg = Guid.CreateVersion7();

    private static ScanLine Running(bool feed = true) =>
        new(Line, "EXT-01", Guid.CreateVersion7(), false, Job, "J-1", Guid.CreateVersion7(), feed ? [(Feed, "EXT-01-HOP")] : []);

    private static ScanLot Lot(string status = "available", decimal qty = 500m, Guid? at = null) =>
        new(Guid.CreateVersion7(), "R-1", Resin, "RESIN", status, qty, Kg, "kg", at, false, 7);

    [Fact]
    public void A_lot_the_job_plans_is_consumed_first_for_what_the_job_still_needs_then_loaded_at_the_feed_point()
    {
        var actions = ScanSuggestions.ForLot(Running(), Lot(), [new ScanComponent(Resin, 300m, Kg, 120m)], 1m);
        Assert.Equal(["consume", "load"], actions.Select(a => a.Kind));
        Assert.Equal((180m, 500m, Job, (string?)null), (actions[0].Quantity!.Value, actions[0].Available!.Value, actions[0].JobId!.Value, actions[0].Warning?.Text));
        Assert.Equal(Feed, actions[1].ToId);

        // Capped at the lot; already at the feed point, it isn't loaded again.
        var small = ScanSuggestions.ForLot(Running(), Lot(qty: 50m, at: Feed), [new ScanComponent(Resin, 300m, Kg, 120m)], 1m);
        Assert.Equal(50m, Assert.Single(small).Quantity);

        // Planned in tonnes: converted to the lot's unit.
        Assert.Equal(500m, ScanSuggestions.ForLot(Running(), Lot(), [new ScanComponent(Resin, 2m, Guid.CreateVersion7(), 0m)], 1000m)[0].Quantity);
    }

    [Fact]
    public void Off_plan_or_used_up_components_are_offered_whole_with_a_warning_and_held_lots_only_moved()
    {
        var offPlan = ScanSuggestions.ForLot(Running(), Lot(), [], null)[0];
        Assert.Equal((500m, true), (offPlan.Quantity!.Value, offPlan.Warning!.Text.Contains("doesn't plan", StringComparison.Ordinal)));
        var usedUp = ScanSuggestions.ForLot(Running(), Lot(), [new ScanComponent(Resin, 100m, Kg, 100m)], 1m)[0];
        Assert.Contains("already used", usedUp.Warning!.Text, StringComparison.Ordinal);

        Assert.Equal(["load"], ScanSuggestions.ForLot(Running(), Lot("on_hold"), [], null).Select(a => a.Kind));
        Assert.Empty(ScanSuggestions.ForLot(Running(), Lot("consumed"), [], null));
        var idle = new ScanLine(Line, "EXT-01", Guid.CreateVersion7(), true, null, null, null, []);
        Assert.Equal(["move"], ScanSuggestions.ForLot(idle, Lot(), [], null).Select(a => a.Kind));
        Assert.Empty(ScanSuggestions.ForLot(idle, Lot(at: Line), [], null));
    }

    [Fact]
    public void A_shop_order_barcode_starts_its_job_here_when_it_may_be_started()
    {
        Assert.Equal(new ShopOrderCode("WO-1", 20), ShopOrderCode.Parse(" WO-1|OP-20 "));
        Assert.Equal(new ShopOrderCode("WO-1", null), ShopOrderCode.Parse("WO-1|OP-NA"));
        Assert.Null(ShopOrderCode.Parse("PUL1L42"));
        Assert.Null(ShopOrderCode.Parse("WO-1|OP-x"));

        ScanJob J(string status, bool onLine = true, bool above = false) => new(Guid.CreateVersion7(), "J-2", status, 3, Guid.CreateVersion7(), onLine, above);
        var idle = new ScanLine(Line, "EXT-01", Guid.CreateVersion7(), false, null, null, null, []);
        Assert.Single(ScanSuggestions.ForJobs(idle, [J("firmed"), J("completed"), J("dispatched", onLine: false)]));
        var grouped = Assert.Single(ScanSuggestions.ForJobs(idle, [J("dispatched", onLine: false, above: true)]));
        Assert.Equal((Line, (string?)null), (grouped.ToId!.Value, grouped.Warning?.Text));
        Assert.Contains("stop it first", ScanSuggestions.ForJobs(Running(), [J("paused")])[0].Warning!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_container_moves_to_the_line_or_its_feed_points_unless_it_is_there()
    {
        var container = new ScanContainer(Guid.CreateVersion7(), "PAL-1", Feed, 2);
        Assert.Empty(ScanSuggestions.ForContainer(Running(), container));
        var line = new ScanLine(Line, "EXT-01", Guid.CreateVersion7(), true, null, null, null, [(Feed, "EXT-01-HOP")]);
        Assert.Equal([Line], ScanSuggestions.ForContainer(line, container).Select(a => a.ToId!.Value));
    }
}

/// <summary>The station confirms the offered action it worked out again, and says what it did (#135b).</summary>
public class ScanActionChoiceTests
{
    [Fact]
    public void The_confirmed_action_is_found_by_its_kind_and_target_and_worded_when_done()
    {
        var hopper = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        Contracts.Inventory.ScanActionDto Load(Guid to) => new() { Kind = "load", Permission = "lot.move", LotLabel = "R-1", ToId = to, ToCode = to == hopper ? "HOP-1" : "HOP-2" };
        var consume = new Contracts.Inventory.ScanActionDto { Kind = "consume", Permission = "production.report", LotLabel = "R-1", JobNumber = "J-1", UomCode = "kg", Quantity = 5m };
        IReadOnlyList<Contracts.Inventory.ScanActionDto> offered = [consume, Load(hopper), Load(other)];

        Assert.Same(consume, ScanActionChoice.Find(offered, "consume", null, null));
        Assert.Equal(other, ScanActionChoice.Find(offered, "load", other, null)!.ToId);
        Assert.Null(ScanActionChoice.Find(offered, "start", null, null));
        Assert.Equal("250 kg of R-1 consumed into J-1.", ScanActionChoice.Done(consume, 250m).Text);
        Assert.Equal("R-1 loaded at HOP-1.", ScanActionChoice.Done(Load(hopper), 0m).Text);
    }
}
