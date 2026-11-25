namespace Pulaski.Web.Services.Security;

/// <summary>
/// Files the operator station shows (#126b): the station is static, so a file of the running job's instructions is a link here, fetched with
/// the terminal's secret from its cookie. The API decides whether the station shows that file.
/// </summary>
public static class TerminalFileEndpoints
{
    public static IEndpointRouteBuilder MapTerminalFileEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/terminal/station/instructions/{id:guid}/files/{fileId:guid}", async (
            Guid id, Guid fileId, string? line, HttpContext http, PulaskiApiClient api, CancellationToken ct) =>
        {
            if (TerminalCookie.Get(http) is not { } secret)
            {
                return Results.NotFound();
            }

            try
            {
                var (content, type, name) = await api.GetStationInstructionFileAsync(secret, id, fileId, line, ct);
                http.Response.Headers.XContentTypeOptions = "nosniff";
                return Results.File(content, type, name);
            }
            catch (ApiException)
            {
                return Results.NotFound();
            }
        }).AllowAnonymous();

        return routes;
    }
}
