using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using Pulaski.Contracts.MachineData;
using Pulaski.Contracts.Security;

namespace Pulaski.Edge;

/// <summary>The edge's calls to the Pulaski API (D-041), signed in as its service user; signs in again when the token runs out.</summary>
public sealed class PulaskiClient(HttpClient http, IOptions<EdgeOptions> options, TimeProvider time, ILogger<PulaskiClient> log) : IDisposable
{
    public static string AgentVersion { get; } = typeof(PulaskiClient).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private readonly SemaphoreSlim _signIn = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    private EdgeOptions Options => options.Value;

    /// <summary>One long-lived client for the edge's life; connections are renewed every few minutes, so DNS changes are seen.</summary>
#pragma warning disable CA2000 // The client owns the handler and disposes it.
    public static HttpClient CreateHttp(Uri apiUrl) =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true) { BaseAddress = apiUrl, Timeout = TimeSpan.FromSeconds(30) };
#pragma warning restore CA2000

    public void Dispose()
    {
        _signIn.Dispose();
        http.Dispose();
    }

    public Task<EdgeConfigDto> GetConfigAsync(CancellationToken cancellationToken) =>
        SendAsync<EdgeConfigDto>(() => new HttpRequestMessage(HttpMethod.Get, $"api/edge/nodes/{Options.EdgeNodeId}/config"), cancellationToken);

    public Task<EdgeSignalBatchResponse> SendSignalsAsync(EdgeSignalBatchRequest batch, CancellationToken cancellationToken) =>
        SendAsync<EdgeSignalBatchResponse>(() => new HttpRequestMessage(HttpMethod.Post, $"api/edge/nodes/{Options.EdgeNodeId}/signals") { Content = JsonContent.Create(batch) }, cancellationToken);

    public Task<int> SendMetricsAsync(EdgeMetricsRequest metrics, CancellationToken cancellationToken) =>
        SendAsync<int>(() => new HttpRequestMessage(HttpMethod.Post, $"api/edge/nodes/{Options.EdgeNodeId}/metrics") { Content = JsonContent.Create(metrics) }, cancellationToken);

    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> request, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var message = request();
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(attempt > 0, cancellationToken));
            using var response = await http.SendAsync(message, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                throw new PulaskiApiException(response.StatusCode, body.Length > 500 ? body[..500] : body);
            }

            return (await response.Content.ReadFromJsonAsync<T>(cancellationToken))!;
        }
    }

    private async Task<string> TokenAsync(bool renew, CancellationToken cancellationToken)
    {
        await _signIn.WaitAsync(cancellationToken);
        try
        {
            if (!renew && _token is not null && time.GetUtcNow() < _expires - TimeSpan.FromMinutes(1))
            {
                return _token;
            }

            using var response = await http.PostAsJsonAsync("api/auth/sign-in", new SignInRequest { Username = Options.Username, Password = Options.Password }, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new PulaskiApiException(response.StatusCode, $"{Options.Username} can't sign in to Pulaski; check the edge's user name and password.");
            }

            var signedIn = (await response.Content.ReadFromJsonAsync<SignInResponse>(cancellationToken))!;
            if (signedIn.User.MustChangePassword)
            {
                throw new PulaskiApiException(HttpStatusCode.Forbidden,
                    $"{Options.Username} still has a temporary password: sign in once in Pulaski as {Options.Username}, set its password, and put that in the edge's configuration.");
            }

            (_token, _expires) = (signedIn.Token, signedIn.ExpiresAt);
            log.LogInformation("Signed in to Pulaski as {User}", Options.Username);
            return _token;
        }
        finally
        {
            _signIn.Release();
        }
    }
}

/// <summary>The API refused a call, or couldn't be reached.</summary>
public sealed class PulaskiApiException(HttpStatusCode status, string message) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;

    /// <summary>A refusal that sending again won't fix (a bad request); anything else is retried.</summary>
    public bool IsPermanent => Status is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotFound;
}
