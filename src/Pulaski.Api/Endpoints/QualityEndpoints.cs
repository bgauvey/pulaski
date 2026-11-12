using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Quality;
using Pulaski.Application.Security;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Quality;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>Quality (#65, D-085): characteristics, material specifications, results, and rejecting held lots on quality.</summary>
internal static class QualityEndpoints
{
    public static IEndpointRouteBuilder MapQualityEndpoints(this IEndpointRouteBuilder routes)
    {
        var quality = routes.MapGroup("/quality").WithTags("Quality");

        // Characteristics are enterprise-wide; lot attribute and result screens pick from them too.
        quality.MapGet("/characteristics", async Task<Ok<IReadOnlyList<CharacteristicDto>>> (
            IQueryHandler<ListCharacteristicsQuery, IReadOnlyList<CharacteristicDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListCharacteristicsQuery(), ct)))
        .RequireAnyView();

        quality.MapPost("/characteristics", async Task<Created<CreatedResponse>> (
            CreateCharacteristicRequest request, ICommandHandler<CreateCharacteristicCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(
                new CreateCharacteristicCommand(request.Code, request.Name, request.Description, request.DataKind, request.UomId, request.Decimals), ct);
            return TypedResults.Created($"/api/quality/characteristics/{created.Id}", created);
        })
        .RequirePermission(Permissions.Specs);

        quality.MapPut("/characteristics/{id:guid}", async Task<Ok<VersionResponse>> (
            Guid id, UpdateCharacteristicRequest request, ICommandHandler<UpdateCharacteristicCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new UpdateCharacteristicCommand(id, request.Version, request.Name, request.Description, request.UomId, request.Decimals, request.IsActive)
                {
                    ChartType = request.ChartType,
                    SubgroupSize = request.SubgroupSize,
                }, ct)))
        .RequirePermission(Permissions.Specs);

        // Control limits (D-099): worked out from a reviewed baseline and approved, or entered by hand; periods are ended, never changed.
        quality.MapGet("/characteristics/{id:guid}/control-limits", async Task<Ok<IReadOnlyList<ControlLimitDto>>> (
            Guid id, IQueryHandler<ListControlLimitsQuery, IReadOnlyList<ControlLimitDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListControlLimitsQuery(id), ct)))
        .RequireAny(null, Permissions.QualityView);

        // A control chart of results at a site (D-099), each point against the limits in force for it at its time.
        quality.MapGet("/characteristics/{id:guid}/chart", async Task<Ok<ControlChartDto>> (
            Guid id, Guid siteId, Guid materialId, Guid? equipmentId, DateTimeOffset from, DateTimeOffset to,
            IQueryHandler<GetControlChartQuery, ControlChartDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetControlChartQuery(siteId, id, materialId, equipmentId, from, to), ct)))
        .RequireAny(Site.Query(), Permissions.QualityView);

        quality.MapGet("/characteristics/{id:guid}/control-limits/baseline", async Task<Ok<LimitBaselineDto>> (
            Guid id, Guid materialId, Guid? equipmentId, DateTimeOffset from, DateTimeOffset to, string? leftOut,
            IQueryHandler<GetLimitBaselineQuery, LimitBaselineDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetLimitBaselineQuery(id, materialId, equipmentId, from, to, SampleIds(leftOut)), ct)))
        .RequireAny(null, Permissions.QualityView);

        quality.MapPost("/characteristics/{id:guid}/control-limits", async Task<Ok<ControlLimitChangeResponse>> (
            Guid id, ApproveControlLimitsRequest r, ICommandHandler<ApproveControlLimitsCommand, ControlLimitChangeResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ApproveControlLimitsCommand(
                id, r.MaterialId, r.EquipmentId, r.From, r.To, r.LeftOutSampleIds, r.CenterLine, r.Ucl, r.Lcl, r.EffectiveFrom, r.Note), ct)))
        .RequirePermission(Permissions.Specs);

        quality.MapPost("/characteristics/{id:guid}/control-limits/manual", async Task<Ok<ControlLimitChangeResponse>> (
            Guid id, SetManualControlLimitsRequest r, ICommandHandler<SetManualControlLimitsCommand, ControlLimitChangeResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetManualControlLimitsCommand(
                id, r.MaterialId, r.EquipmentId, r.CenterLine, r.Ucl, r.Lcl, r.DispersionCenter, r.DispersionUcl, r.DispersionLcl, r.EffectiveFrom, r.Note), ct)))
        .RequirePermission(Permissions.Specs);

        quality.MapPost("/control-limits/{id:guid}/end", async Task<Ok<ControlLimitChangeResponse>> (
            Guid id, EndControlLimitsRequest r, ICommandHandler<EndControlLimitsCommand, ControlLimitChangeResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new EndControlLimitsCommand(id, r.Note), ct)))
        .RequirePermission(Permissions.Specs);

        quality.MapGet("/specs", async Task<Ok<IReadOnlyList<SpecSummaryDto>>> (
            IQueryHandler<ListSpecsQuery, IReadOnlyList<SpecSummaryDto>> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ListSpecsQuery(), ct)))
        .RequireAny(null, Permissions.QualityView);

        quality.MapGet("/specs/{id:guid}", async Task<Ok<SpecDto>> (Guid id, IQueryHandler<GetSpecQuery, SpecDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetSpecQuery(id), ct)))
        .RequireAny(null, Permissions.QualityView);

        quality.MapPost("/specs", async Task<Created<CreatedResponse>> (
            CreateSpecRequest request, ICommandHandler<CreateSpecCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new CreateSpecCommand(request.MaterialId), ct);
            return TypedResults.Created($"/api/quality/specs/{created.Id}", created);
        })
        .RequirePermission(Permissions.Specs);

        quality.MapGet("/specs/{id:guid}/versions/{versionId:guid}", async Task<Ok<SpecVersionDto>> (
            Guid id, Guid versionId, IQueryHandler<GetSpecVersionQuery, SpecVersionDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetSpecVersionQuery(id, versionId), ct)))
        .RequireAny(null, Permissions.QualityView);

        quality.MapPut("/specs/{id:guid}/versions/{versionId:guid}", async Task<Ok<SaveSpecDraftResponse>> (
            Guid id, Guid versionId, SaveSpecDraftRequest request, ICommandHandler<SaveSpecDraftCommand, SaveSpecDraftResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SaveSpecDraftCommand(id, versionId, request.Version, request.Definition), ct)))
        .RequirePermission(Permissions.Specs);

        quality.MapPost("/specs/{id:guid}/versions/{versionId:guid}/release", async Task<Ok<VersionResponse>> (
            Guid id, Guid versionId, ReleaseSpecVersionRequest request, ICommandHandler<ReleaseSpecVersionCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReleaseSpecVersionCommand(id, versionId, request.Version, request.Note), ct)))
        .RequirePermission(Permissions.Specs);

        quality.MapPost("/specs/{id:guid}/drafts", async Task<Created<CreatedResponse>> (
            Guid id, ICommandHandler<StartSpecDraftCommand, CreatedResponse> handler, CancellationToken ct) =>
        {
            var created = await handler.HandleAsync(new StartSpecDraftCommand(id), ct);
            return TypedResults.Created($"/api/quality/specs/{id}/versions/{created.Id}", created);
        })
        .RequirePermission(Permissions.Specs);

        // A material's released specification: the result form, the lot page and the shop order sheet read it.
        routes.MapGet("/materials/{id:guid}/specification", async Task<Ok<ReleasedSpecDto>> (
            Guid id, IQueryHandler<GetReleasedSpecQuery, ReleasedSpecDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetReleasedSpecQuery(id), ct)))
            .WithTags("Quality")
        .RequireAnyView();

        quality.MapGet("/results", async Task<Ok<IReadOnlyList<QualityResultDto>>> (
            Guid? siteId,
            Guid? materialId,
            Guid? lotId,
            Guid? jobId,
            Guid? characteristicId,
            bool? failingOnly,
            DateTimeOffset? from,
            DateTimeOffset? to,
            int? limit,
            HttpContext http,
            IQueryHandler<ListQualityResultsQuery, IReadOnlyList<QualityResultDto>> handler,
            CancellationToken ct) =>
        {
            var sites = await Visible.SitesAsync(http, Permissions.QualityView);
            return TypedResults.Ok(await handler.HandleAsync(
                new ListQualityResultsQuery(new QualityResultFilter(sites, siteId, materialId, lotId, jobId, characteristicId, failingOnly ?? false, from, to, limit ?? 500)),
                ct));
        })
        .RequireAnyAtSite(Site.Query(), Permissions.QualityView);

        quality.MapPost("/results", async Task<Ok<QualityResultResponse>> (
            Guid siteId, RecordQualityResultRequest request, ICommandHandler<RecordQualityResultCommand, QualityResultResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(
                new RecordQualityResultCommand(siteId, request.CharacteristicId, request.LotId, request.JobId, request.OccurredAt, request.Values,
                    request.Inspected, request.Defects, request.Source, request.Comment),
                ct)))
        .RequirePermission(Permissions.QualityRecord, Site.Query());

        quality.MapPost("/results/{id:guid}/exclude", async Task<Ok<QualityResultDto>> (
            Guid id, Guid siteId, ExcludeQualityResultRequest request, ICommandHandler<ExcludeQualityResultCommand, QualityResultDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ExcludeQualityResultCommand(siteId, id, request.OccurredAt, request.ReasonCodeId, request.Comment), ct)))
        .RequirePermission(Permissions.QualityDecide, Site.Query());

        routes.MapPost("/lots/{id:guid}/reject", async Task<Ok<LotTransactionResponse>> (
            Guid id, RejectLotRequest request, ICommandHandler<RejectLotCommand, LotTransactionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new RejectLotCommand(id, request.Version, request.ScrapReasonCodeId, request.Comment), ct)))
            .WithTags("Quality")
        .RequirePermission(Permissions.QualityDecide, Site.Of(SiteOf.Lot));

        return routes;
    }

    // Comma-separated result ids left out of a baseline.
    private static IReadOnlyList<Guid> SampleIds(string? text) =>
        [.. (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => Guid.TryParse(t, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

}
