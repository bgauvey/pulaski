using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Inventory;

/// <summary>How the screens word allocation changes (D-064).</summary>
public static class AllocationView
{
    /// <summary>"2 allocated, 1 trimmed, 1 released, across 3 jobs", or "Nothing to change."</summary>
    public static string Summary(Text l, AllocationChangeResponse result)
    {
        List<string> parts = [];
        if (result.Allocated > 0)
        {
            parts.Add(l["{0} allocated", result.Allocated]);
        }

        if (result.Trimmed > 0)
        {
            parts.Add(l["{0} trimmed", result.Trimmed]);
        }

        if (result.Released > 0)
        {
            parts.Add(l["{0} released", result.Released]);
        }

        if (parts.Count == 0)
        {
            return result.Jobs > 1 ? l["Nothing to change across {0} jobs.", result.Jobs] : l["Nothing to change."];
        }

        return string.Join(", ", parts) + (result.Jobs > 1 ? l[", across {0} jobs.", result.Jobs] : ".");
    }
}
