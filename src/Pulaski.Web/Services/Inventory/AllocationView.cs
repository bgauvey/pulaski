using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services.Inventory;

/// <summary>How the screens word allocation changes (D-064).</summary>
public static class AllocationView
{
    /// <summary>"2 allocated, 1 trimmed, 1 released, across 3 jobs", or "Nothing to change."</summary>
    public static string Summary(AllocationChangeResponse result)
    {
        List<string> parts = [];
        if (result.Allocated > 0)
        {
            parts.Add($"{result.Allocated} allocated");
        }

        if (result.Trimmed > 0)
        {
            parts.Add($"{result.Trimmed} trimmed");
        }

        if (result.Released > 0)
        {
            parts.Add($"{result.Released} released");
        }

        if (parts.Count == 0)
        {
            return result.Jobs > 1 ? $"Nothing to change across {result.Jobs} jobs." : "Nothing to change.";
        }

        return string.Join(", ", parts) + (result.Jobs > 1 ? $", across {result.Jobs} jobs." : ".");
    }
}
