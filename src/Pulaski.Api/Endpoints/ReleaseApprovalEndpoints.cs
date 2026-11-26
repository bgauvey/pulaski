using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Approvals;
using Pulaski.Application.Security;
using Pulaski.Contracts.Approvals;
using Pulaski.Contracts.Common;
using Pulaski.Domain.Approvals;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>
/// Approval before release (#138, D-122): a site's settings; submitting a process draft; approving, returning and withdrawing requests. Approvers
/// hold <c>releases.approve</c> at one of a request's sites, which the handlers check against the sites the caller holds it at.
/// </summary>
internal static class ReleaseApprovalEndpoints
{
    public static IEndpointRouteBuilder MapReleaseApprovalEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/release-approval/settings/{siteId:guid}", async Task<Ok<ReleaseApprovalSettingsDto>> (
            Guid siteId, IQueryHandler<GetReleaseApprovalSettingsQuery, ReleaseApprovalSettingsDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetReleaseApprovalSettingsQuery(siteId), ct)))
        .WithTags("Release approval")
        .RequireAny(Site.Route(), Permissions.SiteSettingsView);

        routes.MapPut("/release-approval/settings/{siteId:guid}", async Task<Ok<VersionResponse>> (
            Guid siteId, SetReleaseApprovalSettingsRequest request, ICommandHandler<SetReleaseApprovalSettingsCommand, VersionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SetReleaseApprovalSettingsCommand(siteId, request.Version, request.Processes, request.Boms, request.Specs), ct)))
        .WithTags("Release approval")
        .RequirePermission(Permissions.SiteSettings, Site.Route());

        // A process draft: submitted by whoever maintains processes at its site; its status read by whoever sees processes there.
        routes.MapPost("/processes/{id:guid}/versions/{versionId:guid}/submit", async Task<Ok<CreatedResponse>> (
            Guid id, Guid versionId, SubmitReleaseRequest request, ICommandHandler<SubmitReleaseCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SubmitReleaseCommand(ReleaseKinds.Process, id, versionId, request.Version, request.Note), ct)))
        .WithTags("Release approval")
        .RequirePermission(Permissions.Processes, Site.Of(SiteOf.Process));

        routes.MapGet("/processes/{id:guid}/release-status", async Task<Ok<ReleaseStatusDto>> (
            Guid id, HttpContext http, IQueryHandler<GetReleaseStatusQuery, ReleaseStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetReleaseStatusQuery(ReleaseKinds.Process, id, await Visible.SitesAsync(http, Permissions.ReleaseApprove)), ct)))
        .WithTags("Release approval")
        .RequireAny(Site.Of(SiteOf.Process), Permissions.ProcessesView);

        // BOMs and specifications (#138b): submitted by who may release them, as their own release endpoints; read by who sees them.
        routes.MapPost("/boms/{id:guid}/versions/{versionId:guid}/submit", async Task<Ok<CreatedResponse>> (
            Guid id, Guid versionId, SubmitReleaseRequest request, ICommandHandler<SubmitReleaseCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SubmitReleaseCommand(ReleaseKinds.Bom, id, versionId, request.Version, request.Note), ct)))
        .WithTags("Release approval")
        .RequirePermission(Permissions.Processes);

        routes.MapGet("/boms/{id:guid}/release-status", async Task<Ok<ReleaseStatusDto>> (
            Guid id, HttpContext http, IQueryHandler<GetReleaseStatusQuery, ReleaseStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetReleaseStatusQuery(ReleaseKinds.Bom, id, await Visible.SitesAsync(http, Permissions.ReleaseApprove)), ct)))
        .WithTags("Release approval")
        .RequireAny(null, Permissions.ProcessesView);

        routes.MapPost("/quality/specs/{id:guid}/versions/{versionId:guid}/submit", async Task<Ok<CreatedResponse>> (
            Guid id, Guid versionId, SubmitReleaseRequest request, ICommandHandler<SubmitReleaseCommand, CreatedResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new SubmitReleaseCommand(ReleaseKinds.Spec, id, versionId, request.Version, request.Note), ct)))
        .WithTags("Release approval")
        .RequirePermission(Permissions.Specs);

        routes.MapGet("/quality/specs/{id:guid}/release-status", async Task<Ok<ReleaseStatusDto>> (
            Guid id, HttpContext http, IQueryHandler<GetReleaseStatusQuery, ReleaseStatusDto> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new GetReleaseStatusQuery(ReleaseKinds.Spec, id, await Visible.SitesAsync(http, Permissions.ReleaseApprove)), ct)))
        .WithTags("Release approval")
        .RequireAny(null, Permissions.QualityView);

        routes.MapPost("/release-requests/{id:guid}/approve", async Task<Ok<ReleaseDecisionResponse>> (
            Guid id, DecideReleaseRequest request, HttpContext http, ICommandHandler<ApproveReleaseCommand, ReleaseDecisionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ApproveReleaseCommand(id, request.Comment, await Visible.SitesAsync(http, Permissions.ReleaseApprove)), ct)))
        .WithTags("Release approval")
        .RequirePermission(Permissions.ReleaseApprove);

        routes.MapPost("/release-requests/{id:guid}/return", async Task<Ok<ReleaseDecisionResponse>> (
            Guid id, DecideReleaseRequest request, HttpContext http, ICommandHandler<ReturnReleaseCommand, ReleaseDecisionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new ReturnReleaseCommand(id, request.Comment, await Visible.SitesAsync(http, Permissions.ReleaseApprove)), ct)))
        .WithTags("Release approval")
        .RequirePermission(Permissions.ReleaseApprove);

        // Only who submitted it withdraws it; the handler checks.
        routes.MapPost("/release-requests/{id:guid}/withdraw", async Task<Ok<ReleaseDecisionResponse>> (
            Guid id, ICommandHandler<WithdrawReleaseCommand, ReleaseDecisionResponse> handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(new WithdrawReleaseCommand(id), ct)))
        .WithTags("Release approval")
        .RequireAnyView();

        return routes;
    }
}
