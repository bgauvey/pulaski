using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pulaski.Application.Materials;

namespace Pulaski.Api.Integrations;

/// <summary>
/// Fetches a connection's items (D-068, #72, #71). SCIM (people): <c>GET {base}/Users</c>, paged by <c>startIndex</c>. REST: <c>GET</c> the address, the items at a path, following a next-page address. SOAP:
/// <c>POST</c> the envelope, the items the answer's elements of one name. Database: the connection's SELECT on a PostgreSQL database
/// (<c>ConnectionString</c>), read-only with a 30 s statement timeout. OData: from its OData v4 service: <c>GET {root}/{entity set}?$select=…&amp;$filter=…</c>, following
/// <c>@odata.nextLink</c> page by page. Credentials come from <c>MaterialConnections:&lt;code&gt;</c> in configuration: <c>BearerToken</c>, or
/// <c>Username</c> and <c>Password</c> for basic authentication. Problems are returned, never thrown, so the screen can show them.
/// </summary>
public sealed class ODataMaterialFetcher(IHttpClientFactory clients, IConfiguration configuration) : IMaterialConnectionFetcher
{
    public const string ClientName = "material-connections";
    public const string Section = "MaterialConnections";
    private const int MaxPages = 200;

    private const int ScimPageSize = 100;

    public bool HasCredentials(string code)
    {
        var section = configuration.GetSection($"{Section}:{code}");
        return !string.IsNullOrEmpty(section["BearerToken"]) || !string.IsNullOrEmpty(section["Username"]) || !string.IsNullOrEmpty(section["ConnectionString"]);
    }

