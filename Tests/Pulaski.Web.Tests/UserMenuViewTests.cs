using Pulaski.Web.Services.Security;

namespace Pulaski.Web.Tests;

public sealed class UserMenuViewTests
{
    [Theory]
    [InlineData("Jo Smith", "JS")]
    [InlineData("María José de la Cruz", "MC")]
    [InlineData("admin", "A")]
    [InlineData("  ", "?")]
    [InlineData(null, "?")]
    [InlineData("Line 2 (night)", "L2")]
    public void The_button_shows_the_first_and_last_initials(string? name, string initials) =>
        Assert.Equal(initials, UserMenuView.Initials(name));
}
