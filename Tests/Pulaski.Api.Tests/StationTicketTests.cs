using Microsoft.Extensions.Options;
using Pulaski.Api.Security;
using Pulaski.Domain.Labor;

namespace Pulaski.Api.Tests;

/// <summary>An operator station's sign-on (#146): a ticket for StationIdle, after which the station waits again.</summary>
public class StationTicketTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void A_station_ticket_lasts_the_idle_time_and_then_reads_as_no_one()
    {
        var clock = new Clock(DateTimeOffset.UtcNow);
        var tickets = new BadgeTickets(Options.Create(new SecurityOptions { SigningKey = new string('k', 40) }), clock);
        Guid terminal = Guid.CreateVersion7(), other = Guid.CreateVersion7(), person = Guid.CreateVersion7();

        var (ticket, expires) = tickets.Issue(terminal, person, TerminalRules.StationIdle);

        Assert.Equal(clock.Now + TerminalRules.StationIdle, expires);
        Assert.Equal(person, tickets.PersonOrNull(ticket, terminal));
        Assert.Null(tickets.PersonOrNull(ticket, other));
        Assert.Null(tickets.PersonOrNull("not a ticket", terminal));

        // A kiosk ticket keeps its two minutes.
        Assert.Equal(clock.Now + TerminalRules.TicketLifetime, tickets.Issue(terminal, person).ExpiresAt);
    }
}
