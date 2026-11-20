using Pulaski.Contracts.Inventory;
using Pulaski.Web.Services.Execution;

namespace Pulaski.Web.Tests;

/// <summary>Reasons as large buttons in the touch layout (#119).</summary>
public class ReasonPickerViewTests
{
    private static ReasonCodeDto Reason(string code, string? group) =>
        new() { Id = Guid.NewGuid(), Kind = "utilization", Code = code, Name = code, GroupPath = group };

    [Fact]
    public void Reasons_are_grouped_in_tree_order_with_the_ungrouped_first()
    {
        var groups = ReasonPickerView.Groups(
        [
            Reason("JAM", "Mechanical"),
            Reason("BREAK", null),
            Reason("BELT", "Mechanical"),
            Reason("WAIT-QA", "Quality › Waiting"),
            Reason("MEETING", null),
        ]);

        Assert.Equal([null, "Mechanical", "Quality › Waiting"], groups.Select(g => g.Title));
        Assert.Equal(["BREAK", "MEETING"], groups[0].Reasons.Select(r => r.Code));
        Assert.Equal(["JAM", "BELT"], groups[1].Reasons.Select(r => r.Code));
        Assert.Empty(ReasonPickerView.Groups([]));
    }
}
