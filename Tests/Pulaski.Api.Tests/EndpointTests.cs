using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.WorkOrders;

namespace Pulaski.Api.Tests;

public sealed class EndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Get_missing_work_order_returns_404_problem()
    {
        var response = await _client.GetAsync(new Uri($"/api/work-orders/{Guid.CreateVersion7()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Invalid_equipment_returns_400_with_camel_case_field_errors()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/equipment", new CreateEquipmentRequest { Level = "site", EquipmentRole = "area" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ValidationProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("code", problem.Errors.Keys);
        Assert.Contains("equipmentRole", problem.Errors.Keys);
    }

    [Fact]
    public async Task Status_change_maps_rule_conflict_and_success()
    {
        var wo = factory.Stores.WorkOrders.Add("planned", version: 5);
        var uri = $"/api/work-orders/{wo.Id}/status";

        var invalid = await _client.PostAsJsonAsync(uri, new ChangeWorkOrderStatusRequest { Version = 5, Status = "closed" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);

        var ok = await _client.PostAsJsonAsync(uri, new ChangeWorkOrderStatusRequest { Version = 5, Status = "released" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(6, (await ok.Content.ReadFromJsonAsync<VersionResponse>())!.Version);

        var stale = await _client.PostAsJsonAsync(uri, new ChangeWorkOrderStatusRequest { Version = 5, Status = "cancelled" });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Create_work_order_returns_201_with_location()
    {
        var (_, site, _) = factory.Stores.Equipment.AddHierarchy();
        var response = await _client.PostAsJsonAsync("/api/work-orders", new CreateWorkOrderRequest
        {
            SiteId = site.Id,
            OrderNumber = "WO-NEW",
            MaterialId = factory.Stores.Materials.AddActive(),
            PlannedQty = 100,
            UomId = Guid.CreateVersion7(),
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<CreatedResponse>();
        Assert.Equal(7, created!.Id.Version);
        Assert.Equal($"/api/work-orders/{created.Id}", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Work_order_for_unknown_site_returns_422()
    {
        var response = await _client.PostAsJsonAsync("/api/work-orders", new CreateWorkOrderRequest
        {
            SiteId = Guid.CreateVersion7(),
            OrderNumber = "WO-X",
            MaterialId = factory.Stores.Materials.AddActive(),
            PlannedQty = 1,
            UomId = Guid.CreateVersion7(),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Work_order_list_includes_next_statuses()
    {
        factory.Stores.WorkOrders.Add("released");

        var rows = await _client.GetFromJsonAsync<List<WorkOrderDto>>("/api/work-orders");

        Assert.Contains(rows!, w => w.Status == "released" && w.NextStatuses.Contains("in_progress"));
    }
}
