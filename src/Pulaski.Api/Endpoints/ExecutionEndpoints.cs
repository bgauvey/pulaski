using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Security;
using Pulaski.Application.Execution;
using Pulaski.Contracts.Execution;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Job execution and production reporting (D-035): start, pause, complete, report, and the floor board.</summary>
internal static class ExecutionEndpoints
{
    public static IEndpointRouteBuilder MapExecutionEndpoints(this IEndpointRouteBuilder routes)
    {
        var jobs = routes.MapGroup("/jobs").WithTags("Execution");

        jobs.MapGet("/{id:guid}/execution", async Task<Ok<JobExecutionDto>> (
            Guid id, IQueryHandler<GetJobExecutionQuery, JobExecutionDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetJobExecutionQuery(id), ct)))
        .RequireAny(Site.Of(SiteOf.Job), Permissions.FloorView, Permissions.InventoryView);

        jobs.MapPost("/{id:guid}/start", async Task<Ok<ExecutionResponse>> (
            Guid id, StartJobRequest request, ICommandHandler<StartJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new StartJobCommand(id, request.Version, request.LineId, request.SkipChangeover), ct)))
        .RequirePermission(Permissions.JobExecute, Site.Of(SiteOf.Job));

        jobs.MapPost("/{id:guid}/pause", async Task<Ok<ExecutionResponse>> (
            Guid id, PauseJobRequest request, ICommandHandler<PauseJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new PauseJobCommand(id, request.Version, request.ReasonCodeId, request.Comment, Partial(request.PartialBatch)), ct)))
        .RequirePermission(Permissions.JobExecute, Site.Of(SiteOf.Job));

        jobs.MapPost("/{id:guid}/complete", async Task<Ok<ExecutionResponse>> (
            Guid id, CompleteJobRequest request, ICommandHandler<CompleteJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new CompleteJobCommand(id, request.Version, request.ReasonCodeId, request.Comment, Partial(request.PartialBatch)), ct)))
        .RequirePermission(Permissions.JobExecute, Site.Of(SiteOf.Job));

        jobs.MapPost("/{id:guid}/reopen", async Task<Ok<ExecutionResponse>> (
            Guid id, JobVersionRequest request, ICommandHandler<ReopenJobCommand, ExecutionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReopenJobCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.JobReopen, Site.Of(SiteOf.Job));

        // Into the open run, or into the run named, also after it ended, until the job is closed.
        jobs.MapPost("/{id:guid}/production", async Task<Ok<ReportProductionResponse>> (
            Guid id, ReportProductionRequest request, ICommandHandler<ReportProductionCommand, ReportProductionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new ReportProductionCommand(id, request.RunId, request.GoodQty, request.ScrapQty, request.UomId, request.ScrapReasonCodeId,
                    request.LocationId, request.LotNumber, request.ExpiresAt, request.Comment)
                {
                    GoodBatches = request.GoodBatches,
                    ScrapBatches = request.ScrapBatches,
                    PartialQty = request.PartialQty,
                },
                ct)))
        .RequirePermission(Permissions.ProductionReport, Site.Of(SiteOf.Job));

        // A report reversed whole, with its counts and lots (D-063).
        jobs.MapPost("/{id:guid}/production/{reportId:guid}/reverse", async Task<Ok<ReverseProductionResponse>> (
            Guid id, Guid reportId, ReverseProductionRequest request, ICommandHandler<ReverseProductionReportCommand, ReverseProductionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReverseProductionReportCommand(id, reportId, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.ProductionCorrect, Site.Of(SiteOf.Job));

        // Partial batches waiting for approval (D-036).
        var partials = routes.MapGroup("/partial-batches").WithTags("Execution");

        partials.MapPost("/{id:guid}/approve", async Task<Ok<PartialBatchDecisionResponse>> (
            Guid id, DecidePartialBatchRequest request, ICommandHandler<ApprovePartialBatchCommand, PartialBatchDecisionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ApprovePartialBatchCommand(id, request.Comment), ct)))
        .RequirePermission(Permissions.PartialBatchDecide, Site.Of(SiteOf.PartialBatch));

        partials.MapPost("/{id:guid}/reject", async Task<Ok<PartialBatchDecisionResponse>> (
            Guid id, DecidePartialBatchRequest request, ICommandHandler<RejectPartialBatchCommand, PartialBatchDecisionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RejectPartialBatchCommand(id, request.ScrapReasonCodeId ?? Guid.Empty, request.Comment), ct)))
        .RequirePermission(Permissions.PartialBatchDecide, Site.Of(SiteOf.PartialBatch));

        var sites = routes.MapGroup("/execution/sites").WithTags("Execution");

        sites.MapGet("/{siteId:guid}/board", async Task<Ok<IReadOnlyList<LineBoardRowDto>>> (
            Guid siteId, IQueryHandler<GetLineBoardQuery, IReadOnlyList<LineBoardRowDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLineBoardQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.FloorView);

        sites.MapGet("/{siteId:guid}/partial-batches", async Task<Ok<IReadOnlyList<PartialBatchDto>>> (
            Guid siteId, IQueryHandler<ListPendingPartialBatchesQuery, IReadOnlyList<PartialBatchDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListPendingPartialBatchesQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.FloorView);

        // Good and scrap by shift for production days (D-044); at most 93 days apart.
        sites.MapGet("/{siteId:guid}/production-by-shift", async Task<Ok<ProductionByShiftDto>> (
            Guid siteId, DateOnly from, DateOnly to, IQueryHandler<GetProductionByShiftQuery, ProductionByShiftDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetProductionByShiftQuery(siteId, from, to), ct)))
        .RequireAny(Site.Route(), Permissions.FloorView);

        sites.MapGet("/{siteId:guid}/policy", async Task<Ok<ExecutionPolicyDto>> (
            Guid siteId, IQueryHandler<GetExecutionPolicyQuery, ExecutionPolicyDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetExecutionPolicyQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.FloorView, Permissions.SiteSettingsView);

        sites.MapPut("/{siteId:guid}/policy", async Task<Ok<ExecutionPolicyDto>> (
            Guid siteId, SetExecutionPolicyRequest request, ICommandHandler<SetExecutionPolicyCommand, ExecutionPolicyDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetExecutionPolicyCommand(siteId, request.Version, request.RateTolerancePct, request.ConsumptionTolerancePct), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        return routes;
    }

    private static PartialBatchInput? Partial(PartialBatchRequest? request) =>
        request is null ? null : new PartialBatchInput(request.Quantity, request.LocationId, request.LotNumber);
}
