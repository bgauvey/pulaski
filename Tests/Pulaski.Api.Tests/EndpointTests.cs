using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Pulaski.Contracts.Common;
using Pulaski.Application.Scheduling;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Scheduling;
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

    [Fact]
    public async Task Line_timeline_returns_projected_segments_and_maps_errors()
    {
        var line = factory.Stores.Scheduling.Add(new LinePlanSource
        {
            Line = new SchedulingLineRow { Id = Guid.CreateVersion7(), Code = "EXT-01", Name = "Line 1", IsSchedulable = true, ChangeoverMode = "auto" },
            Jobs =
            [
                new PlanJobRow { JobId = Guid.CreateVersion7(), JobNumber = "J-1", JobType = "maintenance", Status = "planned", PlannedDuration = TimeSpan.FromHours(2) },
            ],
        }).Line;

        var timeline = await _client.GetFromJsonAsync<LineTimelineDto>($"/api/scheduling/lines/{line.Id}/timeline");

        var segment = Assert.Single(timeline!.Segments);
        Assert.Equal(("J-1", TimeSpan.FromHours(2)), (segment.JobNumber, segment.End - segment.Start));
        Assert.Equal("no_shift_calendar", Assert.Single(timeline.Warnings).Code);

        var missing = await _client.GetAsync(new Uri($"/api/scheduling/lines/{Guid.CreateVersion7()}/timeline", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        var badHorizon = await _client.GetAsync(new Uri($"/api/scheduling/lines/{line.Id}/timeline?horizonDays=0", UriKind.Relative));
        Assert.Equal(HttpStatusCode.BadRequest, badHorizon.StatusCode);
    }

    [Fact]
    public async Task Scheduling_commands_map_to_created_ok_conflict_and_not_found()
    {
        var wo = factory.Stores.WorkOrders.Add("released");
        var line = factory.Stores.Scheduling.AddLine(wo.SiteId);

        var create = await _client.PostAsJsonAsync("/api/scheduling/jobs", new CreateJobRequest
        {
            LineId = line.Id,
            JobNumber = "J-1",
            JobType = "production",
            WorkOrderId = wo.Id,
            PlannedQty = 10m,
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var created = (await create.Content.ReadFromJsonAsync<CreatedResponse>())!;
        Assert.Equal($"/api/scheduling/jobs/{created.Id}", create.Headers.Location?.OriginalString);

        var pin = await _client.PostAsJsonAsync(
            $"/api/scheduling/jobs/{created.Id}/pin", new PinJobRequest { Version = created.Version, IsLocked = true });
        Assert.Equal(HttpStatusCode.OK, pin.StatusCode);

        var stale = await _client.PostAsJsonAsync(
            $"/api/scheduling/jobs/{created.Id}/dispatch", new DispatchJobRequest { Version = created.Version });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var published = await _client.PostAsJsonAsync($"/api/scheduling/lines/{line.Id}/publish", new PublishScheduleRequest());
        Assert.Equal(HttpStatusCode.OK, published.StatusCode);
        Assert.Equal(created.Id, Assert.Single((await published.Content.ReadFromJsonAsync<PublishScheduleResponse>())!.Jobs).JobId);

        var policy = await _client.GetFromJsonAsync<SchedulingPolicyDto>($"/api/scheduling/sites/{wo.SiteId}/policy");
        Assert.True(policy!.IsDefault);

        var missing = await _client.GetAsync(new Uri($"/api/scheduling/sites/{Guid.CreateVersion7()}/policy", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }
}
