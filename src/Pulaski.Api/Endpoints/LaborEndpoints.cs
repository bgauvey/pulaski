using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Labor;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Labor;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Crews, people and labor (D-040).</summary>
internal static class LaborEndpoints
{
    public static IEndpointRouteBuilder MapLaborEndpoints(this IEndpointRouteBuilder routes)
    {
        // ---- Crews and people ----
        var crews = routes.MapGroup("/crews").WithTags("Labor");

        // Crews are named in shift schedules too, so whoever sees shifts or labor may list them.
        crews.MapGet("/", async Task<Ok<IReadOnlyList<CrewDto>>> (Guid siteId, IQueryHandler<ListCrewsQuery, IReadOnlyList<CrewDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListCrewsQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.LaborView, Permissions.ShiftsView);

        crews.MapPost("/", async Task<Created<CreatedResponse>> (CreateCrewRequest request, ICommandHandler<CreateCrewCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateCrewCommand(request.SiteId, request.Code, request.Name), ct);
            return TypedResults.Created($"/api/crews/{created.Id}", created);
        })
        .RequirePermission(Permissions.People, Site.FromBody<CreateCrewRequest>(SiteOf.Equipment, r => r.SiteId));

        crews.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateCrewRequest request, ICommandHandler<UpdateCrewCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateCrewCommand(id, request.Version, request.Name, request.IsActive), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.Crew));

        var people = routes.MapGroup("/people").WithTags("Labor");

        people.MapGet("/", async Task<Ok<IReadOnlyList<PersonDto>>> (Guid siteId, IQueryHandler<ListPeopleQuery, IReadOnlyList<PersonDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListPeopleQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.LaborView);

        people.MapPost("/", async Task<Created<CreatedResponse>> (CreatePersonRequest request, ICommandHandler<CreatePersonCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreatePersonCommand(request.SiteId, request.EmployeeNumber, request.DisplayName, request.CrewId, request.Username), ct);
            return TypedResults.Created($"/api/people/{created.Id}", created);
        })
        .RequirePermission(Permissions.People, Site.FromBody<CreatePersonRequest>(SiteOf.Equipment, r => r.SiteId));

        people.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdatePersonRequest request, ICommandHandler<UpdatePersonCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdatePersonCommand(id, request.Version, request.DisplayName, request.CrewId, request.Username, request.IsActive), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.Person));

        // ---- Labor ----
        var labor = routes.MapGroup("/labor").WithTags("Labor");

        // The signed-in user's own time: who they are as a person and what they are clocked on to.
        labor.MapGet("/me", async Task<Ok<MyLaborDto>> (IRequestContext request, IQueryHandler<GetMyLaborQuery, MyLaborDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMyLaborQuery(request.UserId), ct)))
        .RequireAnyView();

        labor.MapPost("/me/sessions", async Task<Ok<LaborResponse>> (ClockOnRequest request, ICommandHandler<ClockOnCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ClockOnCommand(null, Input(request)), ct)))
        .RequirePermission(Permissions.LaborRecordOwn, Site.FromBody<ClockOnRequest>(SiteOf.Equipment, r => r.EquipmentId));

        labor.MapPost("/me/sessions/{id:guid}/end", async Task<Ok<LaborResponse>> (Guid id, ClockOffRequest request, ICommandHandler<ClockOffCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ClockOffCommand(id, request.Version, request.EndedAt, OwnOnly: true), ct)))
        .RequirePermission(Permissions.LaborRecordOwn, Site.Of(SiteOf.LaborSession));

        labor.MapPost("/sessions", async Task<Ok<LaborResponse>> (ClockOnRequest request, ICommandHandler<ClockOnCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ClockOnCommand(request.PersonId ?? Guid.Empty, Input(request)), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.FromBody<ClockOnRequest>(SiteOf.Equipment, r => r.EquipmentId));

        labor.MapPost("/sessions/{id:guid}/end", async Task<Ok<LaborResponse>> (Guid id, ClockOffRequest request, ICommandHandler<ClockOffCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ClockOffCommand(id, request.Version, request.EndedAt, OwnOnly: false), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.Of(SiteOf.LaborSession));

        labor.MapPut("/sessions/{id:guid}", async Task<Ok<LaborResponse>> (Guid id, UpdateLaborSessionRequest request, ICommandHandler<UpdateLaborSessionCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateLaborSessionCommand(id, request.Version, request.EquipmentId, request.JobId, request.LaborType, request.ReasonCodeId,
                request.StartedAt, request.EndedAt, request.AllocationPct, request.Note), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.Of(SiteOf.LaborSession));

        labor.MapPost("/sessions/{id:guid}/correct", async Task<Ok<LaborResponse>> (Guid id, CorrectLaborSessionRequest request, ICommandHandler<CorrectLaborSessionCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CorrectLaborSessionCommand(id, request.Void, request.EquipmentId, request.JobId, request.LaborType, request.ReasonCodeId,
                request.StartedAt, request.EndedAt, request.AllocationPct, request.Note), ct)))
        .RequirePermission(Permissions.LaborApprove, Site.Of(SiteOf.LaborSession));

        labor.MapPost("/check-in", async Task<Ok<LaborResponse>> (CheckInRequest request, ICommandHandler<CheckInCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CheckInCommand(request.EquipmentId, request.PersonIds, request.StartedAt), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.FromBody<CheckInRequest>(SiteOf.Equipment, r => r.EquipmentId));

        labor.MapPost("/check-out", async Task<Ok<LaborResponse>> (CheckOutRequest request, ICommandHandler<CheckOutCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CheckOutCommand(request.EquipmentId, request.EndedAt), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.FromBody<CheckOutRequest>(SiteOf.Equipment, r => r.EquipmentId));

        var sites = labor.MapGroup("/sites/{siteId:guid}");

        // Who is on each line: the Labor screen, and the floor for whoever clocks on there.
        sites.MapGet("/lines", async Task<Ok<IReadOnlyList<LineLaborDto>>> (Guid siteId, IQueryHandler<ListLineLaborQuery, IReadOnlyList<LineLaborDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListLineLaborQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.LaborView, Permissions.FloorView);

        sites.MapGet("/shifts", async Task<Ok<IReadOnlyList<LaborShiftDto>>> (Guid siteId, DateOnly date, IQueryHandler<ListLaborShiftsQuery, IReadOnlyList<LaborShiftDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListLaborShiftsQuery(siteId, date), ct)))
        .RequireAny(Site.Route(), Permissions.LaborView);

        sites.MapGet("/shift", async Task<Ok<ShiftLaborDto>> (
            Guid siteId, DateOnly date, Guid? scheduleId, string? shiftCode, IQueryHandler<GetShiftLaborQuery, ShiftLaborDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetShiftLaborQuery(new ShiftKey(siteId, date, scheduleId, shiftCode)), ct)))
        .RequireAny(Site.Route(), Permissions.LaborView);

        sites.MapPost("/approvals", async Task<Ok<LaborResponse>> (Guid siteId, ApproveShiftRequest request, ICommandHandler<ApproveShiftCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ApproveShiftCommand(new ShiftKey(siteId, request.ProductionDate, request.ShiftScheduleId, request.ShiftCode), request.Note), ct)))
        .RequirePermission(Permissions.LaborApprove, Site.Route());

        sites.MapGet("/report", async Task<Ok<LaborReportDto>> (Guid siteId, DateOnly from, DateOnly to, IQueryHandler<GetLaborReportQuery, LaborReportDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLaborReportQuery(siteId, from, to), ct)))
        .RequireAny(Site.Route(), Permissions.LaborView);

        return routes;
    }

    private static LaborInput Input(ClockOnRequest r) => new(r.EquipmentId, r.JobId, r.LaborType, r.ReasonCodeId, r.AllocationPct, r.StartedAt, r.Note);
}
