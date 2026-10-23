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

        // ---- Breaks (D-047) ----
        labor.MapPost("/me/breaks", async Task<Ok<LaborResponse>> (StartBreakRequest request, ICommandHandler<StartBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new StartBreakCommand(null, request.IsPaid, request.StartedAt, request.EndedAt, request.Note), ct)))
        .RequirePermission(Permissions.LaborRecordOwn);

        labor.MapPost("/me/breaks/{id:guid}/end", async Task<Ok<LaborResponse>> (Guid id, EndBreakRequest request, ICommandHandler<EndBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndBreakCommand(id, request.Version, request.EndedAt, OwnOnly: true), ct)))
        .RequirePermission(Permissions.LaborRecordOwn, Site.Of(SiteOf.LaborBreak));

        labor.MapPost("/breaks", async Task<Ok<LaborResponse>> (StartBreakRequest request, ICommandHandler<StartBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new StartBreakCommand(request.PersonId ?? Guid.Empty, request.IsPaid, request.StartedAt, request.EndedAt, request.Note), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.FromBody<StartBreakRequest>(SiteOf.Person, r => r.PersonId));

        labor.MapPost("/breaks/{id:guid}/end", async Task<Ok<LaborResponse>> (Guid id, EndBreakRequest request, ICommandHandler<EndBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndBreakCommand(id, request.Version, request.EndedAt, OwnOnly: false), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.Of(SiteOf.LaborBreak));

        labor.MapPut("/breaks/{id:guid}", async Task<Ok<LaborResponse>> (Guid id, UpdateBreakRequest request, ICommandHandler<UpdateBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateBreakCommand(id, request.Version, request.StartedAt, request.EndedAt, request.IsPaid, request.Note), ct)))
        .RequirePermission(Permissions.LaborRecord, Site.Of(SiteOf.LaborBreak));

        labor.MapPost("/breaks/{id:guid}/correct", async Task<Ok<LaborResponse>> (Guid id, CorrectBreakRequest request, ICommandHandler<CorrectBreakCommand, LaborResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CorrectBreakCommand(id, request.Void, request.StartedAt, request.EndedAt, request.IsPaid, request.Note), ct)))
        .RequirePermission(Permissions.LaborApprove, Site.Of(SiteOf.LaborBreak));

        // ---- Qualifications (D-048) ----
        var qualifications = routes.MapGroup("/qualifications").WithTags("Labor");

        qualifications.MapGet("/", async Task<Ok<IReadOnlyList<QualificationDto>>> (Guid siteId, IQueryHandler<ListQualificationsQuery, IReadOnlyList<QualificationDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListQualificationsQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.LaborView, Permissions.EquipmentView);

        qualifications.MapPost("/", async Task<Created<CreatedResponse>> (CreateQualificationRequest request, ICommandHandler<CreateQualificationCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateQualificationCommand(request.SiteId, request.Code, request.Name, request.Description, request.ValidityMonths), ct);
            return TypedResults.Created($"/api/qualifications/{created.Id}", created);
        })
        .RequirePermission(Permissions.People, Site.FromBody<CreateQualificationRequest>(SiteOf.Equipment, r => r.SiteId));

        qualifications.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (Guid id, UpdateQualificationRequest request, ICommandHandler<UpdateQualificationCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateQualificationCommand(id, request.Version, request.Name, request.Description, request.ValidityMonths, request.IsActive), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.Qualification));

        qualifications.MapGet("/grants", async Task<Ok<IReadOnlyList<QualificationGrantDto>>> (
            Guid siteId, Guid? personId, Guid? qualificationId, int? expiringWithinDays,
            IQueryHandler<ListQualificationGrantsQuery, IReadOnlyList<QualificationGrantDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListQualificationGrantsQuery(siteId, personId, qualificationId, expiringWithinDays), ct)))
        .RequireAny(Site.Query(), Permissions.LaborView);

        qualifications.MapPost("/grants", async Task<Created<CreatedResponse>> (GrantQualificationRequest request, ICommandHandler<GrantQualificationCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new GrantQualificationCommand(request.PersonId, request.QualificationId, request.ValidFrom, request.ExpiresOn, request.Note), ct);
            return TypedResults.Created($"/api/qualifications/grants/{created.Id}", created);
        })
        .RequirePermission(Permissions.People, Site.FromBody<GrantQualificationRequest>(SiteOf.Person, r => r.PersonId));

        qualifications.MapPut("/grants/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateQualificationGrantRequest request, ICommandHandler<UpdateQualificationGrantCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateQualificationGrantCommand(id, request.Version, request.ValidFrom, request.ExpiresOn, request.Note), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.QualificationGrant));

        qualifications.MapPost("/grants/{id:guid}/revoke", async Task<Ok<VersionResponse>> (
            Guid id, RevokeQualificationGrantRequest request, ICommandHandler<RevokeQualificationGrantCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RevokeQualificationGrantCommand(id, request.Version, request.Reason), ct)))
        .RequirePermission(Permissions.People, Site.Of(SiteOf.QualificationGrant));

        routes.MapGet("/equipment/{id:guid}/qualifications", async Task<Ok<EquipmentQualificationsDto>> (
            Guid id, IQueryHandler<GetEquipmentQualificationsQuery, EquipmentQualificationsDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetEquipmentQualificationsQuery(id), ct)))
        .WithTags("Labor")
        .RequireAny(Site.Of(SiteOf.Equipment), Permissions.EquipmentView, Permissions.LaborView);

        routes.MapPut("/equipment/{id:guid}/qualifications", async Task<Ok<VersionResponse>> (
            Guid id, SetEquipmentQualificationRequest request, ICommandHandler<SetEquipmentQualificationCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetEquipmentQualificationCommand(id, request.QualificationId, request.IsRequired), ct)))
        .WithTags("Labor")
        .RequirePermission(Permissions.Equipment, Site.Of(SiteOf.Equipment));

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
