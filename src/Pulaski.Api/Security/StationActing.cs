using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Application.Security;
using Pulaski.Domain.Labor;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Security;

/// <summary>
/// An action at an operator station (#150, D-107): the badged person's user acts, with that user's permissions at the terminal's site, as if
/// signed in; the terminal is the events' source. A person without a user may only record labor, through the kiosk's own endpoints.
/// </summary>
internal static class StationActing
{
    /// <summary>The acting user's <see cref="UserAccess"/> in <c>HttpContext.Items</c>: the request context reads it for the actor.</summary>
    public const string ItemKey = "pulaski.station.user";

    /// <summary>
    /// Checks the station's ticket and that its person's user may <paramref name="permission"/> here, then acts as them for the rest of the
    /// request. Returns the ticket renewed for another <see cref="TerminalRules.StationIdle"/>: acting keeps someone signed on.
    /// </summary>
    /// <exception cref="UnauthorizedException">The ticket ran out: badge again.</exception>
    /// <exception cref="ForbiddenException">The person has no user, or their user may not do this at the terminal's site.</exception>
    public static async Task<(TerminalRecord Terminal, UserAccess Access, string Ticket, DateTimeOffset ExpiresAt)> BeginAsync(
        HttpContext http, BadgeTickets tickets, ISecurityReadStore security, TimeProvider time, string? ticket, string permission, string what)
    {
        var terminal = TerminalFilter.Current(http);
        var personId = tickets.PersonOf(ticket ?? "", terminal.Id);
        var access = await security.GetPersonAccessAsync(personId, time.GetUtcNow(), http.RequestAborted)
            ?? throw new ForbiddenException("You have no Pulaski user, so only your time can be recorded here. Ask a supervisor to link your badge to a user.");
        if (!access.Can(permission, terminal.SiteId))
        {
            throw new ForbiddenException(access.IsActive
                ? $"Your role doesn't let you {what} at this site."
                : "Your Pulaski user is switched off; ask a supervisor.");
        }

        http.Items[ItemKey] = access;
        http.Items["pulaski.access"] = access;
        var (renewed, expires) = tickets.Issue(terminal.Id, personId, TerminalRules.StationIdle);
        return (terminal, access, renewed, expires);
    }
}
