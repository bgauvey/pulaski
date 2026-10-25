using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Pulaski.Application.Materials;

namespace Pulaski.Api.Integrations;

/// <summary>
/// Fetches a material connection's items from its OData v4 service (D-068): <c>GET {root}/{entity set}?$select=…&amp;$filter=…</c>, following
/// <c>@odata.nextLink</c> page by page. Credentials come from <c>MaterialConnections:&lt;code&gt;</c> in configuration: <c>BearerToken</c>, or
/// <c>Username</c> and <c>Password</c> for basic authentication. Problems are returned, never thrown, so the screen can show them.
/// </summary>
public sealed class ODataMaterialFetcher(IHttpClientFactory clients, IConfiguration configuration) : IMaterialConnectionFetcher
{
    public const string ClientName = "material-connections";
    public const string Section = "MaterialConnections";
    private const int MaxPages = 200;

    public bool HasCredentials(string code)
    {
        var section = configuration.GetSection($"{Section}:{code}");
        return !string.IsNullOrEmpty(section["BearerToken"]) || !string.IsNullOrEmpty(section["Username"]);
    }

    public async Task<MaterialConnectionFetch> FetchAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken)
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

        return $"{connection.BaseUrl.TrimEnd('/')}/{connection.EntitySet}{query}";
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
