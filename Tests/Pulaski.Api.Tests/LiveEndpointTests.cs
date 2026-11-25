using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Live;
using Pulaski.Contracts.Live;

namespace Pulaski.Api.Tests;

/// <summary>Open screens get changes as server-sent events, signed in only (#143).</summary>
public sealed class LiveEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task A_signed_in_screen_hears_changes_as_they_are_published()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/live");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        await using var body = await response.Content.ReadAsStreamAsync();
        using var reader = new StreamReader(body);
        Assert.Equal(": connected", await reader.ReadLineAsync());

        var hub = factory.Services.GetRequiredService<LiveHub>();
        for (var i = 0; i < 50 && hub.Subscribers == 0; i++)
        {
            await Task.Delay(20);
        }

        var job = Guid.CreateVersion7();
        hub.Publish(new LiveChangeDto("pulaski.job.started.v1", "job", job, null));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        string? line;
        do
        {
            line = await reader.ReadLineAsync(timeout.Token);
        }
        while (line is not null && !line.StartsWith("data:", StringComparison.Ordinal));

        Assert.Contains(job.ToString(), line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Signed_out_there_is_nothing_to_hear() =>
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateSignedOutClient().GetAsync(new Uri("/api/live", UriKind.Relative))).StatusCode);
}
