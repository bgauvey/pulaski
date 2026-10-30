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
            TypedResults.Ok(await handler.HandleAsync(new GetLineTimelineQuery(lineId, horizonDays), ct)))
        .RequireAny(Site.Of(SiteOf.Equipment, "lineId"), Permissions.ScheduleView, Permissions.OverviewView);

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
                new PreviewJobPlacementQuery(lineId, workOrderId, plannedQty, beforeJobId, jobNumber), ct)))
        .RequireAny(Site.Of(SiteOf.Equipment, "lineId"), Permissions.SchedulePlan);

        group.MapGet("/lines/{lineId:guid}/run-standards", async Task<Ok<IReadOnlyList<RunStandardDto>>> (
            Guid lineId,
            IQueryHandler<ListRunStandardsQuery, IReadOnlyList<RunStandardDto>> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListRunStandardsQuery(lineId), ct)))
        .RequireAny(Site.Of(SiteOf.Equipment, "lineId"), Permissions.ScheduleView, Permissions.MaterialsView);

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
                new CancelJobCommand(id, request.Version, request.Reason, request.Replace, request.ReplaceExact), ct)))
        .RequirePermission(Permissions.SchedulePlan, Site.Of(SiteOf.Job))
        .RequireWhen<CancelJobRequest>(r => r.Replace && r.ReplaceExact, Permissions.SchedulePlanPartial, Site.Of(SiteOf.Job),
            "You may not replace a job with a partial batch at this site: it needs permission to plan partial batches.");

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

        // A planning rate from run history (D-045): reviewed first, then accepted; or back to the ideal rate.
        group.MapGet("/run-standards/{id:guid}/history-rate", async Task<Ok<RunHistoryRateDto>> (
            Guid id, int? days, IQueryHandler<GetRunHistoryRateQuery, RunHistoryRateDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetRunHistoryRateQuery(id, days ?? Domain.Scheduling.RunHistoryRate.DefaultDays), ct)))
        .RequireAny(Site.Of(SiteOf.RunStandard), Permissions.MaterialsView, Permissions.ScheduleView);

        group.MapPost("/run-standards/{id:guid}/history-rate", async Task<Ok<VersionResponse>> (
            Guid id, AdoptHistoryRateRequest request, ICommandHandler<AdoptHistoryRateCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new AdoptHistoryRateCommand(id, request.Version, request.Days, request.RatePerHour), ct)))
        .RequirePermission(Permissions.RateModify, Site.Of(SiteOf.RunStandard));

        group.MapPost("/run-standards/{id:guid}/ideal-rate", async Task<Ok<VersionResponse>> (
            Guid id, UseIdealRateRequest request, ICommandHandler<UseIdealRateCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UseIdealRateCommand(id, request.Version), ct)))
        .RequirePermission(Permissions.RateModify, Site.Of(SiteOf.RunStandard));

        group.MapGet("/sites/{siteId:guid}/policy", async Task<Ok<SchedulingPolicyDto>> (
            Guid siteId,
            IQueryHandler<GetSchedulingPolicyQuery, SchedulingPolicyDto> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetSchedulingPolicyQuery(siteId), ct)))
        .RequireAny(Site.Route(), Permissions.ScheduleView, Permissions.SiteSettingsView);

        group.MapPut("/sites/{siteId:guid}/policy", async Task<Ok<VersionResponse>> (
            Guid siteId,
            SetSchedulingPolicyRequest request,
            ICommandHandler<SetSchedulingPolicyCommand, VersionResponse> handler,
            CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new SetSchedulingPolicyCommand(siteId, request.Version, request.ChangeoverMode), ct)))
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        // Changeover rules (D-015, D-074): the site's matrix, maintained with changeovers.manage; seen with the schedule or the materials.
        var changeovers = routes.MapGroup("/changeover-rules").WithTags("Scheduling");
        changeovers.MapGet("", async Task<Ok<IReadOnlyList<ChangeoverRuleDto>>> (
            Guid siteId, IQueryHandler<ListChangeoverRulesQuery, IReadOnlyList<ChangeoverRuleDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListChangeoverRulesQuery(siteId), ct)))
        .RequireAny(Site.Query(), Permissions.ScheduleView, Permissions.MaterialsView);

        changeovers.MapPost("", async Task<Ok<CreatedResponse>> (
            CreateChangeoverRuleRequest request, ICommandHandler<CreateChangeoverRuleCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new CreateChangeoverRuleCommand(request.SiteId, request.EquipmentId, request.FromMaterialId,
                request.FromMaterialClassId, request.ToMaterialId, request.ToMaterialClassId, request.DurationMinutes, request.Note), ct)))
        .RequirePermission(Permissions.Changeovers, Site.FromBody<CreateChangeoverRuleRequest>(SiteOf.Equipment, r => r.SiteId));

        changeovers.MapPut("/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateChangeoverRuleRequest request, ICommandHandler<UpdateChangeoverRuleCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new UpdateChangeoverRuleCommand(id, request.Version, request.DurationMinutes, request.Note, request.IsActive), ct)))
        .RequirePermission(Permissions.Changeovers, Site.Of(SiteOf.ChangeoverRule));

        // All or nothing; ?dryRun=true checks and reports without saving.
        changeovers.MapPost("/import/csv", async Task<Ok<Pulaski.Contracts.Materials.ImportMaterialsResponse>> (
            HttpContext http, Guid siteId, bool? dryRun,
            ICommandHandler<ImportChangeoverRulesCommand, Pulaski.Contracts.Materials.ImportMaterialsResponse> handler, CancellationToken ct) =>
        {
            using var reader = new StreamReader(http.Request.Body);
            var text = http.Request.ContentLength > MaxFileBytes ? null : await reader.ReadToEndAsync(ct);
            if (text is null || text.Length > MaxFileBytes)
            {
                throw new Pulaski.Application.Errors.ValidationException(new Dictionary<string, string[]>
                {
                    ["file"] = [$"The file is larger than {MaxFileBytes / 1024 / 1024} MB; split it."],
                });
            }

            return TypedResults.Ok(await handler.HandleAsync(new ImportChangeoverRulesCommand(siteId, ChangeoverRuleCsv.Parse(text), dryRun ?? false), ct));
        })
        .RequirePermission(Permissions.Changeovers, Site.Query())
        .Accepts<string>("text/csv", "text/plain");

        changeovers.MapGet("/import/template", () =>
            TypedResults.File(System.Text.Encoding.UTF8.GetBytes(ChangeoverRuleCsv.Template + "\r\n"), "text/csv", "changeover-rules.csv"))
        .RequireAny(null, Permissions.ScheduleView, Permissions.MaterialsView);

        return routes;
    }

    private const int MaxFileBytes = 5 * 1024 * 1024;
}
