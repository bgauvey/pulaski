using Pulaski.Application.Execution;
using Pulaski.Application.Labor;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Tests.Domain;

/// <summary>Reports filling a line's lots until full (D-104).</summary>
public class LotCuttingTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 19, 6, 0, 0, TimeSpan.Zero);

    private static OpenLot Open(decimal quantity, DateTimeOffset? started = null) => new(Guid.CreateVersion7(), "J-01", quantity, started ?? Start);

    [Fact]
    public void A_report_fills_the_open_lot_then_starts_lots_of_the_size_for_the_rest()
    {
        var rule = new LotCutRule(100m, null, false);

        Assert.Equal([new LotPart("J-01", 30m)], LotCutting.Parts(rule, Open(60m), 30m, Start, null, null));
        Assert.Equal([new LotPart("J-01", 40m), new LotPart(null, 100m), new LotPart(null, 50m)], LotCutting.Parts(rule, Open(60m), 190m, Start, null, null));

        // Full: nothing more goes in; with no lot open, the first is new too.
        Assert.Equal([new LotPart(null, 20m)], LotCutting.Parts(rule, Open(100m), 20m, Start, null, null));
        Assert.Equal([new LotPart(null, 100m), new LotPart(null, 5m)], LotCutting.Parts(rule, null, 105m, Start, null, null));
        Assert.Equal("lot J-01 is full (100)", LotCutting.WhyCut(rule, Open(100m), Start, null, null));
    }

    [Fact]
    public void A_lot_ends_at_its_age_or_a_change_of_shift_and_without_a_size_takes_everything()
    {
        var aged = new LotCutRule(null, 8m, false);
        Assert.Equal([new LotPart("J-01", 5000m)], LotCutting.Parts(aged, Open(10m), 5000m, Start.AddHours(7.9), null, null));
        Assert.Equal([new LotPart(null, 5m)], LotCutting.Parts(aged, Open(10m), 5m, Start.AddHours(8), null, null));
        Assert.Contains("8 h or more ago", LotCutting.WhyCut(aged, Open(10m), Start.AddHours(9), null, null), StringComparison.Ordinal);

        var shifts = new LotCutRule(null, null, true);
        Assert.Null(LotCutting.WhyCut(shifts, Open(10m), Start, "2026-11-19/A", "2026-11-19/A"));
        Assert.Equal([new LotPart(null, 5m)], LotCutting.Parts(shifts, Open(10m), 5m, Start, "2026-11-19/A", "2026-11-19/B"));
    }

    [Fact]
    public void Rules_are_checked_and_a_report_starts_at_most_a_hundred_lots()
    {
        Assert.False(LotCutRule.Off.IsOn);
        Assert.NotNull(new LotCutRule(0m, null, false).Problem());
        Assert.NotNull(new LotCutRule(null, 721m, false).Problem());
        Assert.Null(new LotCutRule(50m, 720m, true).Problem());
        Assert.Throws<DomainRuleException>(() => LotCutting.Parts(new LotCutRule(1m, null, false), null, 101m, Start, null, null));
        Assert.Equal(100, LotCutting.Parts(new LotCutRule(1m, null, false), null, 100m, Start, null, null).Count);

        // On the capability: kept only when on, so clearing a rule leaves none.
        var change = new CapabilityChange(100m, Guid.CreateVersion7(), null, null, false, Start, null, LotRule: new LotCutRule(100m, null, false));
        var capability = Capability.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), change);
        Assert.Equal(100m, capability.LotCuts.Size);
        Assert.Null(capability.Edit(change with { LotRule = LotCutRule.Off }).LotRule);
        Assert.Throws<DomainRuleException>(() => capability.Edit(change with { LotRule = new LotCutRule(-1m, null, false) }));
    }

    [Fact]
    public void Shifts_are_told_apart_by_production_day_and_code_else_by_the_day()
    {
        Assert.Equal("2026-11-19", LotFilling.ShiftKey(null, Start));
        var noSchedule = new EquipmentShifts(Guid.CreateVersion7(), null, "EXT-01", TimeZoneInfo.Utc, null, null, null);
        Assert.Equal("2026-11-19", LotFilling.ShiftKey(noSchedule, Start.AddHours(17)));
        Assert.Equal("2026-11-20", LotFilling.ShiftKey(noSchedule, Start.AddHours(18)));
    }
}
