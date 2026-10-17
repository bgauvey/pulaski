using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Pulaski.Contracts.Security;
using Pulaski.Domain.Security;
using Pulaski.Testing;

namespace Pulaski.Api.Tests;

/// <summary>
/// Hosts the API with in-memory stores, so endpoint and error mapping run without a database. Clients sign in
/// as an administrator unless a test asks for someone else (D-037).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string Password = "Test-password-1";

    private readonly Lock _lock = new();
    private bool _signedOut;

    public ApiFactory()
    {
        Stores.Security.AddUser("admin", Password, Roles.Administrator);
    }

    public InMemoryStores Stores { get; } = new();

    /// <summary>A client signed in as a new user holding <paramref name="roleCode"/>, at <paramref name="siteId"/> unless global.</summary>
    public async Task<HttpClient> CreateClientAsAsync(string roleCode, Guid? siteId = null)
    {
        var username = $"{roleCode.Replace('_', '-')}-{Guid.CreateVersion7():N}"[..30];
        Stores.Security.AddUser(username, Password, roleCode, siteId);
        var client = CreateSignedOutClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await SignInAsync(client, username));
        return client;
    }

    /// <summary>A client that hasn't signed in.</summary>
    public HttpClient CreateSignedOutClient()
    {
        lock (_lock)
        {
            _signedOut = true;
            try
            {
                return CreateDefaultClient();
            }
            finally
            {
                _signedOut = false;
            }
        }
    }

    public static async Task<string> SignInAsync(HttpClient client, string username, string password = Password)
    {
        var response = await client.PostAsJsonAsync("/api/auth/sign-in", new SignInRequest { Username = username, Password = password });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SignInResponse>())!.Token;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Never opened: every store is replaced below.
        builder.UseSetting("ConnectionStrings:Pulaski", "Host=unused.invalid;Database=none");
        builder.UseSetting("Pulaski:DatabaseMaintenance", "false");
        builder.UseSetting("Security:SigningKey", "api-tests-signing-key-0123456789abcdef0123");
        builder.ConfigureTestServices(services => Stores.Register(services));
    }

    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        if (!_signedOut)
        {
            // CreateClient() signs in as the administrator.
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", SignInAsync(CreateSignedOutClient(), "admin").GetAwaiter().GetResult());
        }
    }
}
