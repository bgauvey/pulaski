using Microsoft.AspNetCore.Authentication.Cookies;
using Pulaski.Web.Components;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Security;

var builder = WebApplication.CreateBuilder(args);

var apiBaseUrl = builder.Configuration["PulaskiApi:BaseUrl"]
    ?? throw new InvalidOperationException("PulaskiApi:BaseUrl is not configured.");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Signing in (D-037): a cookie session holding the API's token; every page needs one.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".Pulaski.Session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.LoginPath = AccountSession.LoginPath;
        options.AccessDeniedPath = AccountSession.LoginPath;
        options.SlidingExpiration = false;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<EquipmentSelection>();
builder.Services.AddScoped<SiteSelection>();
builder.Services.AddScoped<Downloads>();
builder.Services.AddScoped<ApiSession>();
builder.Services.AddScoped<CurrentUser>();
builder.Services.AddHttpClient<PulaskiApiClient>(client => client.BaseAddress = new Uri(apiBaseUrl));

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

// A temporary password must be replaced before anything else (D-037).
app.Use(async (context, next) =>
{
    if (context.User.HasClaim(c => c.Type == AccountSession.MustChangeClaim)
        && HttpMethods.IsGet(context.Request.Method)
        && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.Ordinal)
        && !context.Request.Path.StartsWithSegments("/account", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Redirect(AccountSession.PasswordPath);
        return;
    }

    await next(context);
});

app.UseAntiforgery();

app.MapStaticAssets();
app.MapAccountEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode()
    .RequireAuthorization();

app.Run();
