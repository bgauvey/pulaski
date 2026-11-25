using System.Text.Json;
using Pulaski.Api.Security;
using Pulaski.Application.Live;
using Pulaski.Contracts.Live;

namespace Pulaski.Api.Endpoints;

/// <summary>
/// Live changes as server-sent events (#143, D-117): <c>GET /api/live</c> for a signed-in user, each change at a site they see (or with no site);
/// a <c>changes</c> event carries a batch, a comment line every 20 s keeps the connection open. No data: screens read it again through the API.
/// </summary>
internal static class LiveEndpoints
{
    public static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan Gather = TimeSpan.FromMilliseconds(250);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapLiveEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/live", async (HttpContext http, LiveHub hub, TimeProvider time, CancellationToken ct) =>
        {
            var visible = await Visible.AnyViewSitesAsync(http);
            await StreamAsync(http, hub, time, change => LiveSites.Visible(change, visible), ct);
        })
        .WithTags("Live")
        .RequireAnyView()
        .ExcludeFromDescription();

        return routes;
    }

    /// <summary>Streams the hub's changes that <paramref name="visible"/> lets through until the caller goes.</summary>
    public static async Task StreamAsync(HttpContext http, LiveHub hub, TimeProvider time, Func<LiveChangeDto, bool> visible, CancellationToken ct)
    {
        http.Response.Headers.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        http.Response.Headers["X-Accel-Buffering"] = "no";
        var (id, changes) = hub.Subscribe();
        try
        {
            await http.Response.WriteAsync(": connected\n\n", ct);
            await http.Response.Body.FlushAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                var batch = (await LiveHub.NextBatchAsync(changes, Heartbeat, Gather, time, ct)).Where(visible).ToList();
                await http.Response.WriteAsync(batch.Count == 0 ? ": ping\n\n" : Format(batch), ct);
                await http.Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // The screen went.
        }
        finally
        {
            hub.Unsubscribe(id);
        }
    }

    /// <summary>One server-sent event named <c>changes</c> with the batch as JSON on one line.</summary>
    public static string Format(IReadOnlyList<LiveChangeDto> batch) => $"event: changes\ndata: {JsonSerializer.Serialize(batch, Json)}\n\n";
}
