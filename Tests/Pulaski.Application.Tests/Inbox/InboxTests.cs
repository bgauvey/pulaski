using Microsoft.Extensions.DependencyInjection;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Inbox;
using Pulaski.Contracts.Inbox;
using Pulaski.Domain.Security;
using Pulaski.Testing;

namespace Pulaski.Application.Tests.Inbox;

/// <summary>One inbox (#123): what waits for a user, only where they may decide it.</summary>
public class InboxTests
{
    private static readonly Guid SiteA = Guid.CreateVersion7();
    private static readonly Guid SiteB = Guid.CreateVersion7();

    private static InboxItemDto Item(string kind, Guid site, string title, int minutesAgo) => new()
    {
        Kind = kind,
        Key = $"{kind}:{title}",
        Title = title,
        SiteId = site,
        SiteCode = site == SiteA ? "A" : "B",
        Since = HandlerFixture.Now.AddMinutes(-minutesAgo),
        Link = "somewhere",
    };

    private static UserAccess User(params (Guid Site, string[] Permissions)[] grants) => new(
        Guid.CreateVersion7(), "u", "U", "u", true, [.. grants.Select(g => new RoleGrant(Guid.CreateVersion7(), "custom", "Custom", g.Site, g.Permissions))]);

    [Fact]
    public async Task Each_user_sees_what_they_may_decide_at_each_site_oldest_first_and_nothing_else()
    {
        var stores = new InMemoryStores();
        stores.Inbox.Sites.AddRange([(SiteA, "A"), (SiteB, "B")]);
        stores.Inbox.Items.AddRange(
        [
            Item(InboxKinds.SpcAlert, SiteA, "newer alert", 5),
            Item(InboxKinds.SpcAlert, SiteA, "older alert", 50),
            Item(InboxKinds.SpcAlert, SiteB, "alert at B", 10),
            Item(InboxKinds.PartialBatch, SiteA, "batch at A", 20),
            Item(InboxKinds.ErpChange, SiteB, "change at B", 30),
        ]);
        var services = new ServiceCollection().AddApplication();
        services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider(HandlerFixture.Now));
        services.AddSingleton<IRequestContext, FixedRequestContext>();
        stores.Register(services);
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var inbox = scope.ServiceProvider.GetRequiredService<IQueryHandler<GetInboxQuery, InboxDto>>();

        // Quality at A, work orders at B: A's alerts (oldest first) and B's change; not B's alert, not the batch.
        var mixed = await inbox.HandleAsync(new GetInboxQuery(User((SiteA, [Permissions.QualityRecord]), (SiteB, [Permissions.WorkOrderManage]))), default);
        Assert.Equal(["older alert", "newer alert", "change at B"], mixed.Items.Select(i => i.Title));

        // Someone who decides nothing of these has an empty inbox.
        Assert.Empty((await inbox.HandleAsync(new GetInboxQuery(User((SiteA, [Permissions.FloorView]))), default)).Items);
    }
}
