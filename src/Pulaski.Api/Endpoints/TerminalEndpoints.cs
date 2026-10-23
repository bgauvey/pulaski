using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Labor;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Shop-floor terminals and badges (D-049): managing them, and what a terminal does.</summary>
internal static class TerminalEndpoints
{
    /// <summary>Registering terminals and issuing badges: signed-in users with the permission.</summary>
    public static IEndpointRouteBuilder MapTerminalAdminEndpoints(this IEndpointRouteBuilder routes)
    {
        var terminals = routes.MapGroup("/terminals").WithTags("Terminals");

        terminals.MapGet("/", async Task<Ok<IReadOnlyList<TerminalDto>>> (Guid siteId, IQueryHandler<ListTerminalsQuery, IReadOnlyList<TerminalDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListTerminalsQuery(siteId), ct)))
        .RequirePermission(Permissions.Terminals, Site.Query());

        terminals.MapPost("/", async Task<Created<CreatedResponse>> (CreateTerminalRequest request, ICommandHandler<CreateTerminalCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateTerminalCommand(request.Code, request.Name, request.ScopeEquipmentId, request.DefaultEquipmentId, request.AuthMode), ct);
            return TypedResults.Created($"/api/terminals/{created.Id}", created);
        })
        .RequirePermission(Permissions.Terminals, Site.FromBody<CreateTerminalRequest>(SiteOf.Equipment, r => r.ScopeEquipmentId));

        terminals.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateTerminalRequest request, ICommandHandler<UpdateTerminalCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateTerminalCommand(id, request.Version, request.Name, request.ScopeEquipmentId, request.DefaultEquipmentId,
                request.AuthMode, request.IsActive), ct)))
        .RequirePermission(Permissions.Terminals, Site.Of(SiteOf.Terminal));

        terminals.MapPost("/{id:guid}/enrolment-code", async Task<Ok<EnrolmentCodeResponse>> (Guid id, ICommandHandler<IssueEnrolmentCodeCommand, EnrolmentCodeResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new IssueEnrolmentCodeCommand(id), ct)))
        .RequirePermission(Permissions.Terminals, Site.Of(SiteOf.Terminal));

        var badges = routes.MapGroup("/badges").WithTags("Terminals");

        badges.MapGet("/", async Task<Ok<IReadOnlyList<BadgeDto>>> (Guid siteId, IQueryHandler<ListBadgesQuery, IReadOnlyList<BadgeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListBadgesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.People, Permissions.LaborView);

        badges.MapPost("/", async Task<Created<CreatedResponse>> (IssueBadgeRequest request, ICommandHandler<IssueBadgeCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new IssueBadgeCommand(request.PersonId, request.BadgeValue, request.Pin), ct);
            return TypedResults.Created($"/api/badges/{created.Id}", created);
        })
        .RequirePermission(Permissions.People, Site.FromBody<IssueBadgeRequest>(SiteOf.Person, r => r.PersonId));

        badges.MapPost("/{id:guid}/revoke", async Task<Ok<VersionResponse>> (Guid id, RevokeBadgeRequest request, ICommandHandler<RevokeBadgeCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RevokeBadgeCommand(id, request.Reason), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.Badge));

        return routes;
    }

    /// <summary>
    /// What a terminal does (D-049). Not for users: enrolling needs only the one-time code; everything else the terminal's
    /// secret, and acting for a person the ticket their badge gave it.
    /// </summary>
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/enrol", async Task<Ok<TerminalEnrolledResponse>> (EnrolTerminalRequest request, ICommandHandler<EnrolTerminalCommand, TerminalEnrolledResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EnrolTerminalCommand(request.Code), ct)))
        .WithTags("Terminal");

        var terminal = routes.MapGroup("").WithTags("Terminal").AddEndpointFilter<TerminalFilter>();

        terminal.MapGet("/", async Task<Ok<TerminalStatusDto>> (HttpContext http, IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetTerminalStatusQuery(TerminalFilter.Current(http), null), ct)));

        terminal.MapPost("/identify", async Task<Ok<BadgeIdentityResponse>> (
            IdentifyBadgeRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<IdentifyBadgeCommand, BadgeIdentity> identify,
            IQueryHandler<GetTerminalStatusQuery, TerminalStatusDto> status, CancellationToken ct) =>
        {
            var current = TerminalFilter.Current(http);
            var who = await identify.HandleAsync(new IdentifyBadgeCommand(current, request.BadgeValue, request.Pin), ct);
            if (who.PersonId is not { } personId)
            {
                return TypedResults.Ok(new BadgeIdentityResponse { NeedsPin = true });
            }

            var (ticket, expires) = tickets.Issue(current.Id, personId);
            return TypedResults.Ok(new BadgeIdentityResponse
            {
                Ticket = ticket,
                TicketExpiresAt = expires,
                Status = await status.HandleAsync(new GetTerminalStatusQuery(current, personId), ct),
            });
        });

        terminal.MapPost("/clock-on", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalClockOnCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalClockOnCommand(t, person, request.EquipmentId ?? throw Missing("EquipmentId")), ct)));

        terminal.MapPost("/clock-off", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalClockOffCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalClockOffCommand(t, person, request.SessionId ?? throw Missing("SessionId")), ct)));

        terminal.MapPost("/breaks/start", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalStartBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalStartBreakCommand(t, person, request.IsPaid), ct)));

        terminal.MapPost("/breaks/end", (TerminalActionRequest request, HttpContext http, BadgeTickets tickets, ICommandHandler<TerminalEndBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            ActAsync(http, tickets, request, (t, person) => handler.HandleAsync(new TerminalEndBreakCommand(t, person), ct)));

        return routes;
    }

    private static async Task<Ok<LaborResponse>> ActAsync(
        HttpContext http, BadgeTickets tickets, TerminalActionRequest request, Func<TerminalRecord, Guid, Task<LaborResponse>> act)
    {
        var terminal = TerminalFilter.Current(http);
        return TypedResults.Ok(await act(terminal, tickets.PersonOf(request.Ticket, terminal.Id)));
    }

    private static ValidationException Missing(string field) => new(new Dictionary<string, string[]> { [field] = ["Choose one."] });
}
