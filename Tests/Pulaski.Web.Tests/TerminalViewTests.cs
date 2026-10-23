using Pulaski.Web.Services.Labor;

namespace Pulaski.Web.Tests;

/// <summary>The buttons of a shop-floor terminal and what it says afterwards (D-049).</summary>
public class TerminalViewTests
{
    [Fact]
    public void Buttons_post_back_as_actions_and_anything_else_is_ignored()
    {
        var line = Guid.CreateVersion7();
        var on = new TerminalAction(TerminalAction.ClockOn, line);

        Assert.Equal(on, TerminalAction.Parse(on.Value));
        Assert.Equal("clock-on", on.ApiAction);
        Assert.Equal("breaks/start", TerminalAction.Parse("paidbreak")!.ApiAction);
        Assert.Equal("breaks/end", TerminalAction.Parse("endbreak")!.ApiAction);
        Assert.Null(TerminalAction.Parse("done")!.ApiAction);
        Assert.Null(TerminalAction.Parse("on"));
        Assert.Null(TerminalAction.Parse("off:not-a-guid"));
        Assert.Null(TerminalAction.Parse("delete:" + line));
        Assert.Null(TerminalAction.Parse(null));
    }

    [Fact]
    public void The_terminal_says_what_it_did()
    {
        Assert.Equal("Ann, you're on EXT-01.", new TerminalAction(TerminalAction.ClockOn, Guid.CreateVersion7()).Confirmation("Ann", "EXT-01"));
        Assert.Equal("Ann, you're clocked off.", new TerminalAction(TerminalAction.ClockOff, Guid.CreateVersion7()).Confirmation("Ann", null));
        Assert.Equal("Welcome back, Ann.", new TerminalAction(TerminalAction.EndBreak, null).Confirmation("Ann", null));
        Assert.Equal("since 01:12", TerminalAction.Since(new DateTimeOffset(2026, 10, 23, 6, 12, 0, TimeSpan.Zero), "America/Chicago"));
    }
}
