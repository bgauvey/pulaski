using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Pulaski.Api.Infrastructure;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Security;
using Pulaski.Domain;
using Pulaski.Testing;

namespace Pulaski.Api.Tests;

/// <summary>Acting at an operator station (#150): the badged person's user, with their role at the terminal's site, the terminal as source.</summary>
public class StationActingTests
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly Guid Site = Guid.CreateVersion7();
    private readonly Clock _clock = new(DateTimeOffset.UtcNow);
    private readonly TerminalRecord _terminal = new(Guid.CreateVersion7(), Site, "ST-1", "EXT-01 station", Site, null, "badge", true, DateTimeOffset.UtcNow);

    private (DefaultHttpContext Http, BadgeTickets Tickets) Station()
    {
        var http = new DefaultHttpContext();
        http.Items[TerminalFilter.ItemKey] = _terminal;
        return (http, new BadgeTickets(Options.Create(new SecurityOptions { SigningKey = new string('k', 40) }), _clock));
    }

    private static UserAccess Access(string role, Guid site, bool active = true) =>
        new(Guid.CreateVersion7(), "olu", "Olu", "pulaski-local|olu", active,
            [new RoleGrant(Guid.CreateVersion7(), role, role, site, [.. Roles.Defaults.Single(r => r.Code == role).Permissions])]);

    [Fact]
    public async Task The_persons_user_acts_with_their_role_here_and_events_name_them_with_the_station_as_source()
    {
        var (http, tickets) = Station();
        var security = new InMemorySecurity();
        var person = Guid.CreateVersion7();
        security.PersonAccess[person] = Access(Roles.Operator, Site);
        var (ticket, _) = tickets.Issue(_terminal.Id, person, TerminalRules.StationIdle);

        var (_, access, renewed, expires) = await StationActing.BeginAsync(http, tickets, security, _clock, ticket, Permissions.JobExecute, UserMessage.Of("start jobs"));

        Assert.Equal("olu", access.Username);
        Assert.Equal(_clock.GetUtcNow() + TerminalRules.StationIdle, expires);
        Assert.Equal(person, tickets.PersonOf(renewed, _terminal.Id));
        var accessor = new HttpContextAccessor { HttpContext = http };
        var context = new HttpRequestContext(accessor, new SequentialIds());
        Assert.Equal(("pulaski-local|olu", EventSources.Station("ST-1"), access.UserId), (context.ActorId, context.Source, context.UserId));
    }

    [Fact]
    public async Task Without_a_user_or_the_permission_here_it_is_refused_and_an_old_ticket_must_badge_again()
    {
        var (http, tickets) = Station();
        var security = new InMemorySecurity();
        Guid noUser = Guid.CreateVersion7(), planner = Guid.CreateVersion7(), elsewhere = Guid.CreateVersion7(), off = Guid.CreateVersion7();
        security.PersonAccess[planner] = Access(Roles.Planner, Site);
        security.PersonAccess[elsewhere] = Access(Roles.Operator, Guid.CreateVersion7());
        security.PersonAccess[off] = Access(Roles.Operator, Site, active: false);

        Task Act(Guid person) =>
            StationActing.BeginAsync(http, tickets, security, _clock, tickets.Issue(_terminal.Id, person, TerminalRules.StationIdle).Ticket, Permissions.JobExecute, UserMessage.Of("start jobs"));

        Assert.Contains("no Pulaski user", (await Assert.ThrowsAsync<ForbiddenException>(() => Act(noUser))).Message, StringComparison.Ordinal);
        Assert.Contains("doesn't let you start jobs", (await Assert.ThrowsAsync<ForbiddenException>(() => Act(planner))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<ForbiddenException>(() => Act(elsewhere));
        Assert.Contains("switched off", (await Assert.ThrowsAsync<ForbiddenException>(() => Act(off))).Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<UnauthorizedException>(() =>
            StationActing.BeginAsync(http, tickets, security, _clock, "expired or forged", Permissions.JobExecute, UserMessage.Of("start jobs")));
        Assert.False(http.Items.ContainsKey(StationActing.ItemKey));
    }

    private sealed class SequentialIds : IIdGenerator
    {
        public Guid NewId() => Guid.CreateVersion7();
    }
}
