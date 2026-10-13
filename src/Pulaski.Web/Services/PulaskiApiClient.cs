using System.Net;
using System.Text.Json;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Web.Services;

/// <summary>Typed client for the Pulaski API. The UI never talks to the database directly.</summary>
public sealed class PulaskiApiClient(HttpClient http)
{
    // Equipment

    public Task<IReadOnlyList<EquipmentDto>> ListEquipmentAsync(Guid? siteId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<EquipmentDto>>(siteId is null ? "api/equipment" : $"api/equipment?siteId={siteId}", ct);

    public Task<EquipmentDto> GetEquipmentAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<EquipmentDto>($"api/equipment/{id}", ct);

    public Task<IReadOnlyList<EquipmentTypeDto>> ListEquipmentTypesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<EquipmentTypeDto>>("api/equipment-types", ct);

    public Task<CreatedResponse> CreateEquipmentAsync(CreateEquipmentRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/equipment", request, ct);

    public Task<VersionResponse> UpdateEquipmentAsync(Guid id, UpdateEquipmentRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment/{id}", request, ct);

    // Work orders

    public Task<IReadOnlyList<WorkOrderDto>> ListWorkOrdersAsync(string? status = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkOrderDto>>(
            string.IsNullOrEmpty(status) ? "api/work-orders" : $"api/work-orders?status={Uri.EscapeDataString(status)}", ct);

    public Task<CreatedResponse> CreateWorkOrderAsync(CreateWorkOrderRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/work-orders", request, ct);

    public Task<VersionResponse> ChangeWorkOrderStatusAsync(
        Guid id,
        ChangeWorkOrderStatusRequest request,
        CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/work-orders/{id}/status", request, ct);

    // Reference data

    public Task<IReadOnlyList<UomDto>> ListUomsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<UomDto>>("api/uoms", ct);

    public Task<IReadOnlyList<MaterialDto>> ListMaterialsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialDto>>("api/materials", ct);

    private async Task<T> GetAsync<T>(string uri, CancellationToken ct)
    {
        using var response = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Get, uri), ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string uri, object body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, uri) { Content = JsonContent.Create(body) };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                return await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(0, "The Pulaski API is not reachable.", ex.Message, null);
            }
        }
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiException.FromResponseAsync(response, ct);
        }

        return await response.Content.ReadFromJsonAsync<T>(ct)
            ?? throw new ApiException((int)response.StatusCode, "Empty response from the API.", null, null);
    }
}

/// <summary>An RFC 9457 problem returned by the API, or a transport failure (status 0).</summary>
public sealed class ApiException(
    int status,
    string title,
    string? detail,
    IReadOnlyDictionary<string, string[]>? errors) : Exception(detail ?? title)
{
    public int Status { get; } = status;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors ?? new Dictionary<string, string[]>();

    public bool IsConflict => Status == (int)HttpStatusCode.Conflict;

    internal static async Task<ApiException> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(ct);
            if (problem is not null)
            {
                return new ApiException(status, problem.Title ?? response.ReasonPhrase ?? "Error", problem.Detail, problem.Errors);
            }
        }
        catch (JsonException)
        {
            // Not a problem document; fall through.
        }

        return new ApiException(status, response.ReasonPhrase ?? "Error", null, null);
    }

    private sealed record Problem(string? Title, string? Detail, Dictionary<string, string[]>? Errors);
}
