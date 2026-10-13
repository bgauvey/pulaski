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

app.Run();
