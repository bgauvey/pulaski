using Microsoft.AspNetCore.Http.HttpResults;
using Pulaski.Api.Security;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Audit;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Audit;
using Pulaski.Domain.Security;

namespace Pulaski.Api.Endpoints;

/// <summary>The audit trail (#129, D-128): every recorded change, searched. Events don't record a site, so it is for those who may see it everywhere.</summary>
internal static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/audit", async Task<Ok<AuditPageDto>> (
            Guid? subjectId, string? area, string? action, string? actor, string? text, DateTimeOffset? from, DateTimeOffset? to, string? before, int? limit,
            HttpContext http, IQueryHandler<SearchAuditQuery, AuditPageDto> handler, CancellationToken ct) =>
        {
            if (!(await CallerAccess.RequireAsync(http)).CanEverywhere(Permissions.AuditView))
            {
                throw new ForbiddenException($"The audit trail covers every site, so it needs permission to browse it everywhere.");
            }

            var cursor = AuditCursor.Parse(before);
            return TypedResults.Ok(await handler.HandleAsync(new SearchAuditQuery(new AuditFilter(
                subjectId, area, action, actor, text, from, to, cursor?.RecordedAt, cursor?.Id, limit ?? 50)), ct));
        })
        .WithTags("Audit")
        .RequireAny(null, Permissions.AuditView);

        return routes;
    }
}
