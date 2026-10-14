using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Scheduling;

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

        group.MapPost("/lines/{lineId:guid}/publish", async Task<Ok<PublishScheduleResponse>> (
            Guid lineId,
            PublishScheduleRequest request,
            ICommandHandler<PublishScheduleCommand, PublishScheduleResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new PublishScheduleCommand(lineId, request.HorizonDays), ct)));

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
                request.BeforeJobId);
            var created = await handler.HandleAsync(command, ct);
            return TypedResults.Created($"/api/scheduling/jobs/{created.Id}", created);
        });

        group.MapPost("/jobs/{id:guid}/sequence", async Task<Ok<VersionResponse>> (
            Guid id,
            SequenceJobRequest request,
            ICommandHandler<SequenceJobCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SequenceJobCommand(id, request.Version, request.LineId, request.BeforeJobId), ct)));

        group.MapPost("/jobs/{id:guid}/pin", async Task<Ok<VersionResponse>> (
            Guid id,
            PinJobRequest request,
            ICommandHandler<PinJobCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new PinJobCommand(id, request.Version, request.IsLocked, request.EarliestStart), ct)));

        group.MapPost("/jobs/{id:guid}/dispatch", async Task<Ok<DispatchJobResponse>> (
            Guid id,
            DispatchJobRequest request,
            ICommandHandler<DispatchJobCommand, DispatchJobResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new DispatchJobCommand(id, request.Version), ct)));

        group.MapPost("/run-standards/{id:guid}/modify", async Task<Ok<VersionResponse>> (
            Guid id,
            ModifyRunStandardRequest request,
            ICommandHandler<ModifyRunStandardCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ModifyRunStandardCommand(id, request.Version, request.RatePerHour, request.Note), ct)));

        group.MapPost("/run-standards/{id:guid}/clear", async Task<Ok<VersionResponse>> (
            Guid id,
            ClearRunStandardRequest request,
            ICommandHandler<ClearRunStandardCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ClearRunStandardCommand(id, request.Version, request.Reason), ct)));

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
                new SetSchedulingPolicyCommand(siteId, request.Version, request.ChangeoverMode), ct)));

        return routes;
    }
}
