using Pulaski.Web.Services.Instructions;

namespace Pulaski.Web.Tests;

/// <summary>An instruction's text reads as headings, steps, points and bold, and holds no markup of its own (#126a).</summary>
public class InstructionTextTests
{
    [Fact]
    public void Headings_steps_points_bold_and_paragraphs_become_html()
    {
        const string Text = "# Start up\n1. Heat the **zones**\n2. Start the screw\n\n- Wear gloves\nCheck the die.\nThen go.";
        Assert.Equal(
            "<h3>Start up</h3><ol><li>Heat the <strong>zones</strong></li><li>Start the screw</li></ol><ul><li>Wear gloves</li></ul><p>Check the die.<br>Then go.</p>",
            InstructionText.ToHtml(Text));
        Assert.Equal("<h4>Sub</h4>", InstructionText.ToHtml("## Sub"));
    }

    [Fact]
    public void Markup_in_the_text_is_shown_as_text()
    {
        Assert.Equal("<p>&lt;script&gt;alert(1)&lt;/script&gt; &amp; more</p>", InstructionText.ToHtml("<script>alert(1)</script> & more"));
        Assert.Equal("", InstructionText.ToHtml(null));
    }
}
