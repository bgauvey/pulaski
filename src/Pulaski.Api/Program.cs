using Pulaski.Api.Endpoints;
using Pulaski.Api.Infrastructure;
using Pulaski.Application;
using Pulaski.Application.Abstractions;
using Pulaski.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Pulaski")
    ?? throw new InvalidOperationException("Connection string 'Pulaski' is not configured.");

builder.Services.AddApplication();
builder.Services.AddInfrastructure(connectionString);

builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApplicationExceptionHandler>();
builder.Services.AddOpenApi();

// Schema version check and partition maintenance (D-028); tests without a database turn them off.
if (builder.Configuration.GetValue("Pulaski:DatabaseMaintenance", true))
{
    builder.Services.AddHostedService<SchemaVersionCheck>();
    builder.Services.AddHostedService<PartitionMaintenance>();
}

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

var api = app.MapGroup("/api");
api.MapEquipmentEndpoints();
api.MapWorkOrderEndpoints();
api.MapReferenceDataEndpoints();
api.MapSchedulingEndpoints();
api.MapProcessEndpoints();
api.MapBomEndpoints();
api.MapUnitEndpoints();
api.MapInventoryEndpoints();
api.MapMaterialEndpoints();

app.Run();
