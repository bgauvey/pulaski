using Pulaski.Web.Services;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Tests;

/// <summary>A refusal from the API shown from its template and values (#128).</summary>
public class MessageTextTests
{
    [Fact]
    public void A_message_is_filled_in_from_its_template_else_shown_as_sent()
    {
        var error = new ApiException(422, "Rule violated", "EXT-01 has 1234.5 kg left.", null)
        {
            Template = "{0} has {1:N1} kg left.",
            Values = ["EXT-01", 1234.5m],
        };
        Assert.Equal("EXT-01 has 1,234.5 kg left.", Text.English.Message(error));
        Assert.Equal("Rule violated", Text.English.Title(error));

        Assert.Equal("As sent.", Text.English.Message(new ApiException(422, "Rule violated", "As sent.", null) { Template = "{0} {1}", Values = ["one"] }));
        Assert.Equal("As sent.", Text.English.Message(new ApiException(422, "Rule violated", "As sent.", null)));
        Assert.Equal("Not found", Text.English.Problem(new ApiException(404, "Not found", null, null)));
        Assert.Null(Text.English.Message(null));
    }

    [Fact]
    public void Field_errors_are_filled_in_from_their_templates_else_shown_as_sent()
    {
        var error = new ApiException(400, "One or more fields are invalid.", null, new Dictionary<string, string[]>
        {
            ["code"] = ["At most 50 characters.", "Required."],
            ["note"] = ["Say why."],
        })
        {
            FieldMessages = new Dictionary<string, IReadOnlyList<(string Template, IReadOnlyList<object?> Values)>>
            {
                // The second message of code came without a template, and note's none at all: both show as sent.
                ["code"] = [("At most {0:N0} characters.", [1500m])],
            },
        };

        var fields = Text.English.FieldErrors(error);
        Assert.Equal(["At most 1,500 characters.", "Required."], fields["code"]);
        Assert.Equal(["Say why."], fields["note"]);
        Assert.Equal("At most 1,500 characters. Required. Say why.", Text.English.FieldErrorLine(error));
        Assert.Null(Text.English.FieldErrorLine(new ApiException(400, "One or more fields are invalid.", null, null)));
    }

    [Fact]
    public void A_note_is_filled_in_from_its_template_with_values_as_they_arrive_from_the_api()
    {
        // Values come back from JSON as elements: numbers stay numbers, for the reader's region.
        var values = System.Text.Json.JsonSerializer.Deserialize<object?[]>("""["J-0042", 1234.5]""")!;
        var note = new Pulaski.Contracts.Common.MessageDto("J-0042 has 1234.5 left.") { Template = "{0} has {1:N1} left.", Values = values };
        Assert.Equal("J-0042 has 1,234.5 left.", Text.English.Of(note));

        Assert.Equal("As sent.", Text.English.Of(new Pulaski.Contracts.Common.MessageDto("As sent.")));
        Assert.Equal("As sent.", Text.English.Of(new Pulaski.Contracts.Common.MessageDto("As sent.") { Template = "{0} {1}", Values = values[..1] }));
        Assert.Equal(["J-0042 has 1,234.5 left.", "As sent."], Text.English.Notes([note, new Pulaski.Contracts.Common.MessageDto("As sent.")]));
    }

    [Fact]
    public void A_message_inside_a_message_and_a_code_are_worded_on_their_own()
    {
        // #188: as the API writes them, a part is {template, values, text} and a code is {code}.
        var values = System.Text.Json.JsonSerializer.Deserialize<object?[]>(
            """["J-1", {"code": "on_hold"}, {"template": "valid only from {0}", "values": ["2026-11-01"], "text": "valid only from 2026-11-01"}]""")!;
        var note = new Pulaski.Contracts.Common.MessageDto("J-1 is on hold: valid only from 2026-11-01.") { Template = "{0} is {1}: {2}.", Values = values };
        Assert.Equal("J-1 is on hold: valid only from 2026-11-01.", Text.English.Of(note));

        // A part that doesn't fill shows its own English.
        var broken = System.Text.Json.JsonSerializer.Deserialize<object?[]>("""[{"template": "{0} {1}", "values": [], "text": "As sent."}]""")!;
        Assert.Equal("Why: As sent.", Text.English.Of(new Pulaski.Contracts.Common.MessageDto("Why: As sent.") { Template = "Why: {0}", Values = broken }));
    }

    [Fact]
    public void A_code_mid_sentence_starts_lower_case()
    {
        Assert.Equal("on hold", Text.English.Word("on_hold"));
        Assert.Equal("released", Text.English.Word("released"));
    }
}
