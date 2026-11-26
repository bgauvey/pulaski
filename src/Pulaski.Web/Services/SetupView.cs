using Pulaski.Contracts.Setup;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services;

/// <summary>How the setup checklist reads (#139). Pure.</summary>
public static class SetupView
{
    public const string Done = "done";
    public const string ToDo = "to_do";

    public static string Chip(string status) => status switch
    {
        Done => "chip chip-live",
        ToDo => "chip chip-warning",
        _ => "chip",
    };

    /// <summary>"Done: 3", "To do", "Not needed".</summary>
    public static string Status(Text l, SetupStepDto step) => step.Status switch
    {
        Done => step.Count > 1 ? l["Done: {0}", step.Count] : l["Done"],
        ToDo => l["To do"],
        _ => l["Not needed"],
    };

    /// <summary>The Overview's prompt while the site isn't set up: "PLT1 setup: 6 of 8 steps for the first job done."; null once ready.</summary>
    public static string? Prompt(Text l, SetupChecklistDto checklist) =>
        checklist.Ready ? null
        : checklist.SiteId is null ? l["There is no site yet."]
        : l["{0} setup: {1} of {2} steps for the first job done.", checklist.SiteCode ?? "", checklist.RequiredDone, checklist.RequiredTotal];
}
