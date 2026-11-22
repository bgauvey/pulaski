using Pulaski.Contracts.Inbox;
using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Tests;

/// <summary>How the inbox reads (#123).</summary>
public class InboxViewTests
{
    private static InboxItemDto Item(string kind, string title) => new()
    {
        Kind = kind,
        Key = kind + title,
        Title = title,
        SiteId = Guid.NewGuid(),
        SiteCode = "PLT1",
        Link = "x",
    };

    [Fact]
    public void Items_group_by_kind_in_the_order_given_and_the_count_caps_at_99()
    {
        var groups = InboxView.Groups([Item("spc_alert", "a"), Item("labor", "b"), Item("spc_alert", "c")]);
        Assert.Equal(["spc_alert", "labor"], groups.Select(g => g.Kind));
        Assert.Equal(["a", "c"], groups[0].Items.Select(i => i.Title));
        Assert.Equal(("SPC alerts to close", "Labor to approve"), (InboxView.Heading("spc_alert"), InboxView.Heading("labor")));
        Assert.Equal(("7", "99+"), (InboxView.Badge(7), InboxView.Badge(140)));
    }
}
