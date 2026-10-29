using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.MachineData;
using Pulaski.Application.Security;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.MachineData;
using Pulaski.Domain.MachineData;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Machines (D-050): their live status, their stops, coding stops, and utilization and OEE reports (D-052).</summary>
internal static class MachineEndpoints
{
    public static IEndpointRouteBuilder MapMachineEndpoints(this IEndpointRouteBuilder routes)
    {
        var machines = routes.MapGroup("/machines").WithTags("Machines");

        machines.MapGet("/status", async Task<Ok<IReadOnlyList<MachineStatusDto>>> (
            Guid siteId, IQueryHandler<GetMachineStatusQuery, IReadOnlyList<MachineStatusDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetMachineStatusQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        // Stops that started in [from, to), newest first, a page at a time (#10): pass the page's next as after for the rest. For equipment,
        // its stops and those of everything below it. Replaced stops only when asked for.
        machines.MapGet("/stops", async Task<Ok<StopPageDto>> (
            Guid siteId, DateTimeOffset from, DateTimeOffset to, Guid? equipmentId, bool? uncodedOnly, bool? includeMicroStops, bool? includeSuperseded,
            string? after, int? limit, IQueryHandler<ListStopsQuery, StopPageDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListStopsQuery(new StopFilter(
                siteId, equipmentId, from, to, uncodedOnly ?? false, includeMicroStops ?? false, includeSuperseded ?? false), after, limit), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        // Utilization and OEE between two production days (D-052), for equipment and everything below it, or the whole site; one shift with shiftCode (#21).
        machines.MapGet("/oee", async Task<Ok<OeeReportDto>> (
            Guid siteId, DateOnly from, DateOnly to, Guid? equipmentId, string? shiftCode, IQueryHandler<GetOeeReportQuery, OeeReportDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetOeeReportQuery(siteId, from, to, equipmentId, string.IsNullOrWhiteSpace(shiftCode) ? null : shiftCode), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView, Permissions.OverviewView);

        // OEE targets (D-073): the site's default and each piece of equipment's, by period; seen with the site settings or the machines.
        var targets = routes.MapGroup("/oee-targets/sites").WithTags("Machines");
        targets.MapGet("/{siteId:guid}", async Task<Ok<IReadOnlyList<OeeTargetDto>>> (
            Guid siteId, IQueryHandler<ListOeeTargetsQuery, IReadOnlyList<OeeTargetDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListOeeTargetsQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.SiteSettingsView, Permissions.MachinesView);

        targets.MapPut("/{siteId:guid}", async Task<Ok<IReadOnlyList<OeeTargetDto>>> (
            Guid siteId, SetOeeTargetRequest request, ICommandHandler<SetOeeTargetCommand, IReadOnlyList<OeeTargetDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetOeeTargetCommand(siteId, request.EquipmentId, request.EffectiveFrom,
                new OeeTargetValues(request.AvailabilityPct, request.PerformancePct, request.QualityPct, request.OeePct), request.Version), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        targets.MapPost("/{siteId:guid}/end", async Task<Ok<IReadOnlyList<OeeTargetDto>>> (
            Guid siteId, EndOeeTargetRequest request, ICommandHandler<EndOeeTargetCommand, IReadOnlyList<OeeTargetDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndOeeTargetCommand(siteId, request.EquipmentId, request.EndOn, request.Version), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        // Alarms and warnings active in [from, to) (#26), active first; for equipment, its own and those below it.
        machines.MapGet("/alarms", async Task<Ok<AlarmListDto>> (
            Guid siteId, DateTimeOffset from, DateTimeOffset to, Guid? equipmentId, bool? activeOnly,
            IQueryHandler<ListAlarmsQuery, AlarmListDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListAlarmsQuery(new AlarmFilter(siteId, equipmentId, from, to, activeOnly ?? false)), ct)))
        .RequireAny(Site.Query(), Permissions.MachinesView);

        // A piece of equipment's process values and levels in [from, to) (#26), by tag, oldest first.
        machines.MapGet("/process-values", async Task<Ok<IReadOnlyList<ProcessValueSeriesDto>>> (
            Guid equipmentId, DateTimeOffset from, DateTimeOffset to, Guid? tagId,
            IQueryHandler<GetProcessValuesQuery, IReadOnlyList<ProcessValueSeriesDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetProcessValuesQuery(equipmentId, tagId, from, to), ct)))
        .RequireAny(Site.QueryOf(SiteOf.Equipment, "equipmentId"), Permissions.MachinesView);

        machines.MapPost("/stops/{id:guid}/code", async Task<Ok<DowntimeCodedResponse>> (
            Guid id, CodeDowntimeRequest request, ICommandHandler<CodeDowntimeCommand, DowntimeCodedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CodeDowntimeCommand(id, request.StartedAt, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeCode, Site.Of(SiteOf.Downtime));

        // Adjusting stops (D-069): the stops replaced are kept, superseded; the response names the stops that replace them.
        machines.MapPost("/stops", async Task<Ok<StopsAdjustedResponse>> (
            EnterStopRequest request, ICommandHandler<EnterStopCommand, StopsAdjustedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new EnterStopCommand(request.EquipmentId, request.StartedAt, request.EndedAt, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeAdjust, Site.FromBody<EnterStopRequest>(SiteOf.Equipment, r => r.EquipmentId));

        machines.MapPost("/stops/{id:guid}/split", async Task<Ok<StopsAdjustedResponse>> (
            Guid id, SplitStopRequest request, ICommandHandler<SplitStopCommand, StopsAdjustedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SplitStopCommand(id, request.StartedAt, request.At, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeAdjust, Site.Of(SiteOf.Downtime));

        // Other times for a stop, removing one entered by hand, undoing an adjustment (#68): each replaces, never edits.
        machines.MapPost("/stops/{id:guid}/retime", async Task<Ok<StopsAdjustedResponse>> (
            Guid id, RetimeStopRequest request, ICommandHandler<RetimeStopCommand, StopsAdjustedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RetimeStopCommand(id, request.StartedAt, request.NewStartedAt, request.NewEndedAt, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeAdjust, Site.Of(SiteOf.Downtime));

        machines.MapPost("/stops/{id:guid}/void", async Task<Ok<StopsAdjustedResponse>> (
            Guid id, StopReasonedRequest request, ICommandHandler<VoidStopCommand, StopsAdjustedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new VoidStopCommand(id, request.StartedAt, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeAdjust, Site.Of(SiteOf.Downtime));

        machines.MapPost("/stops/{id:guid}/undo", async Task<Ok<StopsAdjustedResponse>> (
            Guid id, StopReasonedRequest request, ICommandHandler<UndoStopAdjustmentCommand, StopsAdjustedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UndoStopAdjustmentCommand(id, request.StartedAt, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeAdjust, Site.Of(SiteOf.Downtime));

        // The stops are all of one machine (the handler checks), so the first one's site is theirs.
        machines.MapPost("/stops/merge", async Task<Ok<StopsAdjustedResponse>> (
            MergeStopsRequest request, ICommandHandler<MergeStopsCommand, StopsAdjustedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new MergeStopsCommand(request.Stops, request.Comment), ct)))
        .RequirePermission(Permissions.DowntimeAdjust, Site.FromBody<MergeStopsRequest>(SiteOf.Downtime, r => r.Stops.Count > 0 ? r.Stops[0].Id : null));

        routes.MapGet("/equipment/{id:guid}/coding-reasons", async Task<Ok<IReadOnlyList<ReasonCodeDto>>> (
            Guid id, IQueryHandler<ListCodingReasonsQuery, IReadOnlyList<ReasonCodeDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListCodingReasonsQuery(id), ct)))
        .WithTags("Machines")
        .RequireAny(Site.Of(SiteOf.Equipment), Permissions.MachinesView);

        return routes;
    }
}
