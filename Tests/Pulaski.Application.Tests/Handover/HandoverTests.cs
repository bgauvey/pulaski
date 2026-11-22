using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Handover;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Handover;
using Pulaski.Domain;
using Pulaski.Domain.Shifts;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Handover;

/// <summary>Shift handover (#124): which shift is now and which came before, and entries kept and closed.</summary>
public class HandoverTests
{
    private static readonly DateOnly Day = new(2026, 11, 16);

    // Two eights, 06:00-14:00 and 14:00-22:00, every day, in UTC.
    private static EquipmentShifts TwoShifts(Guid line) => new(line, null, "EXT-01", TimeZoneInfo.Utc, Guid.NewGuid(), "2X8",
        new ShiftSchedule("2X8", "Two eights", 1, Day,
            [new("A", "Mornings", new TimeOnly(6, 0), TimeSpan.FromHours(8), [0]), new("B", "Afternoons", new TimeOnly(14, 0), TimeSpan.FromHours(8), [0])]));

    [Fact]
    public void Now_is_the_running_shift_and_before_it_the_one_that_ended_or_with_none_today_and_yesterday()
    {
        var line = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero);
        var (current, previous) = HandoverShifts.CurrentAndPrevious(TwoShifts(line), at);
        Assert.Equal(("B", new DateOnly(2026, 11, 20)), (current.ShiftCode, current.ProductionDate));
        Assert.Equal(("A", new DateOnly(2026, 11, 20)), (previous.ShiftCode, previous.ProductionDate));

        // In the morning the previous shift is yesterday afternoon's; at night (no shift) the next morning is current.
        var morning = HandoverShifts.CurrentAndPrevious(TwoShifts(line), at.AddHours(-8));
        Assert.Equal(("A", "B", new DateOnly(2026, 11, 19)), (morning.Current.ShiftCode, morning.Previous.ShiftCode, morning.Previous.ProductionDate));
        var night = HandoverShifts.CurrentAndPrevious(TwoShifts(line), at.AddHours(9));
        Assert.Equal(("A", new DateOnly(2026, 11, 21), "B", new DateOnly(2026, 11, 20)),
            (night.Current.ShiftCode, night.Current.ProductionDate, night.Previous.ShiftCode, night.Previous.ProductionDate));

        var plain = HandoverShifts.CurrentAndPrevious(null, at);
        Assert.Equal((null, new DateOnly(2026, 11, 20), new DateOnly(2026, 11, 19)), (plain.Current.ShiftCode, plain.Current.ProductionDate, plain.Previous.ProductionDate));
    }

    [Fact]
    public async Task A_note_stays_with_its_shift_and_an_open_item_is_carried_until_closed()
    {
        var site = Guid.NewGuid();
        var line = Guid.NewGuid();
        var stores = new InMemoryStores();
        stores.Handover.Lines[line] = new HandoverLine(line, site, "EXT-01", true, true);
        var services = new ServiceCollection().AddApplication();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 11, 20, 15, 0, 0, TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IRequestContext, FixedRequestContext>();
        stores.Register(services);
        await using var provider = services.BuildServiceProvider();
        async Task<T> Send<T>(ICommand<T> command)
        {
            await using var scope = provider.CreateAsyncScope();
            return await ((dynamic)scope.ServiceProvider.GetRequiredService(typeof(ICommandHandler<,>).MakeGenericType(command.GetType(), typeof(T)))).HandleAsync((dynamic)command, CancellationToken.None);
        }

        async Task<HandoverLineDto> Line()
        {
            await using var scope = provider.CreateAsyncScope();
            return (await scope.ServiceProvider.GetRequiredService<IQueryHandler<GetHandoverQuery, HandoverDto>>().HandleAsync(new GetHandoverQuery(site), default)).Lines.Single();
        }

        CreatedResponse note = await Send(new AddHandoverEntryCommand(line, HandoverKinds.Note, " Die changed at 13:00 "));
        CreatedResponse item = await Send(new AddHandoverEntryCommand(line, HandoverKinds.OpenItem, "Puller belt slipping; watch it"));
        await Assert.ThrowsAsync<Errors.ValidationException>(() => Send(new AddHandoverEntryCommand(line, HandoverKinds.Note, "  ")));
        await Assert.ThrowsAsync<DomainRuleException>(() => Send(new AddHandoverEntryCommand(line, "memo", "x")));

        var today = await Line();
        Assert.Equal(["Die changed at 13:00", "Puller belt slipping; watch it"], today.Current.Entries.Select(e => e.Body));
        Assert.Equal(item.Id, Assert.Single(today.OpenItems).Id);

        // Two days on, the note is gone from view but the item is still carried.
        clock.Advance(TimeSpan.FromDays(2));
        var later = await Line();
        Assert.Empty(later.Current.Entries);
        Assert.Empty(later.Previous.Entries);
        Assert.Equal(item.Id, Assert.Single(later.OpenItems).Id);

        await Send(new CloseHandoverItemCommand(item.Id, "Belt replaced"));
        Assert.Empty((await Line()).OpenItems);
        await Assert.ThrowsAsync<DomainRuleException>(() => Send(new CloseHandoverItemCommand(item.Id, null)));
        await Assert.ThrowsAsync<DomainRuleException>(() => Send(new CloseHandoverItemCommand(note.Id, null)));
    }
}
