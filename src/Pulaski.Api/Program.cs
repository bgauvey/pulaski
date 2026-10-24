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
builder.Services.AddSingleton<LabelPdfRenderer>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();
builder.Services.AddOpenApi();

// Sign-in (D-037): Pulaski's own tokens now; an identity provider becomes another bearer scheme.
var security = builder.Configuration.GetSection(SecurityOptions.Section).Get<SecurityOptions>() ?? new SecurityOptions();
if (security.SigningKey.Length < 32)
{
    throw new InvalidOperationException("Security:SigningKey must be at least 32 characters; set it for this deployment and keep it secret.");
}

builder.Services.Configure<SecurityOptions>(builder.Configuration.GetSection(SecurityOptions.Section));
builder.Services.AddSingleton<TokenIssuer>();
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
}

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
api.MapMaterialEndpoints();
api.MapExecutionEndpoints();
api.MapShiftEndpoints();
api.MapLaborEndpoints();
api.MapMachineDataEndpoints();
api.MapEdgeEndpoints();
api.MapMachineEndpoints();
api.MapTerminalAdminEndpoints();

// Shop-floor terminals (D-049) are not users: they authenticate with their own secret, and only here.
app.MapGroup("/api/terminal").MapTerminalEndpoints();

app.Run();
