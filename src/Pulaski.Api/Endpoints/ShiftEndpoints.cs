using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Shifts;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Shifts;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Shift schedules, and which working time each piece of equipment follows (D-038).</summary>
internal static class ShiftEndpoints
{
    public static IEndpointRouteBuilder MapShiftEndpoints(this IEndpointRouteBuilder routes)
    {
        var schedules = routes.MapGroup("/shift-schedules").WithTags("Shifts");

        schedules.MapGet("/", async Task<Ok<IReadOnlyList<ShiftScheduleDto>>> (
            Guid? siteId, HttpContext http, IQueryHandler<ListShiftSchedulesQuery, IReadOnlyList<ShiftScheduleDto>> handler, CancellationToken ct) =>
            TypedResults.Ok((await handler.HandleAsync(new ListShiftSchedulesQuery(siteId), ct))
                .At(await Visible.SitesAsync(http, Permissions.ShiftsView, Permissions.EquipmentView), s => s.SiteId)))
        .RequireAny(Site.Query(), Permissions.ShiftsView, Permissions.EquipmentView);

        schedules.MapGet("/{id:guid}", async Task<Ok<ShiftScheduleDto>> (Guid id, IQueryHandler<GetShiftScheduleQuery, ShiftScheduleDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetShiftScheduleQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.ShiftSchedule), Permissions.ShiftsView, Permissions.EquipmentView);

        // Works out a schedule's shifts, saved or not; changes nothing, so seeing shift schedules is enough.
        schedules.MapPost("/preview", async Task<Ok<ShiftPreviewDto>> (
            PreviewShiftScheduleRequest request, IQueryHandler<PreviewShiftScheduleQuery, ShiftPreviewDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new PreviewShiftScheduleQuery(request), ct)))
        .RequireAny(Site.FromBody<PreviewShiftScheduleRequest>(SiteOf.Equipment, r => r.SiteId), Permissions.ShiftsView);

        schedules.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateShiftScheduleRequest request, ICommandHandler<CreateShiftScheduleCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateShiftScheduleCommand(
                request.SiteId, request.Code, request.Name, request.Description, request.CycleDays, request.CycleStart, request.Shifts), ct);
            return TypedResults.Created($"/api/shift-schedules/{created.Id}", created);
        })
        .RequirePermission(Permissions.Shifts, Site.FromBody<CreateShiftScheduleRequest>(SiteOf.Equipment, r => r.SiteId));

        schedules.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateShiftScheduleRequest request, ICommandHandler<UpdateShiftScheduleCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateShiftScheduleCommand(
                id, request.Version, request.Name, request.Description, request.CycleDays, request.CycleStart, request.Shifts, request.IsActive), ct)))
        .RequirePermission(Permissions.Shifts, Site.Of(SiteOf.ShiftSchedule));

        // One date's change to a schedule (D-042): set it, or take it off so the cycle applies again.
        schedules.MapPut("/{id:guid}/overrides", async Task<Ok<VersionResponse>> (
            Guid id, SetShiftOverrideRequest request, ICommandHandler<SetShiftOverrideCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetShiftOverrideCommand(
                id, request.Version, request.Date, request.Code, request.IsWorking, request.Name, request.Start, request.Minutes, request.Crew, request.Note), ct)))
        .RequirePermission(Permissions.Shifts, Site.Of(SiteOf.ShiftSchedule));

        schedules.MapDelete("/{id:guid}/overrides/{date}/{code}", async Task<Ok<VersionResponse>> (
            Guid id, DateOnly date, string code, long version, ICommandHandler<RemoveShiftOverrideCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RemoveShiftOverrideCommand(id, version, date, code), ct)))
        .RequirePermission(Permissions.Shifts, Site.Of(SiteOf.ShiftSchedule));

        // Time taken out of the calendar of a site, area or line and everything below it (D-043).
        var exceptions = routes.MapGroup("/calendar-exceptions").WithTags("Site settings");

        exceptions.MapGet("/", async Task<Ok<IReadOnlyList<CalendarExceptionDto>>> (
            Guid siteId, DateTimeOffset? from, bool? includeRemoved, IQueryHandler<ListCalendarExceptionsQuery, IReadOnlyList<CalendarExceptionDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListCalendarExceptionsQuery(siteId, from, includeRemoved ?? false), ct)))
        .RequireAny(Site.Query(), Permissions.SiteSettingsView, Permissions.ScheduleView, Permissions.ShiftsView);

        exceptions.MapPost("/", async Task<Created<CreatedResponse>> (
            CreateCalendarExceptionRequest request, ICommandHandler<CreateCalendarExceptionCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateCalendarExceptionCommand(
                request.ScopeEquipmentId, request.Kind, request.StartsAt, request.EndsAt, request.Note), ct);
            return TypedResults.Created($"/api/calendar-exceptions/{created.Id}", created);
        })
        .RequirePermission(Permissions.SiteSettings, Site.FromBody<CreateCalendarExceptionRequest>(SiteOf.Equipment, r => r.ScopeEquipmentId));

        exceptions.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateCalendarExceptionRequest request, ICommandHandler<UpdateCalendarExceptionCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateCalendarExceptionCommand(
                id, request.Version, request.Kind, request.StartsAt, request.EndsAt, request.Note, request.IsActive), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Of(SiteOf.CalendarException));

        routes.MapPut("/equipment/{id:guid}/calendar", async Task<Ok<VersionResponse>> (
            Guid id, SetEquipmentCalendarRequest request, ICommandHandler<SetEquipmentCalendarCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetEquipmentCalendarCommand(id, request.Version, request.Mode, request.ShiftScheduleId), ct)))
        .WithTags("Shifts")
        .RequirePermission(Permissions.Shifts, Site.Of(SiteOf.Equipment));

        return routes;
    }
}
