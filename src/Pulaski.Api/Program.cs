using Pulaski.Api.Integrations;
using Pulaski.Application.Integrations;
using Pulaski.Api.Endpoints;
using Pulaski.Api.WorkOrders;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Pulaski.Api.Infrastructure;
using Pulaski.Api.Security;
using Pulaski.Application.Security;
using Pulaski.Application;
using Pulaski.Application.Abstractions;
using Pulaski.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Pulaski")
    ?? throw new InvalidOperationException("Connection string 'Pulaski' is not configured.");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);
builder.Services.AddSingleton<ShopOrderPdfRenderer>();
builder.Services.AddSingleton<PickingListPdfRenderer>();
builder.Services.AddSingleton<LabelPdfRenderer>();
builder.Services.Configure<LabelPrintingOptions>(builder.Configuration.GetSection("Labels"));
builder.Services.AddSingleton<LabelTemplates>();
builder.Services.AddSingleton<LabelPrinting>();
builder.Services.AddScoped<LabelBuilder>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();
builder.Services.AddOpenApi();

// Sign-in (D-037): Pulaski's own tokens, also for people an OpenID Connect provider vouched for (D-091), whose real issuer is the 'idp' claim.
var security = builder.Configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
if (security.SigningKey.Length < 32)
{
    throw new InvalidOperationException("Security:SigningKey must be at least 32 characters; set it for this deployment and keep it secret.");
}

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));
builder.Services.AddSingleton<TokenIssuer>();
builder.Services.AddSingleton<IdentityProviderClient>();
builder.Services.AddHttpClient(IdentityProviderClient.ClientName, client => client.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<BadgeTickets>();
builder.Services.AddSingleton<Pulaski.Application.Labor.IBadgeHasher, BadgeHasher>();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(jwt =>
    {
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuers.Local,
            ValidAudience = SecurityOptions.Audience,
            IssuerSigningKey = TokenIssuer.Key(security),
            ClockSkew = TimeSpan.FromMinutes(1),
        };
    });
builder.Services.AddAuthorization();

// Schema version check and partition maintenance (D-028); tests without a database turn them off.
if (builder.Configuration.GetValue("Pulaski:DatabaseMaintenance", true))
{
    builder.Services.AddHostedService<SchemaVersionCheck>();
    builder.Services.AddHostedService<PartitionMaintenance>();
    builder.Services.AddHostedService<BootstrapAdministrator>();
    // One relay for every feed (D-061, #74), and the retention of what it delivered.
    builder.Services.AddHostedService<WebhookDeliveryService>();
    builder.Services.AddHostedService<DeliveryRetentionService>();
    builder.Services.AddHostedService<OeeRollupService>();
    builder.Services.AddHostedService<AllocationRefreshService>();
    builder.Services.AddHostedService<ConnectionScheduleService>();
}

// The ERP feed (D-061): its settings for screens, and the HTTP client it sends with.
builder.Services.AddSingleton<IDeliveryTargets, DeliveryTargets>();
builder.Services.AddSingleton<MqttFeedSender>();
builder.Services.AddSingleton<Pulaski.Application.Materials.IMaterialConnectionFetcher, ODataMaterialFetcher>();
builder.Services.AddHttpClient(ODataMaterialFetcher.ClientName, client => client.Timeout = TimeSpan.FromSeconds(60));
// Each attempt has its feed's own timeout (#73); the client's is only the longest a feed may set.
builder.Services.AddHttpClient(WebhookSender.ClientName, client => client.Timeout = TimeSpan.FromSeconds(125));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// Every call needs a signed-in user with a role (D-037); changes also need their permission.
var api = app.MapGroup("/api").AddEndpointFilter<SignedInFilter>();
api.MapSecurityEndpoints();
api.MapEquipmentEndpoints();
api.MapWorkOrderEndpoints();
api.MapReferenceDataEndpoints();
api.MapSchedulingEndpoints();
api.MapProcessEndpoints();
api.MapBomEndpoints();
api.MapUnitEndpoints();
api.MapInventoryEndpoints();
api.MapLabelEndpoints();
api.MapIntegrationEndpoints();
api.MapMaterialEndpoints();
api.MapExecutionEndpoints();
api.MapShiftEndpoints();
api.MapLaborEndpoints();
api.MapMachineDataEndpoints();
api.MapEdgeEndpoints();
api.MapMachineEndpoints();
api.MapOverviewEndpoints();
api.MapQualityEndpoints();
api.MapTerminalAdminEndpoints();

// Shop-floor terminals (D-049) are not users: they authenticate with their own secret, and only here.
app.MapGroup("/api/terminal").MapTerminalEndpoints();

app.Run();
