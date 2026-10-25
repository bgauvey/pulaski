using System.Net;
using Microsoft.Extensions.Configuration;
using Pulaski.Api.Integrations;
using Pulaski.Application.Materials;

namespace Pulaski.Api.Tests;

/// <summary>The OData client of material connections (D-068): its query, paging and credentials.</summary>
public sealed class ODataMaterialFetcherTests
{
    private static readonly MaterialConnection Connection = new(
        Guid.CreateVersion7(), "ERP-ITEMS", "ERP items", "odata", "https://erp.example/odata/", "Products", "Active eq true", "ERP1",
        new Dictionary<string, string> { ["code"] = "Number", ["external_key"] = "Id", ["base_unit"] = "Unit/Code" }, true);

    [Fact]
    public async Task It_selects_what_the_map_reads_follows_next_links_and_sends_the_configured_credentials()
    {
        var handler = new Pages(
            """{"value": [{"Id": 1}], "@odata.nextLink": "Products?$skiptoken=2"}""",
            """{"value": [{"Id": 2}, {"Id": 3}]}""");
        var fetcher = Fetcher(handler, ("MaterialConnections:ERP-ITEMS:Username", "svc"), ("MaterialConnections:ERP-ITEMS:Password", "pw"));

        var result = await fetcher.FetchAsync(Connection, 5000, CancellationToken.None);

        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Items.Count);
        Assert.Equal("https://erp.example/odata/Products?$select=Id%2CNumber%2CUnit&$filter=Active%20eq%20true", handler.Requests[0].RequestUri!.OriginalString);
        Assert.Equal("https://erp.example/odata/Products?$skiptoken=2", handler.Requests[1].RequestUri!.ToString());
        Assert.Equal("Basic", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("4.0", handler.Requests[0].Headers.GetValues("OData-MaxVersion").Single());
        Assert.True(fetcher.HasCredentials("ERP-ITEMS"));
        Assert.False(fetcher.HasCredentials("OTHER"));
    }

    [Fact]
    public async Task A_refusal_too_many_items_or_an_answer_that_isnt_a_collection_is_a_problem_not_an_exception()
    {
        var refused = await Fetcher(new Pages(HttpStatusCode.Unauthorized, "no")).FetchAsync(Connection, 5000, CancellationToken.None);
        Assert.Equal(["ERP-ITEMS: the service answered 401: no."], refused.Problems);

        var many = await Fetcher(new Pages("""{"value": [{"Id": 1}, {"Id": 2}]}""")).FetchAsync(Connection, 1, CancellationToken.None);
        Assert.Contains("more than 1 items", Assert.Single(many.Problems), StringComparison.Ordinal);

        var odd = await Fetcher(new Pages("""{"items": []}""")).FetchAsync(Connection, 5000, CancellationToken.None);
        Assert.Contains("isn't an OData collection", Assert.Single(odd.Problems), StringComparison.Ordinal);
    }

    private static ODataMaterialFetcher Fetcher(Pages handler, params (string Key, string Value)[] settings) => new(
        new Clients(handler),
        new ConfigurationBuilder().AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build());

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Pages : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _answers;

        public Pages(params string[] bodies) => _answers = new(bodies.Select(b => (HttpStatusCode.OK, b)));

        public Pages(HttpStatusCode status, string body) => _answers = new([(status, body)]);

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = _answers.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }
}
