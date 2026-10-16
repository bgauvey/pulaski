using System.Net;
using System.Text.Json;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Contracts.Scheduling;
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

    /// <summary>Work orders with their projected jobs and slip, for the equipment at and below <paramref name="equipmentId"/>.</summary>
    public Task<IReadOnlyList<WorkOrderProgressDto>> ListWorkOrderProgressAsync(Guid? equipmentId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkOrderProgressDto>>(
            equipmentId is null ? "api/work-orders/progress" : $"api/work-orders/progress?equipmentId={equipmentId}", ct);

    public Task<CreatedResponse> CreateWorkOrderAsync(CreateWorkOrderRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/work-orders", request, ct);

    public Task<VersionResponse> ChangeWorkOrderStatusAsync(
        Guid id,
        ChangeWorkOrderStatusRequest request,
        CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/work-orders/{id}/status", request, ct);

    public Task<DispatchWorkOrderResponse> DispatchWorkOrderAsync(
        Guid id,
        WorkOrderPlanningRequest request,
        CancellationToken ct = default) =>
        SendAsync<DispatchWorkOrderResponse>(HttpMethod.Post, $"api/work-orders/{id}/dispatch", request, ct);

    public Task<VersionResponse> UndispatchWorkOrderAsync(
        Guid id,
        WorkOrderPlanningRequest request,
        CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/work-orders/{id}/undispatch", request, ct);

    public Task<VersionResponse> FirmWorkOrderAsync(
        Guid id,
        WorkOrderPlanningRequest request,
        CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/work-orders/{id}/firm", request, ct);

    /// <summary>
    /// Moves a work order to <paramref name="to"/>. Dispatch, un-dispatch and firm carry the order's
    /// jobs, so they have their own endpoints (D-018); everything else is a plain status change.
    /// </summary>
    public async Task MoveWorkOrderAsync(WorkOrderDto workOrder, string to, CancellationToken ct = default)
    {
        var planning = new WorkOrderPlanningRequest { Version = workOrder.Version };
        switch ((workOrder.Status, to))
        {
            case ("planned", "dispatched"):
                await DispatchWorkOrderAsync(workOrder.Id, planning, ct);
                break;
            case ("dispatched", "planned"):
                await UndispatchWorkOrderAsync(workOrder.Id, planning, ct);
                break;
            case ("dispatched", "firmed"):
                await FirmWorkOrderAsync(workOrder.Id, planning, ct);
                break;
            default:
                await ChangeWorkOrderStatusAsync(
                    workOrder.Id, new ChangeWorkOrderStatusRequest { Version = workOrder.Version, Status = to }, ct);
                break;
        }
    }

    // Scheduling

    public Task<LineTimelineDto> GetLineTimelineAsync(Guid lineId, int? horizonDays = null, CancellationToken ct = default) =>
        GetAsync<LineTimelineDto>(
            horizonDays is null
                ? $"api/scheduling/lines/{lineId}/timeline"
                : $"api/scheduling/lines/{lineId}/timeline?horizonDays={horizonDays}",
            ct);

    /// <summary>Where a new job for the order would run before <paramref name="beforeJobId"/> (null: at the end); saves nothing.</summary>
    public Task<JobPlacementPreviewDto> PreviewPlacementAsync(
        Guid lineId,
        Guid workOrderId,
        decimal plannedQty,
        Guid? beforeJobId,
        string? jobNumber,
        CancellationToken ct = default)
    {
        var query = $"workOrderId={workOrderId}&plannedQty={plannedQty.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        if (beforeJobId is { } before)
        {
            query += $"&beforeJobId={before}";
        }

        if (!string.IsNullOrEmpty(jobNumber))
        {
            query += $"&jobNumber={Uri.EscapeDataString(jobNumber)}";
        }

        return GetAsync<JobPlacementPreviewDto>($"api/scheduling/lines/{lineId}/placement-preview?{query}", ct);
    }

    public Task<CreatedResponse> CreateJobAsync(CreateJobRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/scheduling/jobs", request, ct);

    public Task<VersionResponse> SequenceJobAsync(Guid id, SequenceJobRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/jobs/{id}/sequence", request, ct);

    public Task<VersionResponse> PinJobAsync(Guid id, PinJobRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/jobs/{id}/pin", request, ct);

    /// <summary>Moves a job planned to a group onto one of its lines, between them, or back to the group (D-022).</summary>
    public Task<VersionResponse> AssignJobAsync(Guid id, AssignJobRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/jobs/{id}/assign", request, ct);

    public Task<CancelJobResponse> CancelJobAsync(Guid id, CancelJobRequest request, CancellationToken ct = default) =>
        SendAsync<CancelJobResponse>(HttpMethod.Post, $"api/scheduling/jobs/{id}/cancel", request, ct);

    // Processes (D-023)

    /// <summary>Processes in the site (all when null) whose scope covers the material (any when null).</summary>
    public Task<IReadOnlyList<ProcessSummaryDto>> ListProcessesAsync(Guid? siteId = null, Guid? materialId = null, CancellationToken ct = default)
    {
        var filters = new List<string>();
        if (siteId is { } site)
        {
            filters.Add($"siteId={site}");
        }

        if (materialId is { } material)
        {
            filters.Add($"materialId={material}");
        }

        return GetAsync<IReadOnlyList<ProcessSummaryDto>>(
            filters.Count == 0 ? "api/processes" : $"api/processes?{string.Join('&', filters)}", ct);
    }

    /// <summary>Whether an order for the material can be made from the process, as creating it would check (D-024).</summary>
    public Task<ProcessOrderCheckDto> CheckProcessForOrderAsync(Guid processId, Guid materialId, CancellationToken ct = default) =>
        GetAsync<ProcessOrderCheckDto>($"api/processes/{processId}/order-check?materialId={materialId}", ct);

    public Task<ProcessDto> GetProcessAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<ProcessDto>($"api/processes/{id}", ct);

    public Task<CreatedResponse> CreateProcessAsync(CreateProcessRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/processes", request, ct);

    public Task<VersionResponse> UpdateProcessAsync(Guid id, UpdateProcessRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/processes/{id}", request, ct);

    public Task<ProcessVersionDto> GetProcessVersionAsync(Guid id, Guid versionId, CancellationToken ct = default) =>
        GetAsync<ProcessVersionDto>($"api/processes/{id}/versions/{versionId}", ct);

    public Task<SaveProcessDraftResponse> SaveProcessDraftAsync(Guid id, Guid versionId, SaveProcessDraftRequest request, CancellationToken ct = default) =>
        SendAsync<SaveProcessDraftResponse>(HttpMethod.Put, $"api/processes/{id}/versions/{versionId}", request, ct);

    public Task<VersionResponse> ReleaseProcessVersionAsync(Guid id, Guid versionId, ReleaseProcessVersionRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/processes/{id}/versions/{versionId}/release", request, ct);

    public Task<CreatedResponse> StartProcessDraftAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/processes/{id}/drafts", new { }, ct);

    public Task<VersionResponse> SetProcessScopeAsync(Guid id, SetProcessScopeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/processes/{id}/scope", request, ct);

    public Task<VersionResponse> SetDefaultProcessAsync(Guid materialId, Guid? processId, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/materials/{materialId}/default-process", new SetDefaultProcessRequest { ProcessId = processId }, ct);

    // Reference data

    public Task<IReadOnlyList<UomDto>> ListUomsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<UomDto>>("api/uoms", ct);

    public Task<IReadOnlyList<MaterialClassDto>> ListMaterialClassesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialClassDto>>("api/material-classes", ct);

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