    public Task<MaterialConnectionFetch> FetchAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken) => connection.Kind switch
    {
        MaterialConnectionKinds.Rest => RestAsync(connection, maxItems, cancellationToken),
        MaterialConnectionKinds.Soap => SoapAsync(connection, maxItems, cancellationToken),
        MaterialConnectionKinds.Database => DatabaseAsync(connection, maxItems, cancellationToken),
        MaterialConnectionKinds.Scim => ScimAsync(connection, maxItems, cancellationToken),
        _ => ODataAsync(connection, maxItems, cancellationToken),
    };

    private async Task<MaterialConnectionFetch> RestAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(ClientName);
        var items = new List<JsonElement>();
        Uri? next = new(connection.BaseUrl!);
        for (var page = 0; next is not null; page++)
        {
            if (page == MaxPages)
            {
                return Problem(connection, $"the service sent more than {MaxPages} pages.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Authorize(request, connection.Code);
            var (body, problem) = await SendAsync(http, request, connection, cancellationToken);
            if (problem is not null)
            {
                return new MaterialConnectionFetch([], [problem]);
            }

            try
            {
                using var document = JsonDocument.Parse(body!);
                var (found, why) = ConnectionItems.Rest(document.RootElement, connection.EntitySet);
                if (found is null)
                {
                    return Problem(connection, why!);
                }

                items.AddRange(found);
                if (items.Count > maxItems)
                {
                    return Problem(connection, $"the service has more than {maxItems} items; one import takes at most that.");
                }

                next = ConnectionItems.NextLink(document.RootElement, connection.NextLinkPath) is { } href ? new Uri(next, href) : null;
            }
            catch (JsonException)
            {
                return Problem(connection, "the answer isn't JSON.");
            }
        }

        return new MaterialConnectionFetch(items, []);
    }

    /// <summary>
    /// SCIM 2.0 users (RFC 7644 §3.4.2): <c>GET {base}/Users</c> with the filter, a page of <see cref="ScimPageSize"/> at a time by
    /// <c>startIndex</c>, until <c>totalResults</c> are read or a page comes back empty (#71).
    /// </summary>
    private async Task<MaterialConnectionFetch> ScimAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(ClientName);
        var items = new List<JsonElement>();
        for (var page = 0; ; page++)
        {
            if (page == MaxPages)
            {
                return Problem(connection, $"the service sent more than {MaxPages} pages.");
            }

            var address = $"{connection.BaseUrl!.TrimEnd('/')}/Users?startIndex={items.Count + 1}&count={ScimPageSize}"
                + (connection.Filter is { } filter ? $"&filter={Uri.EscapeDataString(filter)}" : "");
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/scim+json"));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            Authorize(request, connection.Code);
            var (body, problem) = await SendAsync(http, request, connection, cancellationToken);
            if (problem is not null)
            {
                return new MaterialConnectionFetch([], [problem]);
            }

            try
            {
                using var document = JsonDocument.Parse(body!);
                var root = document.RootElement;
                // An empty ListResponse may leave Resources out, but always says totalResults.
                var hasResources = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Resources", out _);
                if (root.ValueKind != JsonValueKind.Object || (!hasResources && !root.TryGetProperty("totalResults", out _)))
                {
                    return Problem(connection, "the answer isn't a SCIM ListResponse.");
                }

                var users = root.TryGetProperty("Resources", out var resources) && resources.ValueKind == JsonValueKind.Array
                    ? [.. resources.EnumerateArray().Select(e => e.Clone())]
                    : new List<JsonElement>();
                items.AddRange(users);
                if (items.Count > maxItems)
                {
                    return Problem(connection, $"the service has more than {maxItems} users; one import takes at most that. Narrow it with a filter.");
                }

                var total = root.TryGetProperty("totalResults", out var t) && t.TryGetInt32(out var n) ? n : items.Count;
                if (users.Count == 0 || items.Count >= total)
                {
                    return new MaterialConnectionFetch(items, []);
                }
            }
            catch (JsonException)
            {
                return Problem(connection, "the answer isn't JSON.");
            }
        }
    }

    private async Task<MaterialConnectionFetch> SoapAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, connection.BaseUrl)
        {
            Content = new StringContent(connection.RequestBody!, Encoding.UTF8, "text/xml"),
        };
        if (connection.SoapAction is { } action)
        {
            request.Headers.Add("SOAPAction", $"\"{action}\"");
        }

        Authorize(request, connection.Code);
        var (body, problem) = await SendAsync(clients.CreateClient(ClientName), request, connection, cancellationToken, acceptFault: true);
        if (problem is not null)
        {
            return new MaterialConnectionFetch([], [problem]);
        }

        var (items, why) = ConnectionItems.Soap(body!, connection.EntitySet);
        return items is null ? Problem(connection, why!)
            : items.Count > maxItems ? Problem(connection, $"the service sent more than {maxItems} items; one import takes at most that.")
            : new MaterialConnectionFetch(items, []);
    }

    private async Task<MaterialConnectionFetch> DatabaseAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
    {
        if (configuration[$"{Section}:{connection.Code}:ConnectionString"] is not { Length: > 0 } connectionString)
        {
            return Problem(connection, $"no connection string is configured ({Section}:{connection.Code}:ConnectionString).");
        }

        try
        {
            await using var database = new Npgsql.NpgsqlConnection(connectionString);
            await database.OpenAsync(cancellationToken);
            await using var transaction = await database.BeginTransactionAsync(cancellationToken);
            await using (var setup = new Npgsql.NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '30s'", database, transaction))
            {
                await setup.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var query = new Npgsql.NpgsqlCommand(connection.Query, database, transaction);
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            var items = new List<JsonElement>();
            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.Count == maxItems)
                {
                    return Problem(connection, $"the query returns more than {maxItems} rows; one import takes at most that.");
                }

                items.Add(ConnectionItems.Row([.. Enumerable.Range(0, reader.FieldCount).Select(i => (reader.GetName(i), reader.IsDBNull(i) ? null : reader.GetValue(i)))]));
            }

            return new MaterialConnectionFetch(items, []);
        }
        catch (Npgsql.NpgsqlException ex)
        {
            return Problem(connection, $"the database refused the query: {ex.Message}");
        }
        catch (ArgumentException ex)
        {
            return Problem(connection, $"the connection string isn't usable: {ex.Message}");
        }
    }

    private static async Task<(string? Body, string? Problem)> SendAsync(
        HttpClient http, HttpRequestMessage request, MaterialConnection connection, CancellationToken cancellationToken, bool acceptFault = false)
    {
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            // A SOAP fault comes back as 500 with its envelope, which says more than the status does.
            return response.IsSuccessStatusCode || acceptFault && body.Contains("Fault", StringComparison.Ordinal)
                ? (body, null)
                : (null, $"{connection.Code}: the service answered {(int)response.StatusCode}{Detail(body)}.");
        }
        catch (HttpRequestException ex)
        {
            return (null, $"{connection.Code}: couldn't reach the service: {ex.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, $"{connection.Code}: the service didn't answer in time.");
        }
    }

    private static MaterialConnectionFetch Problem(MaterialConnection connection, string problem) => new([], [$"{connection.Code}: {problem}"]);

    private async Task<MaterialConnectionFetch> ODataAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
    {
        var http = clients.CreateClient(ClientName);
        var items = new List<JsonElement>();
        Uri? next = new(FirstPage(connection));
        for (var page = 0; next is not null; page++)
        {
            if (page == MaxPages)
            {
                return new MaterialConnectionFetch([], [$"The service sent more than {MaxPages} pages; narrow it with a filter."]);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, next);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.Add("OData-MaxVersion", "4.0");
            Authorize(request, connection.Code);
            try
            {
                using var response = await http.SendAsync(request, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    return new MaterialConnectionFetch([], [$"{connection.Code}: the service answered {(int)response.StatusCode}{Detail(body)}."]);
                }

                using var document = JsonDocument.Parse(body);
                if (!document.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array)
                {
                    return new MaterialConnectionFetch([], [$"{connection.Code}: the answer isn't an OData collection (no \"value\" array)."]);
                }

                items.AddRange(value.EnumerateArray().Select(e => e.Clone()));
                if (items.Count > maxItems)
                {
                    return new MaterialConnectionFetch([], [$"{connection.Code}: the service has more than {maxItems} items; one import takes at most that. Narrow it with a filter."]);
                }

                next = document.RootElement.TryGetProperty("@odata.nextLink", out var link) && link.GetString() is { Length: > 0 } href
                    ? new Uri(next, href)
                    : null;
            }
            catch (HttpRequestException ex)
            {
                return new MaterialConnectionFetch([], [$"{connection.Code}: couldn't reach the service: {ex.Message}"]);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new MaterialConnectionFetch([], [$"{connection.Code}: the service didn't answer in time."]);
            }
            catch (JsonException)
            {
                return new MaterialConnectionFetch([], [$"{connection.Code}: the answer isn't JSON."]);
            }
        }

        return new MaterialConnectionFetch(items, []);
    }

    /// <summary>The first page's address: the entity set, the properties the map reads, and the filter.</summary>
    public static string FirstPage(MaterialConnection connection)
    {
        var query = new StringBuilder("?$select=").Append(Uri.EscapeDataString(string.Join(",", ODataMaterialRows.Selected(connection.FieldMap))));
        if (connection.Filter is { } filter)
        {
            query.Append("&$filter=").Append(Uri.EscapeDataString(filter));
        }

        return $"{connection.BaseUrl!.TrimEnd('/')}/{connection.EntitySet}{query}";
    }

    private void Authorize(HttpRequestMessage request, string code)
    {
        var section = configuration.GetSection($"{Section}:{code}");
        if (section["BearerToken"] is { Length: > 0 } token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        else if (section["Username"] is { Length: > 0 } user)
        {
            var pair = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{section["Password"]}"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", pair);
        }
    }

    private static string Detail(string body) =>
        string.IsNullOrWhiteSpace(body) ? "" : ": " + (body.Length <= 300 ? body : body[..300] + "…");
}
