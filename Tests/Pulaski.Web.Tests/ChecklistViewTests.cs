using System.Globalization;
using Pulaski.Contracts.Instructions;
using Pulaski.Web.Services.Instructions;

namespace Pulaski.Web.Tests;

/// <summary>Checklist answers and draft items are sent as the API reads them, whatever the reader's culture (#126c).</summary>
public class ChecklistViewTests
{
    [Fact]
    public void Numbers_typed_in_the_readers_culture_are_sent_invariant_and_blanks_left_out()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            IReadOnlyList<ChecklistItemDto> items = [new() { Kind = "number" }, new() { Kind = "text" }, new() { Kind = "yes_no" }];
            var sent = ChecklistView.ToSend(items, new Dictionary<int, string?> { [1] = "205,5", [2] = "  ", [3] = "yes" });
            Assert.Equal(new Dictionary<int, string?> { [1] = "205.5", [3] = "yes" }, sent);

            var dto = new ChecklistDraftItem { Prompt = "Die", Kind = "number", Min = "180,5", Max = "x", Unit = "°C", Expected = "yes" }.ToDto();
            Assert.Equal((180.5m, (decimal?)null, "°C", (bool?)null), (dto.Min!.Value, dto.Max, dto.Unit, dto.Expected));
            var choice = new ChecklistDraftItem { Kind = "choice", Choices = " Good \n\nMarks " }.ToDto();
            Assert.Equal(["Good", "Marks"], choice.Choices!);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }
}
