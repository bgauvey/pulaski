namespace Pulaski.Web.Services.Security;

/// <summary>
/// What boards and stations fetch through the web with the terminal's secret (#126b, #143b). Files the operator station shows (#126b): the station is static, so a file of the running job's instructions is a link here, fetched with
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

        // Live changes for a board or station (#143b): the browser can't send the terminal's secret itself, so its cookie is read here and the
        // API's stream passed through as it comes. An unenrolled device gets 404; the script then keeps to its timer.
        // Whether this device still reaches Pulaski (#134, D-133): the floor screens ask before reloading or sending a form. Nothing else.
        routes.MapGet("/terminal/ping", (HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return Results.NoContent();
        }).AllowAnonymous();

        routes.MapGet("/terminal/live", async (HttpContext http, PulaskiApiClient api, CancellationToken ct) =>
        {
            if (TerminalCookie.Get(http) is not { } secret)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            HttpResponseMessage upstream;
            try
            {
                upstream = await api.OpenTerminalLiveAsync(secret, ct);
            }
            catch (ApiException)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            using (upstream)
            {
                http.Response.ContentType = "text/event-stream";
                http.Response.Headers.CacheControl = "no-cache";
                http.Response.Headers["X-Accel-Buffering"] = "no";
                await using var body = await upstream.Content.ReadAsStreamAsync(ct);
                var buffer = new byte[4096];
                try
                {
                    int read;
                    while ((read = await body.ReadAsync(buffer, ct)) > 0)
                    {
                        await http.Response.Body.WriteAsync(buffer.AsMemory(0, read), ct);
                        await http.Response.Body.FlushAsync(ct);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or HttpRequestException)
                {
                    // The screen went, or the API did; the script reconnects.
                }
            }
        }).AllowAnonymous();

        return routes;
    }
}
