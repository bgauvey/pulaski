using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Contracts.Scheduling;
using Pulaski.Contracts.Security;
using Pulaski.Contracts.Shifts;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Services;

/// <summary>
/// Typed client for the Pulaski API. The UI never talks to the database directly. Every call carries the
/// signed-in user's token (D-037).
/// </summary>
public sealed class PulaskiApiClient(HttpClient http, ApiSession session)
{
    // Security (D-037)

    /// <summary>Checks a user name and password; the only call made without signing in.</summary>
    public async Task<SignInResponse> SignInAsync(SignInRequest request, CancellationToken ct = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "api/auth/sign-in") { Content = JsonContent.Create(request) };
        using var response = await SendCoreAsync(message, ct, signedIn: false);
        return await ReadAsync<SignInResponse>(response, ct);
    }

    public Task<VersionResponse> ChangePasswordAsync(ChangePasswordRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, "api/auth/password", request, ct);

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => GetAsync<MeDto>("api/me", ct);

    public Task<IReadOnlyList<RoleDto>> ListRolesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<RoleDto>>("api/roles", ct);

    public Task<IReadOnlyList<PermissionDto>> ListPermissionsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PermissionDto>>("api/permissions", ct);

    public Task<CreatedResponse> CreateRoleAsync(CreateRoleRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/roles", request, ct);

    public Task<VersionResponse> UpdateRoleAsync(string code, UpdateRoleRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/roles/{Uri.EscapeDataString(code)}", request, ct);

    public Task<VersionResponse> SetRolePermissionAsync(string code, string permission, bool granted, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(
            HttpMethod.Put, $"api/roles/{Uri.EscapeDataString(code)}/permissions/{Uri.EscapeDataString(permission)}", new SetRolePermissionRequest { Granted = granted }, ct);

    public Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<UserDto>>("api/users", ct);

    public Task<CreatedResponse> CreateUserAsync(CreateUserRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/users", request, ct);

    public Task<VersionResponse> UpdateUserAsync(Guid id, UpdateUserRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/users/{id}", request, ct);

    public Task<VersionResponse> ResetPasswordAsync(Guid id, ResetPasswordRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/users/{id}/password", request, ct);

    public Task<CreatedResponse> AssignRoleAsync(Guid userId, AssignRoleRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/users/{userId}/roles", request, ct);

    public Task<VersionResponse> EndRoleAsync(Guid assignmentId, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/users/roles/{assignmentId}/end", new { }, ct);

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

    // Labor (D-040)

    public Task<IReadOnlyList<CrewDto>> ListCrewsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CrewDto>>($"api/crews?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateCrewAsync(CreateCrewRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/crews", request, ct);

    public Task<VersionResponse> UpdateCrewAsync(Guid id, UpdateCrewRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/crews/{id}", request, ct);

    public Task<IReadOnlyList<PersonDto>> ListPeopleAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PersonDto>>($"api/people?siteId={siteId}", ct);

    public Task<CreatedResponse> CreatePersonAsync(CreatePersonRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/people", request, ct);

    public Task<VersionResponse> UpdatePersonAsync(Guid id, UpdatePersonRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/people/{id}", request, ct);

    /// <summary>The formats people can be imported from (D-054).</summary>
    public Task<IReadOnlyList<PeopleSourceDto>> ListPeopleSourcesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PeopleSourceDto>>("api/people/import/sources", ct);

    /// <summary>Checks (<paramref name="dryRun"/>) or imports a people file into a site, read by the named source adapter (D-054).</summary>
    public async Task<ImportPeopleResponse> ImportPeopleAsync(
        Guid siteId, string source, string text, string? externalSystem, bool dryRun, string mediaType, CancellationToken ct = default)
    {
        var query = $"siteId={siteId}&dryRun={(dryRun ? "true" : "false")}"
            + (string.IsNullOrWhiteSpace(externalSystem) ? "" : $"&externalSystem={Uri.EscapeDataString(externalSystem.Trim())}");
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/people/import/{Uri.EscapeDataString(source)}?{query}")
        {
            Content = new StringContent(text, System.Text.Encoding.UTF8, mediaType),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportPeopleResponse>(response, ct);
    }

    public Task<MyLaborDto> GetMyLaborAsync(CancellationToken ct = default) => GetAsync<MyLaborDto>("api/labor/me", ct);

    public Task<LaborResponse> ClockMeOnAsync(ClockOnRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, "api/labor/me/sessions", request, ct);

    public Task<LaborResponse> ClockMeOffAsync(Guid sessionId, ClockOffRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/me/sessions/{sessionId}/end", request, ct);

    public Task<LaborResponse> ClockOnAsync(ClockOnRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, "api/labor/sessions", request, ct);

    public Task<LaborResponse> ClockOffAsync(Guid sessionId, ClockOffRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/sessions/{sessionId}/end", request, ct);

    public Task<LaborResponse> UpdateLaborSessionAsync(Guid sessionId, UpdateLaborSessionRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Put, $"api/labor/sessions/{sessionId}", request, ct);

    public Task<LaborResponse> CorrectLaborSessionAsync(Guid sessionId, CorrectLaborSessionRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/sessions/{sessionId}/correct", request, ct);

    public Task<LaborResponse> CheckInAsync(CheckInRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, "api/labor/check-in", request, ct);

    public Task<LaborResponse> CheckOutAsync(CheckOutRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, "api/labor/check-out", request, ct);

    public Task<IReadOnlyList<LineLaborDto>> ListLineLaborAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LineLaborDto>>($"api/labor/sites/{siteId}/lines", ct);

    public Task<IReadOnlyList<LaborShiftDto>> ListLaborShiftsAsync(Guid siteId, DateOnly date, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LaborShiftDto>>($"api/labor/sites/{siteId}/shifts?date={date:yyyy-MM-dd}", ct);

    public Task<ShiftLaborDto> GetShiftLaborAsync(Guid siteId, DateOnly date, Guid? scheduleId, string? shiftCode, CancellationToken ct = default) =>
        GetAsync<ShiftLaborDto>(
            $"api/labor/sites/{siteId}/shift?date={date:yyyy-MM-dd}" + (scheduleId is { } s ? $"&scheduleId={s}&shiftCode={Uri.EscapeDataString(shiftCode ?? "")}" : ""), ct);

    public Task<LaborResponse> ApproveShiftAsync(Guid siteId, ApproveShiftRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/sites/{siteId}/approvals", request, ct);

    // Breaks (D-047)

    public Task<LaborResponse> StartMyBreakAsync(StartBreakRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, "api/labor/me/breaks", request, ct);

    public Task<LaborResponse> EndMyBreakAsync(Guid breakId, EndBreakRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/me/breaks/{breakId}/end", request, ct);

    public Task<LaborResponse> StartBreakAsync(StartBreakRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, "api/labor/breaks", request, ct);

    public Task<LaborResponse> EndBreakAsync(Guid breakId, EndBreakRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/breaks/{breakId}/end", request, ct);

    public Task<LaborResponse> UpdateBreakAsync(Guid breakId, UpdateBreakRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Put, $"api/labor/breaks/{breakId}", request, ct);

    public Task<LaborResponse> CorrectBreakAsync(Guid breakId, CorrectBreakRequest request, CancellationToken ct = default) =>
        SendAsync<LaborResponse>(HttpMethod.Post, $"api/labor/breaks/{breakId}/correct", request, ct);

    // Qualifications (D-048)

    public Task<IReadOnlyList<QualificationDto>> ListQualificationsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<QualificationDto>>($"api/qualifications?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateQualificationAsync(CreateQualificationRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/qualifications", request, ct);

    public Task<VersionResponse> UpdateQualificationAsync(Guid id, UpdateQualificationRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/qualifications/{id}", request, ct);

    public Task<IReadOnlyList<QualificationGrantDto>> ListQualificationGrantsAsync(Guid siteId, Guid? personId = null, Guid? qualificationId = null, int? expiringWithinDays = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<QualificationGrantDto>>(
            $"api/qualifications/grants?siteId={siteId}" + (personId is { } p ? $"&personId={p}" : "") + (qualificationId is { } q ? $"&qualificationId={q}" : "")
            + (expiringWithinDays is { } d ? $"&expiringWithinDays={d}" : ""), ct);

    public Task<CreatedResponse> GrantQualificationAsync(GrantQualificationRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/qualifications/grants", request, ct);

    public Task<VersionResponse> UpdateQualificationGrantAsync(Guid grantId, UpdateQualificationGrantRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/qualifications/grants/{grantId}", request, ct);

    public Task<VersionResponse> RevokeQualificationGrantAsync(Guid grantId, RevokeQualificationGrantRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/qualifications/grants/{grantId}/revoke", request, ct);

    public Task<EquipmentQualificationsDto> GetEquipmentQualificationsAsync(Guid equipmentId, CancellationToken ct = default) =>
        GetAsync<EquipmentQualificationsDto>($"api/equipment/{equipmentId}/qualifications", ct);

    public Task<VersionResponse> SetEquipmentQualificationAsync(Guid equipmentId, SetEquipmentQualificationRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment/{equipmentId}/qualifications", request, ct);

    public Task<LaborReportDto> GetLaborReportAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetAsync<LaborReportDto>($"api/labor/sites/{siteId}/report?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    // Shift schedules (D-038)

    public Task<IReadOnlyList<ShiftScheduleDto>> ListShiftSchedulesAsync(Guid? siteId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ShiftScheduleDto>>(siteId is null ? "api/shift-schedules" : $"api/shift-schedules?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateShiftScheduleAsync(CreateShiftScheduleRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/shift-schedules", request, ct);

    public Task<VersionResponse> UpdateShiftScheduleAsync(Guid id, UpdateShiftScheduleRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/shift-schedules/{id}", request, ct);

    public Task<VersionResponse> SetShiftOverrideAsync(Guid scheduleId, SetShiftOverrideRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/shift-schedules/{scheduleId}/overrides", request, ct);

    public async Task<VersionResponse> RemoveShiftOverrideAsync(Guid scheduleId, DateOnly date, string code, long version, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(new HttpRequestMessage(
            HttpMethod.Delete, $"api/shift-schedules/{scheduleId}/overrides/{date:yyyy-MM-dd}/{Uri.EscapeDataString(code)}?version={version}"), ct);
        return await ReadAsync<VersionResponse>(response, ct);
    }

    public Task<ShiftPreviewDto> PreviewShiftScheduleAsync(PreviewShiftScheduleRequest request, CancellationToken ct = default) =>
        SendAsync<ShiftPreviewDto>(HttpMethod.Post, "api/shift-schedules/preview", request, ct);

    public Task<VersionResponse> SetEquipmentCalendarAsync(Guid equipmentId, SetEquipmentCalendarRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment/{equipmentId}/calendar", request, ct);

    // Machine data (D-050)

    public Task<VersionResponse> SetEquipmentMachineDataAsync(Guid equipmentId, SetEquipmentMachineDataRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment/{equipmentId}/machine-data", request, ct);

    public Task<VersionResponse> SetStopReasonOverrideAsync(Guid equipmentId, int rawValue, SetStopReasonOverrideRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment/{equipmentId}/stop-reason-overrides/{rawValue}", request, ct);

    public Task<IReadOnlyList<MachineProfileDto>> ListMachineProfilesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MachineProfileDto>>($"api/machine-profiles?siteId={siteId}", ct);

    public Task<MachineProfileDetailDto> GetMachineProfileAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<MachineProfileDetailDto>($"api/machine-profiles/{id}", ct);

    public Task<CreatedResponse> CreateMachineProfileAsync(CreateMachineProfileRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/machine-profiles", request, ct);

    public Task<VersionResponse> UpdateMachineProfileAsync(Guid id, UpdateMachineProfileRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/machine-profiles/{id}", request, ct);

    public Task<VersionResponse> SetProfileStateAsync(Guid id, int rawValue, SetProfileStateRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/machine-profiles/{id}/states/{rawValue}", request, ct);

    public Task<VersionResponse> SetProfileCodeAsync(Guid id, string kind, int rawValue, SetProfileCodeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/machine-profiles/{id}/codes/{Uri.EscapeDataString(kind)}/{rawValue}", request, ct);

    public Task<VersionResponse> SetProfileReasonsAsync(Guid id, SetProfileReasonsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/machine-profiles/{id}/reasons", request, ct);

    // Machines: status, stops and OEE (D-050, D-052)

    public Task<IReadOnlyList<MachineStatusDto>> GetMachineStatusAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MachineStatusDto>>($"api/machines/status?siteId={siteId}", ct);

    public Task<IReadOnlyList<DowntimeDto>> ListStopsAsync(Guid siteId, DateTimeOffset from, DateTimeOffset to, Guid? equipmentId, bool uncodedOnly, bool includeMicroStops, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DowntimeDto>>(Query("api/machines/stops", ("siteId", siteId), ("from", from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("to", to.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("equipmentId", equipmentId),
            ("uncodedOnly", uncodedOnly), ("includeMicroStops", includeMicroStops)), ct);

    public Task<OeeReportDto> GetOeeReportAsync(Guid siteId, DateOnly from, DateOnly to, Guid? equipmentId = null, CancellationToken ct = default) =>
        GetAsync<OeeReportDto>(Query("api/machines/oee", ("siteId", siteId),
            ("from", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ("to", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ("equipmentId", equipmentId)), ct);

    public Task<DowntimeCodedResponse> CodeStopAsync(Guid id, CodeDowntimeRequest request, CancellationToken ct = default) =>
        SendAsync<DowntimeCodedResponse>(HttpMethod.Post, $"api/machines/stops/{id}/code", request, ct);

    public Task<IReadOnlyList<ReasonCodeDto>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ReasonCodeDto>>($"api/equipment/{equipmentId}/coding-reasons", ct);

    // Edge nodes, connections and tags (D-050)

    public Task<IReadOnlyList<EdgeNodeDto>> ListEdgeNodesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<EdgeNodeDto>>($"api/edge-nodes?siteId={siteId}", ct);

    public Task<EdgeNodeDetailDto> GetEdgeNodeAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<EdgeNodeDetailDto>($"api/edge-nodes/{id}", ct);

    public Task<CreatedResponse> CreateEdgeNodeAsync(CreateEdgeNodeRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/edge-nodes", request, ct);

    public Task<VersionResponse> UpdateEdgeNodeAsync(Guid id, UpdateEdgeNodeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/edge-nodes/{id}", request, ct);

    public Task DeleteEdgeNodeAsync(Guid id, CancellationToken ct = default) =>
        DeleteAsync($"api/edge-nodes/{id}", ct);

    public Task<CreatedResponse> CreateEdgeConnectionAsync(Guid nodeId, CreateEdgeConnectionRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/edge-nodes/{nodeId}/connections", request, ct);

    public Task<VersionResponse> UpdateEdgeConnectionAsync(Guid id, UpdateEdgeConnectionRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/edge-connections/{id}", request, ct);

    public Task<CreatedResponse> CreateEquipmentTagAsync(CreateEquipmentTagRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/equipment-tags", request, ct);

    public Task<VersionResponse> UpdateEquipmentTagAsync(Guid id, UpdateEquipmentTagRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment-tags/{id}", request, ct);

    public Task DeleteEquipmentTagAsync(Guid id, CancellationToken ct = default) =>
        DeleteAsync($"api/equipment-tags/{id}", ct);

    /// <summary>Checks (<paramref name="dryRun"/>) or imports a machine's definitions from CSV into a profile, all or nothing (D-050).</summary>
    public async Task<ProfileImportResponse> ImportMachineProfileCsvAsync(Guid id, long version, string csv, bool dryRun, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/machine-profiles/{id}/import/csv?version={version}&dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ProfileImportResponse>(response, ct);
    }

    // Work orders

    public Task<IReadOnlyList<WorkOrderDto>> ListWorkOrdersAsync(string? status = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkOrderDto>>(
            string.IsNullOrEmpty(status) ? "api/work-orders" : $"api/work-orders?status={Uri.EscapeDataString(status)}", ct);

    /// <summary>Work orders with their projected jobs and slip, for the equipment at and below <paramref name="equipmentId"/>.</summary>
    public Task<IReadOnlyList<WorkOrderProgressDto>> ListWorkOrderProgressAsync(Guid? equipmentId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<WorkOrderProgressDto>>(
            equipmentId is null ? "api/work-orders/progress" : $"api/work-orders/progress?equipmentId={equipmentId}", ct);

    public async Task<(string FileName, byte[] Content)> GetShopOrderPdfAsync(Guid id, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Get, $"api/work-orders/{id}/shop-order.pdf"), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiException.FromResponseAsync(response, ct);
        }

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName
            ?? "shop-order.pdf";
        return (fileName.Trim('"'), await response.Content.ReadAsByteArrayAsync(ct));
    }

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

    public Task<ReleaseProcessVersionResponse> ReleaseProcessVersionAsync(Guid id, Guid versionId, ReleaseProcessVersionRequest request, CancellationToken ct = default) =>
        SendAsync<ReleaseProcessVersionResponse>(HttpMethod.Post, $"api/processes/{id}/versions/{versionId}/release", request, ct);

    public Task<CreatedResponse> StartProcessDraftAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/processes/{id}/drafts", new { }, ct);

    public Task<VersionResponse> SetProcessScopeAsync(Guid id, SetProcessScopeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/processes/{id}/scope", request, ct);

    // Units and material conversions (D-026)

    public Task<CreatedResponse> CreateUomAsync(CreateUomRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/uoms", request, ct);

    public Task<VersionResponse> UpdateUomAsync(Guid id, UpdateUomRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/uoms/{id}", request, ct);

    public Task<IReadOnlyList<MaterialConversionDto>> ListMaterialConversionsAsync(Guid materialId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialConversionDto>>($"api/materials/{materialId}/conversions", ct);

    public Task<MaterialConversionDto> SetMaterialConversionAsync(Guid materialId, SetMaterialConversionRequest request, CancellationToken ct = default) =>
        SendAsync<MaterialConversionDto>(HttpMethod.Put, $"api/materials/{materialId}/conversions", request, ct);

    /// <summary>Switches the conversion off (the API answers 204 with no body).</summary>
    public async Task RemoveMaterialConversionAsync(Guid materialId, Guid fromUomId, Guid toUomId, CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(
            new HttpRequestMessage(HttpMethod.Delete, $"api/materials/{materialId}/conversions?fromUomId={fromUomId}&toUomId={toUomId}"), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiException.FromResponseAsync(response, ct);
        }
    }

    public Task<UnitConversionResultDto> ConvertAsync(decimal quantity, Guid fromUomId, Guid toUomId, Guid? materialId, CancellationToken ct = default) =>
        GetAsync<UnitConversionResultDto>(
            FormattableString.Invariant($"api/uoms/convert?quantity={quantity}&fromUomId={fromUomId}&toUomId={toUomId}")
            + (materialId is { } m ? $"&materialId={m}" : ""),
            ct);

    // Bills of materials (D-025)

    public Task<IReadOnlyList<BomSummaryDto>> ListBomsAsync(Guid? materialId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<BomSummaryDto>>(materialId is null ? "api/boms" : $"api/boms?materialId={materialId}", ct);

    public Task<BomDto> GetBomAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<BomDto>($"api/boms/{id}", ct);

    public Task<CreatedResponse> CreateBomAsync(CreateBomRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/boms", request, ct);

    public Task<BomVersionDto> GetBomVersionAsync(Guid id, Guid versionId, CancellationToken ct = default) =>
        GetAsync<BomVersionDto>($"api/boms/{id}/versions/{versionId}", ct);

    public Task<SaveBomDraftResponse> SaveBomDraftAsync(Guid id, Guid versionId, SaveBomDraftRequest request, CancellationToken ct = default) =>
        SendAsync<SaveBomDraftResponse>(HttpMethod.Put, $"api/boms/{id}/versions/{versionId}", request, ct);

    public Task<VersionResponse> ReleaseBomVersionAsync(Guid id, Guid versionId, ReleaseBomVersionRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/boms/{id}/versions/{versionId}/release", request, ct);

    public Task<CreatedResponse> StartBomDraftAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/boms/{id}/drafts", new { }, ct);

    public Task<IReadOnlyList<PlannedComponentDto>> ListWorkOrderComponentsAsync(Guid workOrderId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PlannedComponentDto>>($"api/work-orders/{workOrderId}/components", ct);

    public Task<VersionResponse> SetDefaultProcessAsync(Guid materialId, Guid? processId, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/materials/{materialId}/default-process", new SetDefaultProcessRequest { ProcessId = processId }, ct);

    // Inventory (D-027)

    public Task<IReadOnlyList<MaterialStockDto>> ListStockAsync(
        Guid? siteId, string? search = null, Guid? materialClassId = null, Guid? locationId = null, bool inStockOnly = false, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialStockDto>>(
            Query("api/inventory/stock", ("siteId", siteId), ("search", search), ("materialClassId", materialClassId), ("locationId", locationId), ("inStockOnly", inStockOnly ? "true" : null)), ct);

    public Task<MaterialStockDetailDto> GetMaterialStockAsync(Guid materialId, Guid? siteId, CancellationToken ct = default) =>
        GetAsync<MaterialStockDetailDto>(Query($"api/inventory/materials/{materialId}", ("siteId", siteId)), ct);

    public Task<VersionResponse> SetShelfLifeAsync(Guid materialId, SetShelfLifeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/inventory/materials/{materialId}/shelf-life", request, ct);

    public Task<WhereUsedDto> GetWhereUsedAsync(Guid materialId, CancellationToken ct = default) =>
        GetAsync<WhereUsedDto>($"api/inventory/materials/{materialId}/where-used", ct);

    public Task<IReadOnlyList<InventoryLocationDto>> ListInventoryLocationsAsync(Guid? siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<InventoryLocationDto>>(Query("api/inventory/locations", ("siteId", siteId)), ct);

    public Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(Guid? siteId, string? search = null, Guid? materialId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DeclarableJobDto>>(Query("api/inventory/jobs", ("siteId", siteId), ("search", search), ("materialId", materialId)), ct);

    public Task<JobMaterialsDto> GetJobMaterialsAsync(Guid jobId, CancellationToken ct = default) =>
        GetAsync<JobMaterialsDto>($"api/jobs/{jobId}/materials", ct);

    public Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(
        Guid? siteId, string? search = null, Guid? materialId = null, Guid? locationId = null, string? status = null, DateTimeOffset? expiresBefore = null,
        int offset = 0, int limit = 500, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LotSummaryDto>>(
            Query("api/lots", ("siteId", siteId), ("search", search), ("materialId", materialId), ("locationId", locationId), ("status", status),
                ("expiresBefore", Iso(expiresBefore)), ("offset", offset == 0 ? null : offset), ("limit", limit == 500 ? null : limit)), ct);

    public Task<LotDto> GetLotAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<LotDto>($"api/lots/{id}", ct);

    public Task<IReadOnlyList<LotTraceNodeDto>> TraceLotAsync(Guid id, bool forward, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LotTraceNodeDto>>($"api/lots/{id}/trace?direction={(forward ? "forward" : "backward")}", ct);

    public Task<LotTransactionResponse> ReceiveLotAsync(ReceiveLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, "api/lots", request, ct);

    public Task<LotTransactionResponse> WriteUpLotAsync(Guid id, AdjustLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/write-up", request, ct);

    public Task<LotTransactionResponse> WriteOffLotAsync(Guid id, AdjustLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/write-off", request, ct);

    public Task<LotTransactionResponse> MoveLotAsync(Guid id, MoveLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/move", request, ct);

    public Task<LotTransactionResponse> ChangeLotStatusAsync(Guid id, ChangeLotStatusRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/status", request, ct);

    public Task<LotTransactionResponse> ConsumeLotAsync(Guid id, ConsumeLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/consume", request, ct);

    public Task<LotTransactionResponse> ProduceLotAsync(Guid jobId, ProduceLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/produce", request, ct);

    public Task<IReadOnlyList<ReasonCodeDto>> ListReasonCodesAsync(string kind, Guid? siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ReasonCodeDto>>(Query("api/reason-codes", ("kind", kind), ("siteId", siteId)), ct);

    public Task<ShortageReportDto> GetShortagesAsync(Guid siteId, Guid? equipmentId = null, int? horizonDays = null, CancellationToken ct = default) =>
        GetAsync<ShortageReportDto>(Query("api/inventory/shortages", ("siteId", siteId), ("equipmentId", equipmentId), ("horizonDays", horizonDays)), ct);

    /// <summary>A page of the site's movements, newest first; pass the last entry to continue (D-029).</summary>
    public Task<JournalPageDto> ListMovementsAsync(
        Guid siteId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        string? kind = null,
        Guid? materialId = null,
        Guid? locationId = null,
        Guid? jobId = null,
        string? actor = null,
        string? search = null,
        LotEventDto? after = null,
        CancellationToken ct = default) =>
        GetAsync<JournalPageDto>(
            Query("api/inventory/movements", ("siteId", siteId), ("from", Iso(from)), ("to", Iso(to)), ("kind", kind), ("materialId", materialId),
                ("locationId", locationId), ("jobId", jobId), ("actor", actor), ("search", search), ("beforeAt", Iso(after?.OccurredAt)), ("beforeId", after?.Id)),
            ct);

    // Lot operations, counts, reasons and reports (D-030)

    public Task<LotTransactionResponse> SplitLotAsync(Guid id, SplitLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/split", request, ct);

    public Task<LotTransactionResponse> MergeLotsAsync(Guid targetId, MergeLotsRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{targetId}/merge", request, ct);

    public Task<LotTransactionResponse> ShipLotAsync(Guid id, ShipLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/ship", request, ct);

    public Task<LotTransactionResponse> CorrectLotAsync(Guid id, CorrectLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/correct", request, ct);

    public Task<LotTransactionResponse> ReverseMovementAsync(Guid id, ReverseMovementRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{id}/reverse", request, ct);

    public Task<IReadOnlyList<StockCountSummaryDto>> ListStockCountsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<StockCountSummaryDto>>($"api/inventory/counts?siteId={siteId}", ct);

    public Task<StockCountDto> GetStockCountAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<StockCountDto>($"api/inventory/counts/{id}", ct);

    public Task<CreatedResponse> StartStockCountAsync(StartStockCountRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/inventory/counts", request, ct);

    public Task<VersionResponse> RecordCountsAsync(Guid id, RecordCountsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/inventory/counts/{id}/lines", request, ct);

    public Task<PostStockCountResponse> PostStockCountAsync(Guid id, PostStockCountRequest request, CancellationToken ct = default) =>
        SendAsync<PostStockCountResponse>(HttpMethod.Post, $"api/inventory/counts/{id}/post", request, ct);

    public Task<VersionResponse> CancelStockCountAsync(Guid id, CancelStockCountRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/counts/{id}/cancel", request, ct);

    public Task<IReadOnlyList<ReasonCodeAdminDto>> ListAllReasonCodesAsync(Guid? siteId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ReasonCodeAdminDto>>(Query("api/reason-codes/all", ("siteId", siteId)), ct);

    public Task<CreatedResponse> CreateReasonCodeAsync(CreateReasonCodeRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/reason-codes", request, ct);

    public Task<VersionResponse> UpdateReasonCodeAsync(Guid id, UpdateReasonCodeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/reason-codes/{id}", request, ct);

    public Task<IReadOnlyList<StockOnDateDto>> StockOnDateAsync(Guid siteId, DateTimeOffset at, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<StockOnDateDto>>(Query("api/inventory/reports/stock-on-date", ("siteId", siteId), ("at", Iso(at))), ct);

    public Task<IReadOnlyList<AgingRowDto>> AgingAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AgingRowDto>>($"api/inventory/reports/aging?siteId={siteId}", ct);

    private static string? Iso(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A path with the given query parameters, leaving out the null and blank ones.</summary>
    private static string Query(string path, params (string Name, object? Value)[] parameters)
    {
        var pairs = parameters
            .Select(p => (p.Name, Value: p.Value?.ToString()))
            .Where(p => !string.IsNullOrWhiteSpace(p.Value))
            .Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value!.Trim())}")
            .ToList();
        return pairs.Count == 0 ? path : $"{path}?{string.Join('&', pairs)}";
    }

    // Reference data

    public Task<IReadOnlyList<UomDto>> ListUomsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<UomDto>>("api/uoms", ct);

    public Task<IReadOnlyList<MaterialClassDto>> ListMaterialClassesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialClassDto>>("api/material-classes", ct);

    public Task<IReadOnlyList<MaterialDto>> ListMaterialsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialDto>>("api/materials", ct);

    public Task<IReadOnlyList<MaterialDto>> ListAllMaterialsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialDto>>("api/materials?includeInactive=true", ct);

    // Materials management (D-033)

    public Task<MaterialDetailDto> GetMaterialAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<MaterialDetailDto>($"api/materials/{id}", ct);

    public Task<CreatedResponse> CreateMaterialAsync(CreateMaterialRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/materials", request, ct);

    public Task<UpdateMaterialResponse> UpdateMaterialAsync(Guid id, UpdateMaterialRequest request, CancellationToken ct = default) =>
        SendAsync<UpdateMaterialResponse>(HttpMethod.Put, $"api/materials/{id}", request, ct);

    public Task<CreatedResponse> CreateMaterialClassAsync(CreateMaterialClassRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/material-classes", request, ct);

    public Task<VersionResponse> UpdateMaterialClassAsync(Guid id, UpdateMaterialClassRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/material-classes/{id}", request, ct);

    public Task<UpdateMaterialResponse> SetBatchingAsync(Guid materialId, SetBatchingRequest request, CancellationToken ct = default) =>
        SendAsync<UpdateMaterialResponse>(HttpMethod.Put, $"api/materials/{materialId}/batching", request, ct);

    public Task<IReadOnlyList<PartialBatchDto>> ListPendingPartialBatchesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<PartialBatchDto>>($"api/execution/sites/{siteId}/partial-batches", ct);

    public Task<PartialBatchDecisionResponse> ApprovePartialBatchAsync(Guid id, string? comment, CancellationToken ct = default) =>
        SendAsync<PartialBatchDecisionResponse>(HttpMethod.Post, $"api/partial-batches/{id}/approve", new DecidePartialBatchRequest { Comment = comment }, ct);

    public Task<PartialBatchDecisionResponse> RejectPartialBatchAsync(Guid id, Guid scrapReasonId, string? comment, CancellationToken ct = default) =>
        SendAsync<PartialBatchDecisionResponse>(HttpMethod.Post, $"api/partial-batches/{id}/reject", new DecidePartialBatchRequest { ScrapReasonCodeId = scrapReasonId, Comment = comment }, ct);

    public Task<CapabilityResponse> AddCapabilityAsync(Guid materialId, AddCapabilityRequest request, CancellationToken ct = default) =>
        SendAsync<CapabilityResponse>(HttpMethod.Post, $"api/materials/{materialId}/capabilities", request, ct);

    public Task<CapabilityResponse> UpdateCapabilityAsync(Guid id, UpdateCapabilityRequest request, CancellationToken ct = default) =>
        SendAsync<CapabilityResponse>(HttpMethod.Put, $"api/capabilities/{id}", request, ct);

    public Task<CapabilityResponse> EndCapabilityAsync(Guid id, long version, CancellationToken ct = default) =>
        SendAsync<CapabilityResponse>(HttpMethod.Post, $"api/capabilities/{id}/end", new EndCapabilityRequest { Version = version }, ct);

    public Task<VersionResponse> ModifyRunStandardAsync(Guid id, ModifyRunStandardRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/run-standards/{id}/modify", request, ct);

    public Task<VersionResponse> ClearRunStandardAsync(Guid id, ClearRunStandardRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/run-standards/{id}/clear", request, ct);

    /// <summary>The formats materials can be imported from (D-053).</summary>
    public Task<IReadOnlyList<MaterialSourceDto>> ListMaterialSourcesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialSourceDto>>("api/materials/import/sources", ct);

    /// <summary>
    /// Checks (<paramref name="dryRun"/>) or imports a materials file read by the source adapter named <paramref name="source"/>,
    /// all or nothing (D-034, D-053).
    /// </summary>
    public async Task<ImportMaterialsResponse> ImportMaterialsAsync(
        string source, string text, string? externalSystem, bool dryRun, string mediaType, CancellationToken ct = default)
    {
        var query = $"dryRun={(dryRun ? "true" : "false")}"
            + (string.IsNullOrWhiteSpace(externalSystem) ? "" : $"&externalSystem={Uri.EscapeDataString(externalSystem.Trim())}");
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/materials/import/{Uri.EscapeDataString(source)}?{query}")
        {
            Content = new StringContent(text, System.Text.Encoding.UTF8, mediaType),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportMaterialsResponse>(response, ct);
    }

    public Task<IReadOnlyList<LineBoardRowDto>> GetLineBoardAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LineBoardRowDto>>($"api/execution/sites/{siteId}/board", ct);

    public Task<JobExecutionDto> GetJobExecutionAsync(Guid jobId, CancellationToken ct = default) =>
        GetAsync<JobExecutionDto>($"api/jobs/{jobId}/execution", ct);

    public Task<ExecutionResponse> StartJobAsync(Guid jobId, StartJobRequest request, CancellationToken ct = default) =>
        SendAsync<ExecutionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/start", request, ct);

    public Task<ExecutionResponse> PauseJobAsync(Guid jobId, PauseJobRequest request, CancellationToken ct = default) =>
        SendAsync<ExecutionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/pause", request, ct);

    public Task<ExecutionResponse> CompleteJobAsync(Guid jobId, long version, CancellationToken ct = default) =>
        CompleteJobAsync(jobId, new CompleteJobRequest { Version = version }, ct);

    public Task<ExecutionResponse> CompleteJobAsync(Guid jobId, CompleteJobRequest request, CancellationToken ct = default) =>
        SendAsync<ExecutionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/complete", request, ct);

    public Task<ExecutionResponse> ReopenJobAsync(Guid jobId, long version, CancellationToken ct = default) =>
        SendAsync<ExecutionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/reopen", new JobVersionRequest { Version = version }, ct);

    public Task<ReportProductionResponse> ReportProductionAsync(Guid jobId, ReportProductionRequest request, CancellationToken ct = default) =>
        SendAsync<ReportProductionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/production", request, ct);

    // Planning rates from run history (D-045)

    public Task<RunHistoryRateDto> GetRunHistoryRateAsync(Guid runStandardId, int days, CancellationToken ct = default) =>
        GetAsync<RunHistoryRateDto>($"api/scheduling/run-standards/{runStandardId}/history-rate?days={days}", ct);

    public Task<VersionResponse> AdoptHistoryRateAsync(Guid runStandardId, AdoptHistoryRateRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/run-standards/{runStandardId}/history-rate", request, ct);

    public Task<VersionResponse> UseIdealRateAsync(Guid runStandardId, UseIdealRateRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/run-standards/{runStandardId}/ideal-rate", request, ct);

    // Site settings (D-043): changeover mode, reporting tolerances and calendar exceptions

    public Task<SchedulingPolicyDto> GetSchedulingPolicyAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<SchedulingPolicyDto>($"api/scheduling/sites/{siteId}/policy", ct);

    public Task<VersionResponse> SetSchedulingPolicyAsync(Guid siteId, SetSchedulingPolicyRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/scheduling/sites/{siteId}/policy", request, ct);

    public Task<IReadOnlyList<CalendarExceptionDto>> ListCalendarExceptionsAsync(Guid siteId, DateTimeOffset? from, bool includeRemoved, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CalendarExceptionDto>>(
            $"api/calendar-exceptions?siteId={siteId}&includeRemoved={(includeRemoved ? "true" : "false")}"
            + (from is { } f ? $"&from={Uri.EscapeDataString(f.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}" : ""), ct);

    public Task<CreatedResponse> CreateCalendarExceptionAsync(CreateCalendarExceptionRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/calendar-exceptions", request, ct);

    public Task<VersionResponse> UpdateCalendarExceptionAsync(Guid id, UpdateCalendarExceptionRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/calendar-exceptions/{id}", request, ct);

    public Task<ProductionByShiftDto> GetProductionByShiftAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct = default) =>
        GetAsync<ProductionByShiftDto>($"api/execution/sites/{siteId}/production-by-shift?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", ct);

    public Task<ExecutionPolicyDto> GetExecutionPolicyAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<ExecutionPolicyDto>($"api/execution/sites/{siteId}/policy", ct);

    public Task<ExecutionPolicyDto> SetExecutionPolicyAsync(Guid siteId, SetExecutionPolicyRequest request, CancellationToken ct = default) =>
        SendAsync<ExecutionPolicyDto>(HttpMethod.Put, $"api/execution/sites/{siteId}/policy", request, ct);

    // Terminals and badges (D-049): managing them as a signed-in user

    public Task<IReadOnlyList<TerminalDto>> ListTerminalsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TerminalDto>>($"api/terminals?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateTerminalAsync(CreateTerminalRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/terminals", request, ct);

    public Task<VersionResponse> UpdateTerminalAsync(Guid id, UpdateTerminalRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/terminals/{id}", request, ct);

    public Task<EnrolmentCodeResponse> IssueEnrolmentCodeAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<EnrolmentCodeResponse>(HttpMethod.Post, $"api/terminals/{id}/enrolment-code", new { }, ct);

    public Task<IReadOnlyList<BadgeDto>> ListBadgesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<BadgeDto>>($"api/badges?siteId={siteId}", ct);

    public Task<CreatedResponse> IssueBadgeAsync(IssueBadgeRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/badges", request, ct);

    public Task<VersionResponse> RevokeBadgeAsync(Guid badgeId, RevokeBadgeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/badges/{badgeId}/revoke", request, ct);

    // At a terminal (D-049): no user signs in; the terminal's secret, from its cookie, is its credential.

    public Task<TerminalEnrolledResponse> EnrolTerminalAsync(EnrolTerminalRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<TerminalEnrolledResponse>(null, HttpMethod.Post, "api/terminal/enrol", request, ct);

    public Task<TerminalStatusDto> GetTerminalStatusAsync(string secret, CancellationToken ct = default) =>
        TerminalSendAsync<TerminalStatusDto>(secret, HttpMethod.Get, "api/terminal", null, ct);

    public Task<BadgeIdentityResponse> IdentifyBadgeAsync(string secret, IdentifyBadgeRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<BadgeIdentityResponse>(secret, HttpMethod.Post, "api/terminal/identify", request, ct);

    /// <summary>One of <c>clock-on</c>, <c>clock-off</c>, <c>breaks/start</c> or <c>breaks/end</c> for the person the ticket names.</summary>
    public Task<LaborResponse> TerminalActAsync(string secret, string action, TerminalActionRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<LaborResponse>(secret, HttpMethod.Post, $"api/terminal/{action}", request, ct);

    private async Task<T> TerminalSendAsync<T>(string? secret, HttpMethod method, string uri, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, uri) { Content = body is null ? null : JsonContent.Create(body) };
        if (secret is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Terminal", secret);
        }

        using var response = await SendCoreAsync(request, ct, signedIn: false);
        return await ReadAsync<T>(response, ct);
    }

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

    private async Task DeleteAsync(string uri, CancellationToken ct)
    {
        using var response = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Delete, uri), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiException.FromResponseAsync(response, ct);
        }
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpRequestMessage request, CancellationToken ct, bool signedIn = true)
    {
        using (request)
        {
            if (signedIn && await session.TokenAsync() is { } token)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(0, "The Pulaski API is not reachable.", ex.Message, null);
            }

            if (signedIn && response.StatusCode == HttpStatusCode.Unauthorized)
            {
                session.SignInAgain();
            }

            return response;
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
