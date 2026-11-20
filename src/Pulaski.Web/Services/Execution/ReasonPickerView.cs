using Pulaski.Contracts.Inventory;

namespace Pulaski.Web.Services.Execution;

/// <summary>Reasons laid out as large buttons for the touch layout (#119): one block per reason group, in the order given.</summary>
public static class ReasonPickerView
{
    public sealed record Group(string? Title, IReadOnlyList<ReasonCodeDto> Reasons);

    /// <summary>
    /// The reasons by the group they are filed under (D-062), groups in the order their first reason comes (the tree's order); reasons at
    /// the top, with no group, come first under no title.
    /// </summary>
    public static IReadOnlyList<Group> Groups(IEnumerable<ReasonCodeDto> reasons)
    {
        var groups = new List<(string? Title, List<ReasonCodeDto> Reasons)>();
        foreach (var reason in reasons)
        {
            var title = string.IsNullOrWhiteSpace(reason.GroupPath) ? null : reason.GroupPath;
            var index = groups.FindIndex(g => g.Title == title);
            if (index < 0)
            {
                groups.Add((title, [reason]));
            }
            else
            {
                groups[index].Reasons.Add(reason);
            }
        }

        return [.. groups.OrderBy(g => g.Title is null ? 0 : 1).Select(g => new Group(g.Title, g.Reasons))];
    }
}
