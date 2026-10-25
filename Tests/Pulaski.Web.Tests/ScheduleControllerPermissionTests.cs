using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Pulaski.Contracts.WorkOrders;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Scheduling;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Tests;

/// <summary>The schedule holds back what the user may not do at the site it shows (D-037, #47), before anything reaches the API.</summary>
public class ScheduleControllerPermissionTests
{
    private static readonly Guid PlantA = Guid.CreateVersion7();
    private static readonly Guid PlantB = Guid.CreateVersion7();

    private sealed class Counting : HttpMessageHandler
    {
        public List<string> Posts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Get)
            {
                Posts.Add(request.RequestUri!.AbsolutePath);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(request.Method == HttpMethod.Get ? "[]" : """{"version":1}""", System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Anonymous : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(new ClaimsPrincipal()));
    }

    private sealed class Nowhere : NavigationManager
    {
        public Nowhere() => Initialize("http://localhost/", "http://localhost/");

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
        }
    }

    // Plans at PLT1 only; manages work orders at PLT2 only.
    private static (ScheduleController Controller, Counting Http) Controller()
    {
        var http = new Counting();
        var api = new PulaskiApiClient(new HttpClient(http) { BaseAddress = new Uri("http://api/") }, new ApiSession(new Anonymous(), new Nowhere()));
        var controller = new ScheduleController(api, TimeProvider.System)
        {
            CanPlanAt = id => id == PlantA,
            CanManageOrdersAt = id => id == PlantB,
        };
        return (controller, http);
    }

    private static WorkOrderDto Order(Guid siteId) => new()
    {
        Id = Guid.CreateVersion7(),
        SiteId = siteId,
        SiteCode = "PLT",
        OrderNumber = "WO-1",
        MaterialId = Guid.CreateVersion7(),
        MaterialCode = "M",
        MaterialName = "M",
        UomId = Guid.CreateVersion7(),
        UomCode = "kg",
        Status = "planned",
        Origin = "manual",
        NextStatuses = ["dispatched"],
    };

    [Fact]
    public async Task Planning_and_managing_orders_follow_the_site_shown()
    {
        var (controller, _) = Controller();
        Assert.Equal((true, true), (controller.ReadOnly, controller.OrdersReadOnly));

        await controller.SelectSiteAsync(PlantA);
        Assert.Equal((false, true), (controller.ReadOnly, controller.OrdersReadOnly));

        await controller.SelectSiteAsync(PlantB);
        Assert.Equal((true, false), (controller.ReadOnly, controller.OrdersReadOnly));
    }

    [Fact]
    public async Task A_status_change_the_user_may_not_make_says_why_and_sends_nothing()
    {
        var (controller, http) = Controller();
        await controller.SelectSiteAsync(PlantA);

        var order = Order(PlantA);
        await controller.MoveWorkOrderAsync(order, "dispatched");

        Assert.Empty(http.Posts);
        Assert.Equal(ScheduleController.OrdersReadOnlyMessage, Assert.Single(controller.Toasts).Text);

        // Planning is allowed here, so the order still drags (onto a line on the Gantt).
        controller.StartDrag(order);
        Assert.Same(order, controller.DraggingWorkOrder);
    }

    [Fact]
    public async Task Where_the_user_may_manage_orders_the_status_change_goes_to_the_api()
    {
        var (controller, http) = Controller();
        await controller.SelectSiteAsync(PlantB);

        await controller.MoveWorkOrderAsync(Order(PlantB), "dispatched");

        Assert.Single(http.Posts);
    }
}
