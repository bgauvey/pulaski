using Pulaski.Contracts.Setup;
using Pulaski.Web.Services;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>The Overview prompts for setup until the site is ready for its first job (#139).</summary>
public class SetupViewTests
{
    [Fact]
    public void The_prompt_says_how_far_setup_is_and_goes_once_ready()
    {
        var site = Guid.CreateVersion7();
        Assert.Equal("PLT1 setup: 6 of 8 steps for the first job done.",
            SetupView.Prompt(Text.English, new SetupChecklistDto { SiteId = site, SiteCode = "PLT1", RequiredDone = 6, RequiredTotal = 8 }));
        Assert.Equal("There is no site yet.", SetupView.Prompt(Text.English, new SetupChecklistDto()));
        Assert.Null(SetupView.Prompt(Text.English, new SetupChecklistDto { SiteId = site, Ready = true }));
        var step = new SetupStepDto { Key = "lines", Title = "Lines", Why = "", Href = "equipment", Permission = "equipment.view", Status = "done", Count = 3 };
        Assert.Equal("Done: 3", SetupView.Status(Text.English, step));
    }
}
