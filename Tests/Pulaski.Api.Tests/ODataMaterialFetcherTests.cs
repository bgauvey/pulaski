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

    [Fact]
    public async Task A_rest_connection_reads_items_at_its_path_and_follows_the_next_link_relative_to_the_page()
    {
        var rest = Connection with { Kind = "rest", BaseUrl = "https://erp.example/api/items?plant=1000", EntitySet = "data.items", Filter = null, NextLinkPath = "links.next" };
        var handler = new Pages(
            """{"data": {"items": [{"Id": 1}]}, "links": {"next": "/api/items?plant=1000&page=2"}}""",
            """{"data": {"items": [{"Id": 2}]}, "links": {"next": null}}""");

        var result = await Fetcher(handler).FetchAsync(rest, 5000, CancellationToken.None);

        Assert.Empty(result.Problems);
        Assert.Equal(2, result.Items.Count);
        Assert.Equal("https://erp.example/api/items?plant=1000&page=2", handler.Requests[1].RequestUri!.ToString());

        var wrong = await Fetcher(new Pages("""{"data": {}}""")).FetchAsync(rest, 5000, CancellationToken.None);
        Assert.Contains("no array at 'data.items'", Assert.Single(wrong.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_soap_connection_posts_its_envelope_and_reads_items_by_element_and_a_fault_is_a_problem()
    {
        var soap = Connection with
        {
            Kind = "soap",
            BaseUrl = "https://erp.example/soap",
            EntitySet = "Material",
            Filter = null,
            RequestBody = "<Envelope><Body><GetMaterials/></Body></Envelope>",
            SoapAction = "urn:GetMaterials",
        };
        var handler = new Pages("""
            <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><GetMaterialsResponse>
              <Material><Id>1</Id><Number>PP-1</Number><Unit><Code>kg</Code></Unit></Material>
              <Material><Id>2</Id><Number>PP-2</Number><Unit><Code>kg</Code></Unit></Material>
            </GetMaterialsResponse></s:Body></s:Envelope>
            """);

        var result = await Fetcher(handler).FetchAsync(soap, 5000, CancellationToken.None);

        Assert.Empty(result.Problems);
        Assert.Equal(["PP-1", "PP-2"], result.Items.Select(i => i.GetProperty("Number").GetString()));
        Assert.Equal("kg", result.Items[0].GetProperty("Unit").GetProperty("Code").GetString());
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal("\"urn:GetMaterials\"", handler.Requests[0].Headers.GetValues("SOAPAction").Single());
        Assert.Equal("<Envelope><Body><GetMaterials/></Body></Envelope>", handler.Bodies[0]);

        var fault = await Fetcher(new Pages(HttpStatusCode.InternalServerError,
            """<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/"><s:Body><s:Fault><faultstring>No plant</faultstring></s:Fault></s:Body></s:Envelope>"""))
            .FetchAsync(soap, 5000, CancellationToken.None);
        Assert.Contains("SOAP fault: No plant", Assert.Single(fault.Problems), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_database_connection_without_a_connection_string_is_a_problem_and_counts_as_no_credentials()
    {
        var database = Connection with { Kind = "database", BaseUrl = null, EntitySet = "", Filter = null, Query = "SELECT 1" };
        var fetcher = Fetcher(new Pages());

        var result = await fetcher.FetchAsync(database, 5000, CancellationToken.None);

        Assert.Contains("no connection string is configured", Assert.Single(result.Problems), StringComparison.Ordinal);
        Assert.False(fetcher.HasCredentials("ERP-ITEMS"));
        Assert.True(Fetcher(new Pages(), ("MaterialConnections:ERP-ITEMS:ConnectionString", "Host=erp")).HasCredentials("ERP-ITEMS"));
    }

    [Fact]
    public async Task A_scim_connection_pages_through_users_by_start_index_with_its_filter()
    {
        var scim = Connection with { Kind = "scim", BaseUrl = "https://hr.example/scim/v2", EntitySet = "", Filter = "active eq true" };
        var handler = new Pages(
            """{"totalResults": 3, "startIndex": 1, "itemsPerPage": 2, "Resources": [{"id": "u1"}, {"id": "u2"}]}""",
            """{"totalResults": 3, "startIndex": 3, "itemsPerPage": 2, "Resources": [{"id": "u3"}]}""");

        var result = await Fetcher(handler).FetchAsync(scim, 5000, CancellationToken.None);

        Assert.Empty(result.Problems);
        Assert.Equal(["u1", "u2", "u3"], result.Items.Select(i => i.GetProperty("id").GetString()));
        Assert.Equal("https://hr.example/scim/v2/Users?startIndex=1&count=100&filter=active%20eq%20true", handler.Requests[0].RequestUri!.OriginalString);
        Assert.StartsWith("https://hr.example/scim/v2/Users?startIndex=3&", handler.Requests[1].RequestUri!.OriginalString, StringComparison.Ordinal);

        var empty = await Fetcher(new Pages("""{"totalResults": 0}""")).FetchAsync(scim, 5000, CancellationToken.None);
        Assert.Equal((0, 0), (empty.Items.Count, empty.Problems.Count));
        var odd = await Fetcher(new Pages("""[{"id": "u1"}]""")).FetchAsync(scim, 5000, CancellationToken.None);
        Assert.Contains("isn't a SCIM ListResponse", Assert.Single(odd.Problems), StringComparison.Ordinal);
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

        public List<string?> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken));
            var (status, body) = _answers.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }
}
