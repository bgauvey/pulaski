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
}
