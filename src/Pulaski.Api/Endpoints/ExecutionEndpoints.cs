using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Execution;
using Pulaski.Contracts.Execution;

namespace Pulaski.Api.Endpoints;

/// <summary>Job execution and production reporting (D-035): start, pause, complete, report, and the floor board.</summary>
internal static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder routes)
    {
        var jobs = routes.MapGroup("/jobs").WithTags("Execution");

        jobs.MapGet("/{id:guid}/execution", async Task<Ok<JobExecutionDto>> (
            Guid id, IQueryHandler<GetJobExecutionQuery, JobExecutionDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetJobExecutionQuery(id), ct)));

        jobs.MapPost("/{id:guid}/start", async Task<Ok<ExecutionResponse>> (
            Guid id, StartJobRequest request, ICommandHandler<StartJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new StartJobCommand(id, request.Version, request.LineId, request.SkipChangeover), ct)));

        jobs.MapPost("/{id:guid}/pause", async Task<Ok<ExecutionResponse>> (
            Guid id, PauseJobRequest request, ICommandHandler<PauseJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new PauseJobCommand(id, request.Version, request.ReasonCodeId, request.Comment), ct)));

        jobs.MapPost("/{id:guid}/complete", async Task<Ok<ExecutionResponse>> (
            Guid id, JobVersionRequest request, ICommandHandler<CompleteJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CompleteJobCommand(id, request.Version), ct)));

        jobs.MapPost("/{id:guid}/reopen", async Task<Ok<ExecutionResponse>> (
            Guid id, JobVersionRequest request, ICommandHandler<ReopenJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReopenJobCommand(id, request.Version), ct)));

        // Into the open run, or into the run named, also after it ended, until the job is closed.
        jobs.MapPost("/{id:guid}/production", async Task<Ok<ReportProductionResponse>> (
            Guid id, ReportProductionRequest request, ICommandHandler<ReportProductionCommand, ReportProductionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ReportProductionCommand(id, request.RunId, request.GoodQty, request.ScrapQty, request.UomId, request.ScrapReasonCodeId,
                    request.LocationId, request.LotNumber, request.ExpiresAt, request.Comment),
                ct)));

        var sites = routes.MapGroup("/execution/sites").WithTags("Execution");

        sites.MapGet("/{siteId:guid}/board", async Task<Ok<IReadOnlyList<LineBoardRowDto>>> (
            Guid siteId, IQueryHandler<GetLineBoardQuery, IReadOnlyList<LineBoardRowDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLineBoardQuery(siteId), ct)));

        sites.MapGet("/{siteId:guid}/policy", async Task<Ok<ExecutionPolicyDto>> (
            Guid siteId, IQueryHandler<GetExecutionPolicyQuery, ExecutionPolicyDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetExecutionPolicyQuery(siteId), ct)));

        sites.MapPut("/{siteId:guid}/policy", async Task<Ok<ExecutionPolicyDto>> (
            Guid siteId, SetExecutionPolicyRequest request, ICommandHandler<SetExecutionPolicyCommand, ExecutionPolicyDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetExecutionPolicyCommand(siteId, request.RateTolerancePct, request.ConsumptionTolerancePct), ct)));

        return routes;
    }
}
