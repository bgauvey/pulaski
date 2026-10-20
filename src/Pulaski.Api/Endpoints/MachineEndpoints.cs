using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Application.Security;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Machines (D-041): their live status, their stops, coding stops, and utilization and OEE reports (D-043).</summary>
internal static class MachineEndpoints
{
    public static IEndpointRouteBuilder MapMachineEndpoints(this IEndpointRouteBuilder routes)
    {
        var machines = routes.MapGroup("/machines").WithTags("Machines");

        machines.MapGet("/status", async Task<Ok<IReadOnlyList<MachineStatusDto>>> (
            Guid siteId, IQueryHandler<GetMachineStatusQuery, IReadOnlyList<MachineStatusDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMachineStatusQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        // Stops that started in [from, to); for equipment, its stops and those of everything below it.
        machines.MapGet("/stops", async Task<Ok<IReadOnlyList<DowntimeDto>>> (
            Guid siteId, DateTimeOffset from, DateTimeOffset to, Guid? equipmentId, bool? uncodedOnly, bool? includeMicroStops,
            IQueryHandler<ListStopsQuery, IReadOnlyList<DowntimeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListStopsQuery(new StopFilter(siteId, equipmentId, from, to, uncodedOnly ?? false, includeMicroStops ?? false)), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        // Utilization and OEE between two production days (D-043), for equipment and everything below it, or the whole site.
        machines.MapGet("/oee", async Task<Ok<OeeReportDto>> (
            Guid siteId, DateOnly from, DateOnly to, Guid? equipmentId, IQueryHandler<GetOeeReportQuery, OeeReportDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetOeeReportQuery(siteId, from, to, equipmentId), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        machines.MapPost("/stops/{id:guid}/code", async Task<Ok<DowntimeCodedResponse>> (
            Guid id, CodeDowntimeRequest request, ICommandHandler<CodeDowntimeCommand, DowntimeCodedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CodeDowntimeCommand(id, request.StartedAt, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeCode, Site.Of(SiteOf.Downtime));

        routes.MapGet("/equipment/{id:guid}/coding-reasons", async Task<Ok<IReadOnlyList<ReasonCodeDto>>> (
            Guid id, IQueryHandler<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListCodingReasonsQuery(id), ct)))
        .WithTags("Machines")
        .RequireAny(Site.Of(SiteOf.Equipment), Permissions.MachinesView);

        return routes;
    }
}
