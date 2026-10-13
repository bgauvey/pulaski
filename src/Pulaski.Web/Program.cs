using Pulaski.Web.Components;
using Pulaski.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var apiBaseUrl = builder.Configuration["PulaskiApi:BaseUrl"]
    ?? throw new InvalidOperationException("PulaskiApi:BaseUrl is not configured.");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.AddHttpClient<PulaskiApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
