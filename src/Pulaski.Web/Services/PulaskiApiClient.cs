using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Pulaski.Web.Services.Localization;
using Pulaski.Contracts.Alerts;
using Pulaski.Contracts.Andon;
using Pulaski.Contracts.Boms;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Execution;
using Pulaski.Contracts.Integrations;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.Overview;
using Pulaski.Contracts.Processes;
using Pulaski.Contracts.Quality;
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
    /// <summary>How people may sign in (D-091); asked before anyone has.</summary>
    public async Task<SignInOptionsDto> GetSignInOptionsAsync(CancellationToken ct = default)
    {
        using var response = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Get, "api/auth/options"), ct, signedIn: false);
        return await ReadAsync<SignInOptionsDto>(response, ct);
    }

    public async Task<StartProviderSignInResponse> StartProviderSignInAsync(string provider, StartProviderSignInRequest request, CancellationToken ct = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"api/auth/providers/{Uri.EscapeDataString(provider)}/start") { Content = JsonContent.Create(request) };
        using var response = await SendCoreAsync(message, ct, signedIn: false);
        return await ReadAsync<StartProviderSignInResponse>(response, ct);
    }

    public async Task<SignInResponse> CompleteProviderSignInAsync(string provider, CompleteProviderSignInRequest request, CancellationToken ct = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"api/auth/providers/{Uri.EscapeDataString(provider)}/complete") { Content = JsonContent.Create(request) };
        using var response = await SendCoreAsync(message, ct, signedIn: false);
        return await ReadAsync<SignInResponse>(response, ct);
    }

    /// <summary>Asks for a password reset link (D-093); the answer is the same whether or not an account matched.</summary>
    public async Task<PasswordResetAnswer> RequestPasswordResetAsync(RequestPasswordResetRequest request, CancellationToken ct = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "api/auth/password-reset") { Content = JsonContent.Create(request) };
        using var response = await SendCoreAsync(message, ct, signedIn: false);
        return await ReadAsync<PasswordResetAnswer>(response, ct);
    }

    public async Task CompletePasswordResetAsync(CompletePasswordResetRequest request, CancellationToken ct = default)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, "api/auth/password-reset/complete") { Content = JsonContent.Create(request) };
        using var response = await SendCoreAsync(message, ct, signedIn: false);
        await ReadAsync<VersionResponse>(response, ct);
    }

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
        Guid siteId, string source, string text, string? externalSystem, bool dryRun, string mediaType, bool switchOffMissing = false, CancellationToken ct = default)
    {
        var query = $"siteId={siteId}&dryRun={(dryRun ? "true" : "false")}"
            + (string.IsNullOrWhiteSpace(externalSystem) ? "" : $"&externalSystem={Uri.EscapeDataString(externalSystem.Trim())}")
            + (switchOffMissing ? "&switchOffMissing=true" : "");
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

    public Task<OverviewDto> GetOverviewAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<OverviewDto>($"api/overview?siteId={siteId}", ct);

    public Task<PendingLaborApprovalsDto> ListPendingLaborApprovalsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<PendingLaborApprovalsDto>($"api/labor/sites/{siteId}/approvals/pending", ct);

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

    /// <summary>A line's report defaults (#122): its default output location and lot label printer.</summary>
    public Task<VersionResponse> SetLineOutputAsync(Guid equipmentId, SetLineOutputRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/equipment/{equipmentId}/output", request, ct);

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

    public Task<StopPageDto> ListStopsAsync(Guid siteId, DateTimeOffset from, DateTimeOffset to, Guid? equipmentId, bool uncodedOnly, bool includeMicroStops,
        bool includeSuperseded = false, string? after = null, CancellationToken ct = default) =>
        GetAsync<StopPageDto>(Query("api/machines/stops", ("siteId", siteId), ("from", from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("to", to.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("equipmentId", equipmentId),
            ("uncodedOnly", uncodedOnly), ("includeMicroStops", includeMicroStops), ("includeSuperseded", includeSuperseded), ("after", after)), ct);

    public Task<AlarmListDto> ListAlarmsAsync(Guid siteId, DateTimeOffset from, DateTimeOffset to, Guid? equipmentId, bool activeOnly, CancellationToken ct = default) =>
        GetAsync<AlarmListDto>(Query("api/machines/alarms", ("siteId", siteId), ("from", from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)),
            ("to", to.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("equipmentId", equipmentId), ("activeOnly", activeOnly)), ct);

    public Task<IReadOnlyList<ProcessValueSeriesDto>> GetProcessValuesAsync(Guid equipmentId, DateTimeOffset from, DateTimeOffset to, Guid? tagId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProcessValueSeriesDto>>(Query("api/machines/process-values", ("equipmentId", equipmentId),
            ("from", from.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("to", to.ToString("O", System.Globalization.CultureInfo.InvariantCulture)), ("tagId", tagId)), ct);

    public Task<OeeReportDto> GetOeeReportAsync(Guid siteId, DateOnly from, DateOnly to, Guid? equipmentId = null, string? shiftCode = null, CancellationToken ct = default) =>
        GetAsync<OeeReportDto>(Query("api/machines/oee", ("siteId", siteId), ("shiftCode", shiftCode),
            ("from", from.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ("to", to.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)),
            ("equipmentId", equipmentId)), ct);

    public Task<DowntimeCodedResponse> CodeStopAsync(Guid id, CodeDowntimeRequest request, CancellationToken ct = default) =>
        SendAsync<DowntimeCodedResponse>(HttpMethod.Post, $"api/machines/stops/{id}/code", request, ct);

    public Task<StopsAdjustedResponse> EnterStopAsync(EnterStopRequest request, CancellationToken ct = default) =>
        SendAsync<StopsAdjustedResponse>(HttpMethod.Post, "api/machines/stops", request, ct);

    public Task<StopsAdjustedResponse> SplitStopAsync(Guid id, SplitStopRequest request, CancellationToken ct = default) =>
        SendAsync<StopsAdjustedResponse>(HttpMethod.Post, $"api/machines/stops/{id}/split", request, ct);

    public Task<StopsAdjustedResponse> MergeStopsAsync(MergeStopsRequest request, CancellationToken ct = default) =>
        SendAsync<StopsAdjustedResponse>(HttpMethod.Post, "api/machines/stops/merge", request, ct);

    public Task<StopsAdjustedResponse> RetimeStopAsync(Guid id, RetimeStopRequest request, CancellationToken ct = default) =>
        SendAsync<StopsAdjustedResponse>(HttpMethod.Post, $"api/machines/stops/{id}/retime", request, ct);

    public Task<StopsAdjustedResponse> VoidStopAsync(Guid id, StopReasonedRequest request, CancellationToken ct = default) =>
        SendAsync<StopsAdjustedResponse>(HttpMethod.Post, $"api/machines/stops/{id}/void", request, ct);

    public Task<StopsAdjustedResponse> UndoStopAdjustmentAsync(Guid id, StopReasonedRequest request, CancellationToken ct = default) =>
        SendAsync<StopsAdjustedResponse>(HttpMethod.Post, $"api/machines/stops/{id}/undo", request, ct);

    public Task<IReadOnlyList<ReasonCodeDto>> ListCodingReasonsAsync(Guid equipmentId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ReasonCodeDto>>($"api/equipment/{equipmentId}/coding-reasons", ct);

    // Material connections (D-068)

    public Task<IReadOnlyList<MaterialConnectionDto>> ListMaterialConnectionsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialConnectionDto>>("api/material-connections", ct);

    public Task<CreatedResponse> SaveMaterialConnectionAsync(Guid? id, SaveMaterialConnectionRequest request, CancellationToken ct = default) =>
        id is { } existing
            ? SendAsync<CreatedResponse>(HttpMethod.Put, $"api/material-connections/{existing}", request, ct)
            : SendAsync<CreatedResponse>(HttpMethod.Post, "api/material-connections", request, ct);

    public Task<ImportMaterialsResponse> RunMaterialConnectionAsync(Guid id, RunMaterialConnectionRequest request, CancellationToken ct = default) =>
        SendAsync<ImportMaterialsResponse>(HttpMethod.Post, $"api/material-connections/{id}/run", request, ct);

    // People connections at a site (#71), and every connection's run history.

    public Task<IReadOnlyList<MaterialConnectionDto>> ListPeopleConnectionsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<MaterialConnectionDto>>($"api/people-connections?siteId={siteId}", ct);

    public Task<CreatedResponse> SavePeopleConnectionAsync(Guid? id, SaveMaterialConnectionRequest request, CancellationToken ct = default) =>
        id is { } existing
            ? SendAsync<CreatedResponse>(HttpMethod.Put, $"api/people-connections/{existing}", request, ct)
            : SendAsync<CreatedResponse>(HttpMethod.Post, "api/people-connections", request, ct);

    public Task<ImportPeopleResponse> RunPeopleConnectionAsync(Guid id, RunPeopleConnectionRequest request, CancellationToken ct = default) =>
        SendAsync<ImportPeopleResponse>(HttpMethod.Post, $"api/people-connections/{id}/run", request, ct);

    /// <summary>A connection's imports, newest first; <paramref name="target"/> is <c>materials</c> or <c>people</c>.</summary>
    public Task<IReadOnlyList<ConnectionRunDto>> ListConnectionRunsAsync(string target, Guid id, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ConnectionRunDto>>($"api/{(target == "people" ? "people" : "material")}-connections/{id}/runs", ct);

    // Outbound deliveries (D-061): the ERP feed (erp) and the document system's (documents, #78)

    public Task<DeliveryStatusDto> GetDeliveryStatusAsync(string feed, CancellationToken ct = default) =>
        GetAsync<DeliveryStatusDto>($"api/integrations/{feed}", ct);

    public Task<IReadOnlyList<DeliveryDto>> ListDeliveriesAsync(
        string feed, string? status, string? search = null, string? type = null, DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DeliveryDto>>(Query($"api/integrations/{feed}/deliveries",
            ("status", status), ("search", search), ("type", type), ("from", from?.ToString("o")), ("to", to?.ToString("o"))), ct);

    public Task<IReadOnlyList<DeliveryFeedSummaryDto>> ListDeliveryFeedsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DeliveryFeedSummaryDto>>("api/integrations", ct);

    public Task<VersionResponse> CreateDeliveryFeedAsync(CreateDeliveryFeedRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, "api/integrations", request, ct);

    public Task<RetryDeliveriesResponse> RetryDeliveryAsync(string feed, Guid id, CancellationToken ct = default) =>
        SendAsync<RetryDeliveriesResponse>(HttpMethod.Post, $"api/integrations/{feed}/deliveries/{id}/retry", new { }, ct);

    public Task<RetryDeliveriesResponse> SkipDeliveryAsync(string feed, Guid id, SkipDeliveryRequest request, CancellationToken ct = default) =>
        SendAsync<RetryDeliveriesResponse>(HttpMethod.Post, $"api/integrations/{feed}/deliveries/{id}/skip", request, ct);

    public Task<DeliveryFeedSettingsDto> GetDeliveryFeedSettingsAsync(string feed, CancellationToken ct = default) =>
        GetAsync<DeliveryFeedSettingsDto>($"api/integrations/{feed}/settings", ct);

    public Task<VersionResponse> SaveDeliveryFeedSettingsAsync(string feed, SaveDeliveryFeedSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/integrations/{feed}/settings", request, ct);

    public Task<RetryDeliveriesResponse> RetryFailedDeliveriesAsync(string feed, CancellationToken ct = default) =>
        SendAsync<RetryDeliveriesResponse>(HttpMethod.Post, $"api/integrations/{feed}/deliveries/retry-failed", new { }, ct);

    /// <summary>The site's attribute values as the import reads them (#78).</summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetLotAttributeValuesCsvAsync(Guid siteId, CancellationToken ct = default) =>
        GetFileAsync($"api/lot-attributes/values.csv?siteId={siteId}", ct);

    public Task<IReadOnlyList<LotAttributeExportDto>> ListLotAttributeValuesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LotAttributeExportDto>>($"api/lot-attributes/values?siteId={siteId}", ct);

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

    // Orders from the ERP (D-066)

    public Task<VersionResponse> AcceptErpChangeAsync(Guid workOrderId, long version, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/work-orders/{workOrderId}/erp-change/accept", new DecideErpChangeRequest { Version = version }, ct);

    public Task<VersionResponse> RejectErpChangeAsync(Guid workOrderId, long version, string reason, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/work-orders/{workOrderId}/erp-change/reject", new DecideErpChangeRequest { Version = version, Reason = reason }, ct);

    public Task<VersionResponse> SetWorkOrderQuantityAsync(Guid workOrderId, long version, decimal quantity, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/work-orders/{workOrderId}/quantity", new SetWorkOrderQuantityRequest { Version = version, Quantity = quantity }, ct);

    public Task<VersionResponse> SetSiteErpBatchRuleAsync(Guid siteId, string rule, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/sites/{siteId}/erp-batch-rule", new SetErpBatchRuleRequest { Rule = rule }, ct);

    public Task<VersionResponse> SetMaterialErpBatchRuleAsync(Guid materialId, long version, string? rule, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/materials/{materialId}/erp-batch-rule", new SetErpBatchRuleRequest { Version = version, Rule = rule }, ct);

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

    /// <summary>The other sites a person also works at (#84).</summary>
    public Task<VersionResponse> SetPersonSitesAsync(Guid personId, SetPersonSitesRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/people/{personId}/sites", request, ct);

    /// <summary>A line's jobs, for choosing what a labor session is for (#84).</summary>
    public Task<IReadOnlyList<LineJobDto>> ListLineJobsAsync(Guid equipmentId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LineJobDto>>($"api/labor/lines/{equipmentId}/jobs", ct);

    public Task<IReadOnlyList<DeclarableJobDto>> ListDeclarableJobsAsync(Guid? siteId, string? search = null, Guid? materialId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<DeclarableJobDto>>(Query("api/inventory/jobs", ("siteId", siteId), ("search", search), ("materialId", materialId)), ct);

    public Task<JobMaterialsDto> GetJobMaterialsAsync(Guid jobId, CancellationToken ct = default) =>
        GetAsync<JobMaterialsDto>($"api/jobs/{jobId}/materials", ct);

    /// <summary>Lots by the filters; with an attribute (D-056), only lots whose value of it matches: a value, or a min and max for a number or date.</summary>
    public Task<IReadOnlyList<LotSummaryDto>> SearchLotsAsync(
        Guid? siteId, string? search = null, Guid? materialId = null, Guid? locationId = null, string? status = null, DateTimeOffset? expiresBefore = null,
        int offset = 0, int limit = 500, (Guid Id, string? Value, string? Min, string? Max)? attribute = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LotSummaryDto>>(
            Query("api/lots", ("siteId", siteId), ("search", search), ("materialId", materialId), ("locationId", locationId), ("status", status),
                ("expiresBefore", Iso(expiresBefore)), ("offset", offset == 0 ? null : offset), ("limit", limit == 500 ? null : limit),
                ("attributeId", attribute?.Id), ("attributeValue", attribute?.Value), ("attributeMin", attribute?.Min), ("attributeMax", attribute?.Max)), ct);

    public Task<LotDto> GetLotAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<LotDto>($"api/lots/{id}", ct);

    // ---- Genealogy through balances (D-060) ----

    public Task<VersionResponse> SetFlowAsync(Guid locationId, SetFlowRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/inventory/locations/{locationId}/flow", request, ct);

    public Task<LotTransactionResponse> LoadLotAsync(Guid lotId, LoadLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{lotId}/load", request, ct);

    public Task<IReadOnlyList<FeedPointDto>> ListFeedPointsAsync(Guid? siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FeedPointDto>>(Query("api/inventory/feed-points", ("siteId", siteId)), ct);

    public Task<IReadOnlyList<FeedShortfallDto>> ListFeedShortfallsAsync(Guid? siteId, string status = "open", CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<FeedShortfallDto>>(Query("api/inventory/feed-shortfalls", ("siteId", siteId), ("status", status)), ct);

    public Task<VersionResponse> ResolveFeedShortfallAsync(Guid id, ResolveShortfallRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/feed-shortfalls/{id}/resolve", request, ct);

    public Task<VersionResponse> DismissFeedShortfallAsync(Guid id, DismissShortfallRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/feed-shortfalls/{id}/dismiss", request, ct);

    public Task<LabelSetupDto> GetLabelSetupAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<LabelSetupDto>($"api/labels/sites/{siteId}", ct);

    public Task<VersionResponse> SetLabelSettingsAsync(Guid siteId, SetLabelSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/labels/sites/{siteId}", request, ct);

    public Task<CreatedResponse> AddLabelPrinterAsync(Guid siteId, SaveLabelPrinterRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/labels/sites/{siteId}/printers", request, ct);

    public Task<CreatedResponse> UpdateLabelPrinterAsync(Guid id, SaveLabelPrinterRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Put, $"api/labels/printers/{id}", request, ct);

    public Task<CreatedResponse> AddSupplierFormatAsync(Guid siteId, SaveSupplierFormatRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/labels/sites/{siteId}/supplier-formats", request, ct);

    public Task<CreatedResponse> UpdateSupplierFormatAsync(Guid id, Guid siteId, SaveSupplierFormatRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Put, Query($"api/labels/supplier-formats/{id}", ("siteId", siteId)), request, ct);

    public Task<SupplierFormatTestDto> TestSupplierFormatAsync(TestSupplierFormatRequest request, CancellationToken ct = default) =>
        SendAsync<SupplierFormatTestDto>(HttpMethod.Post, "api/labels/supplier-formats/test", request, ct);

    public Task<LabelOptionsDto> GetLabelOptionsAsync(CancellationToken ct = default) => GetAsync<LabelOptionsDto>("api/labels/options", ct);

    public Task<PrintLabelsResponse> PrintLabelsAsync(PrintLabelsRequest request, CancellationToken ct = default) =>
        SendAsync<PrintLabelsResponse>(HttpMethod.Post, "api/labels/print", request, ct);

    public Task<VersionResponse> SetMaterialLeadTimeAsync(Guid materialId, SetMaterialLeadTimeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/materials/{materialId}/lead-time", request, ct);

    public Task<VersionResponse> CompleteExpectedReceiptAsync(Guid id, long version, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/expected-receipts/{id}/complete", new CancelExpectedReceiptRequest { Version = version }, ct);

    public Task<VersionResponse> SetMaterialGtinAsync(Guid materialId, SetMaterialGtinRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/materials/{materialId}/gtin", request, ct);

    public Task<VersionResponse> SetMadeFromAsync(Guid lotId, SetMadeFromRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/lots/{lotId}/made-from", request, ct);

    public Task<BalanceAllocationDto> GetBalanceAllocationAsync(Guid lotId, CancellationToken ct = default) =>
        GetAsync<BalanceAllocationDto>($"api/lots/{lotId}/allocation", ct);

    // ---- Containers, labels and scanning (D-059) ----

    public Task<IReadOnlyList<ContainerDto>> ListContainersAsync(Guid siteId, string? search = null, bool includeRetired = false, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ContainerDto>>(
            Query("api/inventory/containers", ("siteId", siteId), ("search", search), ("includeRetired", includeRetired ? "true" : null)), ct);

    public Task<ContainerDto> GetContainerAsync(Guid id, CancellationToken ct = default) => GetAsync<ContainerDto>($"api/inventory/containers/{id}", ct);

    public Task<CreatedResponse> CreateContainerAsync(CreateContainerRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/inventory/containers", request, ct);

    public Task<VersionResponse> MoveContainerAsync(Guid id, MoveContainerRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/containers/{id}/move", request, ct);

    public Task<VersionResponse> RetireContainerAsync(Guid id, long version, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/containers/{id}/retire", new RetireContainerRequest { Version = version }, ct);

    /// <summary>Packs the lot into <c>ContainerId</c>, or with none unpacks it.</summary>
    public Task<LotTransactionResponse> PackLotAsync(Guid lotId, PackLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{lotId}/pack", request, ct);

    public Task<VersionResponse> SetContainerMixingAsync(Guid containerId, SetContainerMixingRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/inventory/containers/{containerId}/mixing", request, ct);

    public Task<VersionResponse> PackContainerAsync(Guid containerId, PackContainerRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/containers/{containerId}/pack", request, ct);

    public Task<ScanResultDto> ResolveScanAsync(Guid siteId, string code, CancellationToken ct = default) =>
        GetAsync<ScanResultDto>(Query("api/inventory/scan", ("siteId", siteId), ("code", code)), ct);

    /// <summary>A lot's, container's or location's label as a PDF, to hand to the browser.</summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetLabelAsync(string kind, Guid id, Guid siteId, CancellationToken ct = default) =>
        GetFileAsync(kind switch
        {
            "lot" => $"api/lots/{id}/label.pdf",
            "container" => $"api/inventory/containers/{id}/label.pdf",
            _ => $"api/inventory/locations/{id}/label.pdf?siteId={siteId}",
        }, ct);

    // ---- Lot attributes and documents (D-056) ----

    public Task<IReadOnlyList<LotAttributeDto>> ListLotAttributesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<LotAttributeDto>>($"api/lot-attributes?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateLotAttributeAsync(CreateLotAttributeRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/lot-attributes", request, ct);

    public Task<VersionResponse> UpdateLotAttributeAsync(Guid id, UpdateLotAttributeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/lot-attributes/{id}", request, ct);

    public Task<LotRecordsDto> GetLotRecordsAsync(Guid lotId, CancellationToken ct = default) =>
        GetAsync<LotRecordsDto>($"api/lots/{lotId}/records", ct);

    public Task<VersionResponse> SetLotAttributeAsync(Guid lotId, Guid attributeId, SetLotAttributeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/lots/{lotId}/attributes/{attributeId}", request, ct);

    /// <summary>Attaches a document to a lot: a file (<paramref name="content"/>), a reference, or both.</summary>
    public async Task<CreatedResponse> AttachLotDocumentAsync(
        Guid lotId, string kind, string title, string? documentNumber, string? issuer, DateOnly? issuedOn, string? url, string? fileName, byte[]? content,
        CancellationToken ct = default)
    {
        // The form disposes its parts, and the request (disposed in SendCoreAsync) the form.
#pragma warning disable CA2000
        var form = new MultipartFormDataContent();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                form.Add(new StringContent(value), name);
            }
        }

        Add("kind", kind);
        Add("title", title);
        Add("documentNumber", documentNumber);
        Add("issuer", issuer);
        Add("issuedOn", issuedOn?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        Add("url", url);
        if (content is not null)
        {
            form.Add(new ByteArrayContent(content), "file", fileName ?? "document");
        }
#pragma warning restore CA2000

        using var response = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Post, $"api/lots/{lotId}/documents") { Content = form }, ct);
        return await ReadAsync<CreatedResponse>(response, ct);
    }

    /// <summary>A stored document's file, to hand to the browser.</summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetLotDocumentContentAsync(Guid documentId, CancellationToken ct = default) =>
        GetFileAsync($"api/lot-documents/{documentId}/content", ct);

    private async Task<(byte[] Content, string ContentType, string FileName)> GetFileAsync(string uri, CancellationToken ct)
    {
        using var response = await SendCoreAsync(new HttpRequestMessage(HttpMethod.Get, uri), ct);
        if (!response.IsSuccessStatusCode)
        {
            throw await ApiException.FromResponseAsync(response, ct);
        }

        return (await response.Content.ReadAsByteArrayAsync(ct),
            response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream",
            response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "document");
    }

    public Task<VersionResponse> WithdrawLotDocumentAsync(Guid documentId, WithdrawLotDocumentRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/lot-documents/{documentId}/withdraw", request, ct);

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

    public Task<ShortageReportDto> GetShortagesAsync(
        Guid siteId, Guid? equipmentId = null, int? horizonDays = null, bool liveTiming = true, CancellationToken ct = default) =>
        GetAsync<ShortageReportDto>(
            Query("api/inventory/shortages", ("siteId", siteId), ("equipmentId", equipmentId), ("horizonDays", horizonDays), ("liveTiming", liveTiming ? null : "false")), ct);

    // ---- Expected receipts (D-058) ----

    public Task<IReadOnlyList<ExpectedReceiptDto>> ListExpectedReceiptsAsync(Guid siteId, bool includeClosed = false, Guid? materialId = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ExpectedReceiptDto>>(
            Query("api/inventory/expected-receipts", ("siteId", siteId), ("includeClosed", includeClosed ? "true" : null), ("materialId", materialId)), ct);

    public Task<CreatedResponse> CreateExpectedReceiptAsync(CreateExpectedReceiptRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/inventory/expected-receipts", request, ct);

    public Task<VersionResponse> UpdateExpectedReceiptAsync(Guid id, UpdateExpectedReceiptRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/inventory/expected-receipts/{id}", request, ct);

    public Task<VersionResponse> CancelExpectedReceiptAsync(Guid id, long version, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/inventory/expected-receipts/{id}/cancel", new CancelExpectedReceiptRequest { Version = version }, ct);

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

    /// <summary>Checks (<paramref name="dryRun"/>) or does a chain undo of a movement and what depends on it (#79).</summary>
    public Task<ChainReversalDto> ReverseChainAsync(Guid id, ReverseChainRequest request, bool dryRun, CancellationToken ct = default) =>
        SendAsync<ChainReversalDto>(HttpMethod.Post, $"api/lots/{id}/reverse-chain?dryRun={(dryRun ? "true" : "false")}", request, ct);

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

    public Task<VersionResponse> MoveReasonCodeAsync(Guid id, MoveReasonCodeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/reason-codes/{id}/parent", request, ct);

    public Task<VersionResponse> ConvertReasonCodeAsync(Guid id, ConvertReasonCodeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/reason-codes/{id}/group", request, ct);

    /// <summary>A lot's quantity, location, container and status at a past time (#77).</summary>
    public Task<LotStateAtDto> GetLotStateAtAsync(Guid lotId, DateTimeOffset at, CancellationToken ct = default) =>
        GetAsync<LotStateAtDto>(Query($"api/lots/{lotId}/at", ("at", Iso(at))), ct);

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
        string source, string text, string? externalSystem, bool dryRun, string mediaType, bool createClasses = false, CancellationToken ct = default)
    {
        var query = $"dryRun={(dryRun ? "true" : "false")}"
            + (string.IsNullOrWhiteSpace(externalSystem) ? "" : $"&externalSystem={Uri.EscapeDataString(externalSystem.Trim())}")
            + (createClasses ? "&createClasses=true" : "");
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/materials/import/{Uri.EscapeDataString(source)}?{query}")
        {
            Content = new StringContent(text, System.Text.Encoding.UTF8, mediaType),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportMaterialsResponse>(response, ct);
    }

    /// <summary>Checks (<paramref name="dryRun"/>) or imports a capabilities CSV file, all or nothing (D-055).</summary>
    public async Task<ImportMaterialsResponse> ImportCapabilitiesCsvAsync(string csv, bool dryRun, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/capabilities/import/csv?dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"),
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

    /// <summary>Closes the lot the job is filling (D-104), so its next report starts a new one.</summary>
    public Task<StartNewLotResponse> StartNewLotAsync(Guid jobId, string? comment, CancellationToken ct = default) =>
        SendAsync<StartNewLotResponse>(HttpMethod.Post, $"api/jobs/{jobId}/new-lot", new StartNewLotRequest { Comment = comment }, ct);

    public Task<ExecutionResponse> ReopenJobAsync(Guid jobId, long version, CancellationToken ct = default) =>
        SendAsync<ExecutionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/reopen", new JobVersionRequest { Version = version }, ct);

    public Task<MachineCountsDto> GetMachineCountsAsync(Guid jobId, Guid? runId, CancellationToken ct = default) =>
        GetAsync<MachineCountsDto>($"api/jobs/{jobId}/machine-counts{(runId is { } id ? $"?runId={id}" : "")}", ct);

    public Task<ReportProductionResponse> ReportProductionAsync(Guid jobId, ReportProductionRequest request, CancellationToken ct = default) =>
        SendAsync<ReportProductionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/production", request, ct);

    // Allocations and lot recommendations (D-064)

    public Task<JobAllocationsDto> GetJobAllocationsAsync(Guid jobId, CancellationToken ct = default) =>
        GetAsync<JobAllocationsDto>($"api/jobs/{jobId}/allocations", ct);

    public Task<AllocationChangeResponse> AllocateLotAsync(Guid jobId, AllocateLotRequest request, CancellationToken ct = default) =>
        SendAsync<AllocationChangeResponse>(HttpMethod.Post, $"api/jobs/{jobId}/allocations", request, ct);

    public Task<AllocationChangeResponse> AllocateRecommendedAsync(Guid jobId, Guid? materialId, CancellationToken ct = default) =>
        SendAsync<AllocationChangeResponse>(HttpMethod.Post, $"api/jobs/{jobId}/allocations/recommended", new AllocateRecommendedRequest { MaterialId = materialId }, ct);

    public Task<AllocationChangeResponse> RefreshAllocationsAsync(Guid jobId, CancellationToken ct = default) =>
        SendAsync<AllocationChangeResponse>(HttpMethod.Post, $"api/jobs/{jobId}/allocations/refresh", new { }, ct);

    public Task<AllocationChangeResponse> ReleaseAllocationAsync(Guid jobId, Guid allocationId, string? note, CancellationToken ct = default) =>
        SendAsync<AllocationChangeResponse>(HttpMethod.Post, $"api/jobs/{jobId}/allocations/{allocationId}/release", new ReleaseAllocationRequest { Note = note }, ct);

    public Task<AllocationChangeResponse> RefreshSiteAllocationsAsync(Guid siteId, CancellationToken ct = default) =>
        SendAsync<AllocationChangeResponse>(HttpMethod.Post, $"api/inventory/allocations/refresh?siteId={siteId}", new { }, ct);

    public Task<AllocationSettingsDto> GetAllocationSettingsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<AllocationSettingsDto>($"api/inventory/allocation-settings/{siteId}", ct);

    public Task<VersionResponse> SetAllocationSettingsAsync(Guid siteId, SetAllocationSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/inventory/allocation-settings/{siteId}", request, ct);

    /// <summary>What to fetch for open jobs' allocations (#82): the site's, one job's (<paramref name="jobId"/>) or one line's.</summary>
    public Task<PickingListDto> GetPickingListAsync(Guid siteId, Guid? jobId = null, Guid? lineId = null, CancellationToken ct = default) =>
        GetAsync<PickingListDto>(Query("api/inventory/picking-list", ("siteId", siteId), ("jobId", jobId), ("lineId", lineId)), ct);

    /// <summary>The picking list as a PDF, to hand to the browser.</summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetPickingListPdfAsync(Guid siteId, Guid? jobId = null, Guid? lineId = null, CancellationToken ct = default) =>
        GetFileAsync(Query("api/inventory/picking-list.pdf", ("siteId", siteId), ("jobId", jobId), ("lineId", lineId)), ct);

    /// <summary>Corrects a report's figures (#80): reversed and reported again in one transaction.</summary>
    public Task<CorrectProductionResponse> CorrectProductionReportAsync(Guid jobId, Guid reportId, CorrectProductionRequest request, CancellationToken ct = default) =>
        SendAsync<CorrectProductionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/production/{reportId}/correct", request, ct);

    public Task<ReverseProductionResponse> ReverseProductionReportAsync(Guid jobId, Guid reportId, ReverseProductionRequest request, CancellationToken ct = default) =>
        SendAsync<ReverseProductionResponse>(HttpMethod.Post, $"api/jobs/{jobId}/production/{reportId}/reverse", request, ct);

    // Quality (#65): characteristics, specifications, results, rejecting lots

    public Task<IReadOnlyList<CharacteristicDto>> ListCharacteristicsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<CharacteristicDto>>("api/quality/characteristics", ct);

    public Task<CreatedResponse> CreateCharacteristicAsync(CreateCharacteristicRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/quality/characteristics", request, ct);

    public Task<VersionResponse> UpdateCharacteristicAsync(Guid id, UpdateCharacteristicRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/quality/characteristics/{id}", request, ct);

    public Task<IReadOnlyList<TagBindingDto>> ListTagBindingsAsync(Guid characteristicId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<TagBindingDto>>($"api/quality/characteristics/{characteristicId}/tags", ct);

    public Task<IReadOnlyList<ProcessValueTagDto>> ListProcessValueTagsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ProcessValueTagDto>>("api/quality/process-value-tags", ct);

    public Task<CreatedResponse> AddTagBindingAsync(Guid characteristicId, AddTagBindingRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/quality/characteristics/{characteristicId}/tags", request, ct);

    public Task<VersionResponse> UpdateTagBindingAsync(Guid id, UpdateTagBindingRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/quality/characteristic-tags/{id}", request, ct);

    public Task<IReadOnlyList<SpcAlertDto>> ListSpcAlertsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SpcAlertDto>>($"api/quality/alerts?siteId={siteId}", ct);

    public Task<SpcAlertDto> GetSpcAlertAsync(Guid siteId, Guid id, CancellationToken ct = default) =>
        GetAsync<SpcAlertDto>($"api/quality/alerts/{id}?siteId={siteId}", ct);

    public Task<SpcAlertDto> CloseSpcAlertAsync(Guid siteId, Guid id, CloseSpcAlertRequest request, CancellationToken ct = default) =>
        SendAsync<SpcAlertDto>(HttpMethod.Post, $"api/quality/alerts/{id}/close?siteId={siteId}", request, ct);

    public Task<ControlChartDto> GetControlChartAsync(
        Guid characteristicId, Guid siteId, Guid materialId, Guid? equipmentId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        GetAsync<ControlChartDto>(
            $"api/quality/characteristics/{characteristicId}/chart?siteId={siteId}&materialId={materialId}"
            + (equipmentId is { } line ? $"&equipmentId={line}" : "")
            + $"&from={Uri.EscapeDataString(from.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}"
            + $"&to={Uri.EscapeDataString(to.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", ct);

    public Task<IReadOnlyList<ControlLimitDto>> ListControlLimitsAsync(Guid characteristicId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ControlLimitDto>>($"api/quality/characteristics/{characteristicId}/control-limits", ct);

    public Task<LimitBaselineDto> GetLimitBaselineAsync(
        Guid characteristicId, Guid materialId, Guid? equipmentId, DateTimeOffset from, DateTimeOffset to, IReadOnlyCollection<Guid> leftOut, CancellationToken ct = default) =>
        GetAsync<LimitBaselineDto>(
            $"api/quality/characteristics/{characteristicId}/control-limits/baseline?materialId={materialId}"
            + (equipmentId is { } line ? $"&equipmentId={line}" : "")
            + $"&from={Uri.EscapeDataString(from.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}"
            + $"&to={Uri.EscapeDataString(to.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}"
            + (leftOut.Count > 0 ? $"&leftOut={string.Join(',', leftOut)}" : ""), ct);

    public Task<ControlLimitChangeResponse> ApproveControlLimitsAsync(Guid characteristicId, ApproveControlLimitsRequest request, CancellationToken ct = default) =>
        SendAsync<ControlLimitChangeResponse>(HttpMethod.Post, $"api/quality/characteristics/{characteristicId}/control-limits", request, ct);

    public Task<ControlLimitChangeResponse> SetManualControlLimitsAsync(Guid characteristicId, SetManualControlLimitsRequest request, CancellationToken ct = default) =>
        SendAsync<ControlLimitChangeResponse>(HttpMethod.Post, $"api/quality/characteristics/{characteristicId}/control-limits/manual", request, ct);

    public Task<ControlLimitChangeResponse> EndControlLimitsAsync(Guid limitId, EndControlLimitsRequest request, CancellationToken ct = default) =>
        SendAsync<ControlLimitChangeResponse>(HttpMethod.Post, $"api/quality/control-limits/{limitId}/end", request, ct);

    public Task<IReadOnlyList<SpecSummaryDto>> ListSpecsAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SpecSummaryDto>>("api/quality/specs", ct);

    public Task<SpecDto> GetSpecAsync(Guid id, CancellationToken ct = default) => GetAsync<SpecDto>($"api/quality/specs/{id}", ct);

    public Task<CreatedResponse> CreateSpecAsync(Guid materialId, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/quality/specs", new CreateSpecRequest { MaterialId = materialId }, ct);

    public Task<SpecVersionDto> GetSpecVersionAsync(Guid specId, Guid versionId, CancellationToken ct = default) =>
        GetAsync<SpecVersionDto>($"api/quality/specs/{specId}/versions/{versionId}", ct);

    public Task<SaveSpecDraftResponse> SaveSpecDraftAsync(Guid specId, Guid versionId, SaveSpecDraftRequest request, CancellationToken ct = default) =>
        SendAsync<SaveSpecDraftResponse>(HttpMethod.Put, $"api/quality/specs/{specId}/versions/{versionId}", request, ct);

    public Task<VersionResponse> ReleaseSpecVersionAsync(Guid specId, Guid versionId, ReleaseSpecVersionRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/quality/specs/{specId}/versions/{versionId}/release", request, ct);

    public Task<CreatedResponse> StartSpecDraftAsync(Guid specId, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, $"api/quality/specs/{specId}/drafts", new { }, ct);

    /// <summary>The material's released specification, or null when it has none.</summary>
    public async Task<ReleasedSpecDto?> GetReleasedSpecAsync(Guid materialId, CancellationToken ct = default)
    {
        try
        {
            return await GetAsync<ReleasedSpecDto>($"api/materials/{materialId}/specification", ct);
        }
        catch (ApiException ex) when (ex.Status == (int)HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public Task<IReadOnlyList<QualityResultDto>> ListQualityResultsAsync(
        Guid? siteId, Guid? materialId = null, Guid? lotId = null, Guid? jobId = null, Guid? characteristicId = null, bool failingOnly = false,
        DateTimeOffset? from = null, DateTimeOffset? to = null, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<QualityResultDto>>(Query("api/quality/results",
            ("siteId", siteId), ("materialId", materialId), ("lotId", lotId), ("jobId", jobId), ("characteristicId", characteristicId),
            ("failingOnly", failingOnly ? "true" : null), ("from", from?.ToString("o")), ("to", to?.ToString("o"))), ct);

    public Task<QualityResultResponse> RecordQualityResultAsync(Guid siteId, RecordQualityResultRequest request, CancellationToken ct = default) =>
        SendAsync<QualityResultResponse>(HttpMethod.Post, $"api/quality/results?siteId={siteId}", request, ct);

    public Task<QualityResultDto> ExcludeQualityResultAsync(Guid siteId, Guid id, ExcludeQualityResultRequest request, CancellationToken ct = default) =>
        SendAsync<QualityResultDto>(HttpMethod.Post, $"api/quality/results/{id}/exclude?siteId={siteId}", request, ct);

    public Task<LotTransactionResponse> RejectLotAsync(Guid lotId, RejectLotRequest request, CancellationToken ct = default) =>
        SendAsync<LotTransactionResponse>(HttpMethod.Post, $"api/lots/{lotId}/reject", request, ct);

    // Planning rates from run history (D-045)

    public Task<RunHistoryRateDto> GetRunHistoryRateAsync(Guid runStandardId, int days, bool? takeOutStops = null, CancellationToken ct = default) =>
        GetAsync<RunHistoryRateDto>(
            $"api/scheduling/run-standards/{runStandardId}/history-rate?days={days}{(takeOutStops is { } stops ? $"&takeOutStops={(stops ? "true" : "false")}" : "")}", ct);

    public Task<VersionResponse> AcceptRateProposalAsync(Guid runStandardId, AcceptRateProposalRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/run-standards/{runStandardId}/proposal/accept", request, ct);

    public Task<VersionResponse> RejectRateProposalAsync(Guid runStandardId, RejectRateProposalRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/scheduling/run-standards/{runStandardId}/proposal/reject", request, ct);

    public Task<IReadOnlyList<RateProposalDto>> ListRateProposalsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<RateProposalDto>>($"api/scheduling/sites/{siteId}/rate-proposals", ct);

    public Task<RateRefreshSettingsDto> GetRateRefreshSettingsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<RateRefreshSettingsDto>($"api/scheduling/sites/{siteId}/rate-refresh", ct);

    public Task<VersionResponse> SetRateRefreshSettingsAsync(Guid siteId, SetRateRefreshSettingsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/scheduling/sites/{siteId}/rate-refresh", request, ct);

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

    // Changeover rules (D-015, D-074)

    public Task<IReadOnlyList<ChangeoverRuleDto>> ListChangeoverRulesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ChangeoverRuleDto>>($"api/changeover-rules?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateChangeoverRuleAsync(CreateChangeoverRuleRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/changeover-rules", request, ct);

    public Task<VersionResponse> UpdateChangeoverRuleAsync(Guid id, UpdateChangeoverRuleRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/changeover-rules/{id}", request, ct);

    /// <summary>Checks (<paramref name="dryRun"/>) or imports a site's changeover rules from CSV, all or nothing (D-074).</summary>
    public async Task<ImportMaterialsResponse> ImportChangeoverRulesCsvAsync(Guid siteId, string csv, bool dryRun, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/changeover-rules/import/csv?siteId={siteId}&dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportMaterialsResponse>(response, ct);
    }

    /// <summary>Imports lot attribute values (#78); <paramref name="dryRun"/> checks without saving.</summary>
    /// <summary>B2MML material lots at a site (#72): new lots received, known ones' properties set as attributes.</summary>
    /// <summary>A B2MML production schedule's requests as ERP work orders (D-102); a dry run says what would happen and saves nothing.</summary>
    public async Task<ImportWorkOrdersResponse> ImportB2mmlWorkOrdersAsync(string xml, string externalSystem, string? siteCode, bool dryRun, CancellationToken ct = default)
    {
        var site = string.IsNullOrWhiteSpace(siteCode) ? "" : $"&siteCode={Uri.EscapeDataString(siteCode)}";
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/work-orders/import/b2mml?externalSystem={Uri.EscapeDataString(externalSystem)}{site}&dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportWorkOrdersResponse>(response, ct);
    }

    public async Task<ImportMaterialsResponse> ImportB2mmlLotsAsync(Guid siteId, string xml, bool dryRun, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/lots/import/b2mml?siteId={siteId}&dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(xml, System.Text.Encoding.UTF8, "application/xml"),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportMaterialsResponse>(response, ct);
    }

    /// <summary>A production job's totals as a B2MML production performance for the ERP (D-102).</summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetJobProductionPerformanceAsync(Guid jobId, CancellationToken ct = default) =>
        GetFileAsync($"api/jobs/{jobId}/production-performance", ct);

    /// <summary>Every class and material as B2MML (#72), which the B2MML import reads back.</summary>
    public Task<(byte[] Content, string ContentType, string FileName)> GetMaterialsB2mmlAsync(CancellationToken ct = default) =>
        GetFileAsync("api/materials/export/b2mml", ct);

    public async Task<ImportMaterialsResponse> ImportLotAttributeValuesAsync(Guid siteId, string csv, bool dryRun, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/lot-attributes/values/import?siteId={siteId}&dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<ImportMaterialsResponse>(response, ct);
    }

    // OEE targets (D-073)

    /// <summary>A site's shift handover (#124): each line's open items, current and previous shift.</summary>
    public Task<Pulaski.Contracts.Handover.HandoverDto> GetHandoverAsync(Guid siteId, Guid? lineId = null, CancellationToken ct = default) =>
        GetAsync<Pulaski.Contracts.Handover.HandoverDto>(lineId is { } line ? $"api/handover?siteId={siteId}&lineId={line}" : $"api/handover?siteId={siteId}", ct);

    public Task<CreatedResponse> AddHandoverEntryAsync(Pulaski.Contracts.Handover.AddHandoverEntryRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/handover/entries", request, ct);

    public Task<VersionResponse> CloseHandoverItemAsync(Guid id, Pulaski.Contracts.Handover.CloseHandoverItemRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, $"api/handover/entries/{id}/close", request, ct);

    public Task<IReadOnlyList<Pulaski.Contracts.Localization.LanguageDto>> ListLanguagesAsync(CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Pulaski.Contracts.Localization.LanguageDto>>("api/languages", ct);

    public Task<VersionResponse> AddLanguageAsync(Pulaski.Contracts.Localization.CreateLanguageRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Post, "api/languages", request, ct);

    public Task<VersionResponse> UpdateLanguageAsync(string code, Pulaski.Contracts.Localization.UpdateLanguageRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/languages/{Uri.EscapeDataString(code)}", request, ct);

    public Task<IReadOnlyList<Pulaski.Contracts.Localization.TranslationTermDto>> GetLanguageTermsAsync(string code, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<Pulaski.Contracts.Localization.TranslationTermDto>>($"api/languages/{Uri.EscapeDataString(code)}/terms", ct);

    public Task<VersionResponse> SetTranslationsAsync(string code, Pulaski.Contracts.Localization.SetTranslationsRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/languages/{Uri.EscapeDataString(code)}/translations", request, ct);

    public Task<(byte[] Content, string ContentType, string FileName)> ExportTranslationsAsync(string code, CancellationToken ct = default) =>
        GetFileAsync($"api/languages/{Uri.EscapeDataString(code)}/translations.csv", ct);

    /// <summary>Checks (<paramref name="dryRun"/>) or imports a language's translations from CSV, all or nothing (#128).</summary>
    public async Task<Pulaski.Contracts.Localization.ImportTranslationsResponse> ImportTranslationsAsync(string code, string csv, bool dryRun, CancellationToken ct = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"api/languages/{Uri.EscapeDataString(code)}/import?dryRun={(dryRun ? "true" : "false")}")
        {
            Content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv"),
        };
        using var response = await SendCoreAsync(request, ct);
        return await ReadAsync<Pulaski.Contracts.Localization.ImportTranslationsResponse>(response, ct);
    }

    public Task<VersionResponse> SetPreferencesAsync(Pulaski.Contracts.Localization.PreferencesRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, "api/me/preferences", request, ct);

    /// <summary>What waits for the signed-in user to decide (#123).</summary>
    public Task<Pulaski.Contracts.Inbox.InboxDto> GetInboxAsync(CancellationToken ct = default) => GetAsync<Pulaski.Contracts.Inbox.InboxDto>("api/me/inbox", ct);

    /// <summary>The signed-in user's e-mail alerts (#121).</summary>
    public Task<MyAlertsDto> GetMyAlertsAsync(CancellationToken ct = default) => GetAsync<MyAlertsDto>("api/me/alerts", ct);

    public Task<CreatedResponse> CreateAlertSubscriptionAsync(CreateAlertSubscriptionRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/me/alerts", request, ct);

    public Task<VersionResponse> UpdateAlertSubscriptionAsync(Guid id, UpdateAlertSubscriptionRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/me/alerts/{id}", request, ct);

    /// <summary>The site's andon call types (#125), in their order.</summary>
    public Task<IReadOnlyList<AndonTypeDto>> ListAndonTypesAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<AndonTypeDto>>($"api/andon/types?siteId={siteId}", ct);

    /// <summary>The site's calls not yet resolved (#158), or with <paramref name="openOnly"/> false those raised in [from, until).</summary>
    public Task<IReadOnlyList<AndonCallDto>> ListAndonCallsAsync(
        Guid siteId, bool openOnly = true, DateTimeOffset? from = null, DateTimeOffset? until = null, CancellationToken ct = default)
    {
        var query = $"api/andon/calls?siteId={siteId}&open={(openOnly ? "true" : "false")}";
        if (from is { } f)
        {
            query += $"&from={Uri.EscapeDataString(f.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}";
        }

        if (until is { } u)
        {
            query += $"&to={Uri.EscapeDataString(u.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}";
        }

        return GetAsync<IReadOnlyList<AndonCallDto>>(query, ct);
    }

    /// <summary>How fast the site's calls were answered (#159), over calls raised in [from, until).</summary>
    public Task<AndonResponseTimesDto> GetAndonResponseTimesAsync(Guid siteId, DateTimeOffset from, DateTimeOffset until, CancellationToken ct = default) =>
        GetAsync<AndonResponseTimesDto>(
            $"api/andon/response-times?siteId={siteId}&from={Uri.EscapeDataString(from.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}" +
            $"&to={Uri.EscapeDataString(until.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}", ct);

    public Task<AndonCallResponse> AcknowledgeAndonCallAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<AndonCallResponse>(HttpMethod.Post, $"api/andon/calls/{id}/acknowledge", new { }, ct);

    public Task<AndonCallResponse> ResolveAndonCallAsync(Guid id, ResolveAndonCallRequest request, CancellationToken ct = default) =>
        SendAsync<AndonCallResponse>(HttpMethod.Post, $"api/andon/calls/{id}/resolve", request, ct);

    public Task<CreatedResponse> CreateAndonTypeAsync(CreateAndonTypeRequest request, CancellationToken ct = default) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/andon/types", request, ct);

    public Task<VersionResponse> UpdateAndonTypeAsync(Guid id, UpdateAndonTypeRequest request, CancellationToken ct = default) =>
        SendAsync<VersionResponse>(HttpMethod.Put, $"api/andon/types/{id}", request, ct);

    public Task<IReadOnlyList<OeeTargetDto>> ListOeeTargetsAsync(Guid siteId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<OeeTargetDto>>($"api/oee-targets/sites/{siteId}", ct);

    public Task<IReadOnlyList<OeeTargetDto>> SetOeeTargetAsync(Guid siteId, SetOeeTargetRequest request, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<OeeTargetDto>>(HttpMethod.Put, $"api/oee-targets/sites/{siteId}", request, ct);

    public Task<IReadOnlyList<OeeTargetDto>> EndOeeTargetAsync(Guid siteId, EndOeeTargetRequest request, CancellationToken ct = default) =>
        SendAsync<IReadOnlyList<OeeTargetDto>>(HttpMethod.Post, $"api/oee-targets/sites/{siteId}/end", request, ct);

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

    /// <summary>The operator station (#146) for whoever <paramref name="ticket"/> signs on (none: waiting).</summary>
    public Task<StationResponse> GetStationAsync(string secret, string? ticket, string? line, CancellationToken ct = default) =>
        TerminalSendAsync<StationResponse>(secret, HttpMethod.Post, "api/terminal/station", new StationRequest { Ticket = ticket, Line = line }, ct);

    /// <summary>Start, pause or complete a job at the station (#147): <paramref name="action"/> is <c>start</c>, <c>pause</c> or <c>complete</c>.</summary>
    public Task<StationActionResponse> StationJobAsync(string secret, string action, StationJobRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<StationActionResponse>(secret, HttpMethod.Post, $"api/terminal/station/{action}", request, ct);

    /// <summary>A production report at the station (#148).</summary>
    public Task<StationActionResponse> StationReportAsync(string secret, StationReportRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<StationActionResponse>(secret, HttpMethod.Post, "api/terminal/station/report", request, ct);

    /// <summary>Code one of the station's stops (#149).</summary>
    public Task<StationActionResponse> StationCodeStopAsync(string secret, StationStopRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<StationActionResponse>(secret, HttpMethod.Post, "api/terminal/station/code-stop", request, ct);

    /// <summary>Andon at the station (#157): <paramref name="action"/> is call, acknowledge or resolve.</summary>
    public Task<StationActionResponse> StationAndonAsync(string secret, string action, StationAndonRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<StationActionResponse>(secret, HttpMethod.Post, $"api/terminal/station/andon/{action}", request, ct);

    public Task<StationResponse> StationSignOnAsync(string secret, StationSignOnRequest request, CancellationToken ct = default) =>
        TerminalSendAsync<StationResponse>(secret, HttpMethod.Post, "api/terminal/station/sign-on", request, ct);

    /// <summary>The line display board for the terminal's lines (#120); <paramref name="line"/> narrows it to one.</summary>
    public Task<LineDisplayDto> GetLineDisplayAsync(string secret, string? line, CancellationToken ct = default) =>
        TerminalSendAsync<LineDisplayDto>(secret, HttpMethod.Get, line is null ? "api/terminal/board" : $"api/terminal/board?line={Uri.EscapeDataString(line)}", null, ct);

    public Task<TerminalStatusDto> GetTerminalStatusAsync(string secret, CancellationToken ct = default) =>
        TerminalSendAsync<TerminalStatusDto>(secret, HttpMethod.Get, "api/terminal", null, ct);

    /// <summary>The badged person's status again, by their ticket (#84).</summary>
    public Task<TerminalStatusDto> GetTerminalPersonAsync(string secret, string ticket, CancellationToken ct = default) =>
        TerminalSendAsync<TerminalStatusDto>(secret, HttpMethod.Post, "api/terminal/person", new TerminalActionRequest { Ticket = ticket }, ct);

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
                throw new ApiException(0, Term.Mark("The Pulaski API is not reachable."), ex.Message, null);
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
            ?? throw new ApiException((int)response.StatusCode, Term.Mark("Empty response from the API."), null, null);
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

    /// <summary>The detail's English template with {0}, {1}… (#128): the key a screen translates it by. Null when the API sent none.</summary>
    public string? Template { get; init; }

    /// <summary>The values that fill <see cref="Template"/>.</summary>
    public IReadOnlyList<object?> Values { get; init; } = [];

    /// <summary>
    /// Each field's messages as templates and values (#189), keyed and ordered as <see cref="Errors"/>; a field the API sent none for is missing.
    /// </summary>
    public IReadOnlyDictionary<string, IReadOnlyList<(string Template, IReadOnlyList<object?> Values)>> FieldMessages { get; init; } =
        new Dictionary<string, IReadOnlyList<(string Template, IReadOnlyList<object?> Values)>>();

    internal static async Task<ApiException> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<Problem>(ct);
            if (problem is not null)
            {
                return new ApiException(status, problem.Title ?? response.ReasonPhrase ?? "Error", problem.Detail, problem.Errors)
                {
                    Template = problem.Template,
                    Values = ValuesOf(problem.Values),
                    FieldMessages = problem.FieldMessages?.ToDictionary(
                        f => f.Key,
                        f => (IReadOnlyList<(string, IReadOnlyList<object?>)>)[.. f.Value.Select(m => (m.Template ?? "", (IReadOnlyList<object?>)ValuesOf(m.Values)))])
                        ?? [],
                };
            }
        }
        catch (JsonException)
        {
            // Not a problem document; fall through.
        }

        return new ApiException(status, response.ReasonPhrase ?? "Error", null, null);
    }

    private sealed record Problem(
        string? Title, string? Detail, Dictionary<string, string[]>? Errors, string? Template = null, JsonElement[]? Values = null,
        Dictionary<string, FieldMessage[]>? FieldMessages = null);

    private sealed record FieldMessage(string? Template, JsonElement[]? Values);

    /// <summary>A message's values as sent (#128): numbers as numbers, for the reader's region; the rest as text.</summary>
    private static object?[] ValuesOf(JsonElement[]? values) =>
        values is null ? [] : [.. values.Select(v => v.ValueKind switch
        {
            JsonValueKind.Number when v.TryGetDecimal(out var number) => number,
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Null => null,
            _ => (object?)v.ToString(),
        })];
}
