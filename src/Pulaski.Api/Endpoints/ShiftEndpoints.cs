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
            Guid? siteId, IQueryHandler<ListShiftSchedulesQuery, IReadOnlyList<ShiftScheduleDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListShiftSchedulesQuery(siteId), ct)));

        schedules.MapGet("/{id:guid}", async Task<Ok<ShiftScheduleDto>> (Guid id, IQueryHandler<GetShiftScheduleQuery, ShiftScheduleDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetShiftScheduleQuery(id), ct)));

        // Works out a schedule's shifts, saved or not; changes nothing, so it needs no permission.
        schedules.MapPost("/preview", async Task<Ok<ShiftPreviewDto>> (
            PreviewShiftScheduleRequest request, IQueryHandler<PreviewShiftScheduleQuery, ShiftPreviewDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new PreviewShiftScheduleQuery(request), ct)));

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

        routes.MapPut("/equipment/{id:guid}/calendar", async Task<Ok<VersionResponse>> (
            Guid id, SetEquipmentCalendarRequest request, ICommandHandler<SetEquipmentCalendarCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetEquipmentCalendarCommand(id, request.Version, request.Mode, request.ShiftScheduleId), ct)))
        .WithTags("Shifts")
        .RequirePermission(Permissions.Shifts, Site.Of(SiteOf.Equipment));

        return routes;
    }
}
