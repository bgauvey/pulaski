using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Security;

namespace Pulaski.DemoSetup;

/// <summary>The few API calls the demo setup makes, as whoever signed in last.</summary>
public sealed class PulaskiApi(HttpClient http) : IDisposable
{
    public void Dispose() => http.Dispose();

    /// <summary>Signs in; null when the user name and password aren't accepted.</summary>
    public async Task<SignInResponse?> SignInAsync(string username, string password, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("api/auth/sign-in", new SignInRequest { Username = username, Password = password }, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            return null;
        }

        var signedIn = await ReadAsync<SignInResponse>(response, ct);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", signedIn.Token);
        return signedIn;
    }

    /// <summary>Waits until the API answers at all, as it starts after the database is set up.</summary>
    public async Task WaitUntilUpAsync(TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            try
            {
                using var response = await http.GetAsync("api/me", ct);
                return;
            }
            catch (HttpRequestException) when (DateTimeOffset.UtcNow < until)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }
    }

    public Task ChangePasswordAsync(string current, string next, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, "api/auth/password", new ChangePasswordRequest { CurrentPassword = current, NewPassword = next }, ct);

    public Task ResetPasswordAsync(Guid userId, string temporaryPassword, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"api/users/{userId}/password", new ResetPasswordRequest { TemporaryPassword = temporaryPassword }, ct);

    public Task<IReadOnlyList<EquipmentDto>> ListEquipmentAsync(CancellationToken ct) => GetAsync<IReadOnlyList<EquipmentDto>>("api/equipment", ct);

    public Task<IReadOnlyList<EdgeNodeDto>> ListEdgeNodesAsync(Guid siteId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<EdgeNodeDto>>($"api/edge-nodes?siteId={siteId}", ct);

    public Task<EdgeNodeDetailDto> GetEdgeNodeAsync(Guid id, CancellationToken ct) => GetAsync<EdgeNodeDetailDto>($"api/edge-nodes/{id}", ct);

    public async Task<Guid> CreateEdgeNodeAsync(CreateEdgeNodeRequest request, CancellationToken ct) =>
        (await SendAsync<CreatedResponse>(HttpMethod.Post, "api/edge-nodes", request, ct)).Id;

    public async Task<Guid> CreateConnectionAsync(Guid edgeNodeId, CreateEdgeConnectionRequest request, CancellationToken ct) =>
        (await SendAsync<CreatedResponse>(HttpMethod.Post, $"api/edge-nodes/{edgeNodeId}/connections", request, ct)).Id;

    public Task CreateTagAsync(CreateEquipmentTagRequest request, CancellationToken ct) => SendAsync(HttpMethod.Post, "api/equipment-tags", request, ct);

    public Task<IReadOnlyList<MachineProfileDto>> ListMachineProfilesAsync(Guid siteId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<MachineProfileDto>>($"api/machine-profiles?siteId={siteId}", ct);

    public Task<CreatedResponse> CreateMachineProfileAsync(CreateMachineProfileRequest request, CancellationToken ct) =>
        SendAsync<CreatedResponse>(HttpMethod.Post, "api/machine-profiles", request, ct);

    /// <summary>Loads a profile's definitions from its CSV file, all or nothing.</summary>
    public async Task<ProfileImportResponse> ImportMachineProfileAsync(Guid id, long version, string csv, CancellationToken ct)
    {
        using var content = new StringContent(csv, System.Text.Encoding.UTF8, "text/csv");
        using var response = await http.PostAsync($"api/machine-profiles/{id}/import/csv?version={version}", content, ct);
        return await ReadAsync<ProfileImportResponse>(response, ct);
    }

    public Task SetMachineDataAsync(Guid equipmentId, SetEquipmentMachineDataRequest request, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"api/equipment/{equipmentId}/machine-data", request, ct);

    public Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken ct) => GetAsync<IReadOnlyList<UserDto>>("api/users", ct);

    public async Task<Guid> CreateUserAsync(CreateUserRequest request, CancellationToken ct) =>
        (await SendAsync<CreatedResponse>(HttpMethod.Post, "api/users", request, ct)).Id;

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(path, ct);
        return await ReadAsync<T>(response, ct);
    }

    private async Task SendAsync(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        await EnsureAsync(response, ct);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        using var response = await http.SendAsync(request, ct);
        return await ReadAsync<T>(response, ct);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await EnsureAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ct))!;
    }

    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new ApiCallException(response.StatusCode, $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath}: {(int)response.StatusCode} {body}");
        }
    }
}

public sealed class ApiCallException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}
