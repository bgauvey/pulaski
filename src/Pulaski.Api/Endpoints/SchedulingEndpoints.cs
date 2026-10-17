using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

internal static class SchedulingEndpoints
{
    public static IEndpointRouteBuilder MapSchedulingEndpoints(this IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/scheduling").WithTags("Scheduling");

        group.MapGet("/lines/{lineId:guid}/timeline", async Task<Ok<LineTimelineDto>> (
            Guid lineId,
            int? horizonDays,
            IQueryHandler<GetLineTimelineQuery, LineTimelineDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLineTimelineQuery(lineId, horizonDays), ct)));

        // A read: where a new job for the order would run, without creating it.
        group.MapGet("/lines/{lineId:guid}/placement-preview", async Task<Ok<JobPlacementPreviewDto>> (
            Guid lineId,
            Guid workOrderId,
            decimal plannedQty,
            Guid? beforeJobId,
            string? jobNumber,
            IQueryHandler<PreviewJobPlacementQuery, JobPlacementPreviewDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new PreviewJobPlacementQuery(lineId, workOrderId, plannedQty, beforeJobId, jobNumber), ct)));

        group.MapGet("/lines/{lineId:guid}/run-standards", async Task<Ok<IReadOnlyList<RunStandardDto>>> (
            Guid lineId,
            IQueryHandler<ListRunStandardsQuery, IReadOnlyList<RunStandardDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListRunStandardsQuery(lineId), ct)));

        group.MapPost("/jobs", async Task<Created<CreatedResponse>> (
            CreateJobRequest request,
            ICommandHandler<CreateJobCommand, CreatedResponse> handler,
            CancellationToken ct) =>
        {
            var command = new CreateJobCommand(
                request.LineId,
                request.JobNumber,
                request.JobType,
                request.WorkOrderId,
                request.PlannedQty,
                request.PlannedDuration,
                request.CountsAsPlannedDowntime,
                request.EarliestStart,
                request.BeforeJobId,
                request.LatestEnd);
            var created = await handler.HandleAsync(command, ct);
            return TypedResults.Created($"/api/scheduling/jobs/{created.Id}", created);
        })
        .RequirePermission(Permissions.SchedulePlan, Site.FromBody<CreateJobRequest>(SiteOf.Equipment, r => r.LineId));

        group.MapPost("/jobs/{id:guid}/sequence", async Task<Ok<VersionResponse>> (
            Guid id,
            SequenceJobRequest request,
            ICommandHandler<SequenceJobCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SequenceJobCommand(id, request.Version, request.LineId, request.BeforeJobId), ct)))
        .RequirePermission(Permissions.SchedulePlan, Site.Of(SiteOf.Job));

        group.MapPost("/jobs/{id:guid}/pin", async Task<Ok<VersionResponse>> (
            Guid id,
            PinJobRequest request,
            ICommandHandler<PinJobCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new PinJobCommand(id, request.Version, request.IsLocked, request.EarliestStart, request.LatestEnd), ct)))
        .RequirePermission(Permissions.SchedulePlan, Site.Of(SiteOf.Job));

        group.MapPost("/jobs/{id:guid}/assign", async Task<Ok<VersionResponse>> (
            Guid id,
            AssignJobRequest request,
            ICommandHandler<AssignJobCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new AssignJobCommand(id, request.Version, request.TargetId, request.BeforeJobId), ct)))
        .RequirePermission(Permissions.SchedulePlan, Site.Of(SiteOf.Job));

        group.MapPost("/jobs/{id:guid}/cancel", async Task<Ok<CancelJobResponse>> (
            Guid id,
            CancelJobRequest request,
            ICommandHandler<CancelJobCommand, CancelJobResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new CancelJobCommand(id, request.Version, request.Reason, request.Replace), ct)))
        .RequirePermission(Permissions.SchedulePlan, Site.Of(SiteOf.Job));

        group.MapPost("/run-standards/{id:guid}/modify", async Task<Ok<VersionResponse>> (
            Guid id,
            ModifyRunStandardRequest request,
            ICommandHandler<ModifyRunStandardCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ModifyRunStandardCommand(id, request.Version, request.RatePerHour, request.Note), ct)))
        .RequirePermission(Permissions.RateModify, Site.Of(SiteOf.RunStandard));

        group.MapPost("/run-standards/{id:guid}/clear", async Task<Ok<VersionResponse>> (
            Guid id,
            ClearRunStandardRequest request,
            ICommandHandler<ClearRunStandardCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ClearRunStandardCommand(id, request.Version, request.Reason), ct)))
        .RequirePermission(Permissions.RateModify, Site.Of(SiteOf.RunStandard));

        group.MapGet("/sites/{siteId:guid}/policy", async Task<Ok<SchedulingPolicyDto>> (
            Guid siteId,
            IQueryHandler<GetSchedulingPolicyQuery, SchedulingPolicyDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetSchedulingPolicyQuery(siteId), ct)));

        group.MapPut("/sites/{siteId:guid}/policy", async Task<Ok<VersionResponse>> (
            Guid siteId,
            SetSchedulingPolicyRequest request,
            ICommandHandler<SetSchedulingPolicyCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetSchedulingPolicyCommand(siteId, request.Version, request.ChangeoverMode), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        return routes;
    }
}
