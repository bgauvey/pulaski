using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Pulaski.Application.Inventory;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Inventory;

namespace Pulaski.Api.Tests;

/// <summary>Inventory routes and their problem responses (D-027).</summary>
public sealed class InventoryEndpointTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Receive_write_off_and_consume_map_to_created_ok_conflict_and_rule()
    {
        var stores = factory.Stores;
        var kg = stores.Units.AddUnit("kg", "mass", 1m);
        var site = Guid.CreateVersion7();
        var location = stores.Inventory.AddLocation("RM-STORE", site);
        var material = stores.Inventory.AddMaterial("PVC-K67", kg.Id, "kg");
        var reason = stores.Inventory.AddReason(ReasonKinds.InventoryAdjustment, "DAMAGE");
        var closed = stores.Inventory.AddJob("WO-1-10", site, Guid.CreateVersion7(), status: "closed");

        var receive = await _client.PostAsJsonAsync("/api/lots", new ReceiveLotRequest
        {
            MaterialId = material.Id,
            LocationId = location.Id,
            LotNumber = "R-1",
            Quantity = 100m,
            UomId = kg.Id,
        });
        Assert.Equal(HttpStatusCode.Created, receive.StatusCode);
        var lot = (await receive.Content.ReadFromJsonAsync<LotTransactionResponse>())!;
        Assert.Equal($"/api/lots/{lot.LotId}", receive.Headers.Location?.OriginalString);

        var missingNumber = await _client.PostAsJsonAsync("/api/lots", new ReceiveLotRequest
        {
            MaterialId = material.Id,
            LocationId = location.Id,
            Quantity = 1m,
            UomId = kg.Id,
        });
        Assert.Equal(HttpStatusCode.BadRequest, missingNumber.StatusCode);
        Assert.Contains("lotNumber", (await missingNumber.Content.ReadFromJsonAsync<ValidationProblemDetails>())!.Errors.Keys);

        var off = await _client.PostAsJsonAsync($"/api/lots/{lot.LotId}/write-off", new AdjustLotRequest
        {
            Version = lot.Version,
            Quantity = 10m,
            UomId = kg.Id,
            ReasonCodeId = reason.Id,
        });
        Assert.Equal(HttpStatusCode.OK, off.StatusCode);
        Assert.Equal(90m, (await off.Content.ReadFromJsonAsync<LotTransactionResponse>())!.Quantity);

        var stale = await _client.PostAsJsonAsync($"/api/lots/{lot.LotId}/write-off", new AdjustLotRequest
        {
            Version = lot.Version,
            ReasonCodeId = reason.Id,
        });
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var consume = await _client.PostAsJsonAsync($"/api/lots/{lot.LotId}/consume", new ConsumeLotRequest
        {
            JobId = closed.Id,
            Quantity = 1m,
            UomId = kg.Id,
        });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, consume.StatusCode);

        var reasons = await _client.GetFromJsonAsync<IReadOnlyList<ReasonCodeDto>>($"/api/reason-codes?kind=inventory_adjustment&siteId={site}");
        Assert.Equal("DAMAGE", Assert.Single(reasons!).Code);

        var missing = await _client.GetAsync(new Uri($"/api/lots/{Guid.CreateVersion7()}", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_reason_moves_under_a_group_and_a_move_under_a_reason_is_refused()
    {
        var site = Guid.CreateVersion7();
        factory.Stores.Inventory.AddSite(site);
        var group = await (await _client.PostAsJsonAsync("/api/reason-codes", new CreateReasonCodeRequest
        {
            SiteId = site,
            Kind = "hold",
            Code = "QA-GROUP",
            Name = "Quality",
            IsGroup = true,
        })).Content.ReadFromJsonAsync<CreatedResponse>();
        var reason = await (await _client.PostAsJsonAsync("/api/reason-codes", new CreateReasonCodeRequest
        {
            SiteId = site,
            Kind = "hold",
            Code = "QA-LAB",
            Name = "Waiting for the lab",
        })).Content.ReadFromJsonAsync<CreatedResponse>();

        var moved = await _client.PutAsJsonAsync($"/api/reason-codes/{reason!.Id}/parent", new MoveReasonCodeRequest { Version = reason.Version, ParentId = group!.Id });
        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);
        var refused = await _client.PutAsJsonAsync($"/api/reason-codes/{group.Id}/parent", new MoveReasonCodeRequest { Version = group.Version, ParentId = reason.Id });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
    }
}
