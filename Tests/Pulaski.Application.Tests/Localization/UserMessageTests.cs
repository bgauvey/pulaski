using Pulaski.Application.Errors;
using Pulaski.Domain;

namespace Pulaski.Application.Tests.Localization;

/// <summary>Messages for people keep their template and values (#128).</summary>
public class UserMessageTests
{
    [Fact]
    public void An_interpolated_message_keeps_its_template_and_values_and_reads_as_before()
    {
        var code = "EXT-01";
        var left = 1234.5m;
        var error = new DomainRuleException($"{code} has {left:N1} kg left {{in}} {3,3}.");
        Assert.Equal("EXT-01 has 1,234.5 kg left {in}   3.", error.Message);
        Assert.Equal("{0} has {1:N1} kg left {{in}} {2,3}.", error.UserMessage.Template);
        Assert.Equal(["EXT-01", 1234.5m, 3], error.UserMessage.Values);
        Assert.Equal(error.Message, string.Format(System.Globalization.CultureInfo.InvariantCulture, error.UserMessage.Template, [.. error.UserMessage.Values]));
    }

    [Fact]
    public void Interpolated_parts_joined_with_plus_are_one_template()
    {
        var (a, b) = ("EXT-01", 7);
        var error = new DomainRuleException(
            $"{a} is more " +
            $"than {b}.");
        Assert.Equal(("{0} is more than {1}.", "EXT-01 is more than 7."), (error.UserMessage.Template, error.Message));
    }

    [Fact]
    public void A_plain_message_is_its_own_template()
    {
        var error = new NotFoundException("No {such} job.");
        Assert.Equal(("No {such} job.", "No {{such}} job."), (error.Message, error.UserMessage.Template));
        Assert.Empty(error.UserMessage.Values);
        Assert.Equal("A 5 b", new ConflictException($"A {5} b").Message);
        Assert.Equal("Here", new ForbiddenException($"Here").Message);
    }

    [Fact]
    public void A_message_or_code_inside_a_message_stays_one_for_the_screen_to_word()
    {
        // #188: the screen translates the part and words the code; the English reads as before.
        var why = UserMessage.Of("not held");
        var error = new DomainRuleException($"Job {"J-1"} is {UserMessage.Code("on_hold")}: {why}.");
        Assert.Equal("Job J-1 is on hold: not held.", error.Message);
        Assert.Equal("Job {0} is {1}: {2}.", error.UserMessage.Template);
        Assert.Equal(["J-1", new MessageCode("on_hold"), why], error.UserMessage.Values);
    }

    [Fact]
    public void Joined_messages_are_each_their_own_value()
    {
        UserMessage[] parts = [UserMessage.From($"Shift {"A"} overlaps."), UserMessage.Of("Pick {another}.")];
        var joined = UserMessage.Join(parts);
        Assert.Equal(("{0} {1}", "Shift A overlaps. Pick {another}."), (joined.Template, joined.Text));
        Assert.Equal(parts, joined.Values);
        Assert.Equal(("{0}; {1}", "Shift A overlaps.; Pick {another}."), (UserMessage.Join(parts, "; ").Template, UserMessage.Join(parts, "; ").Text));
        Assert.Same(parts[0], UserMessage.Join(parts[..1]));
    }

    [Fact]
    public void A_field_check_takes_a_message_worked_out_elsewhere()
    {
        var problem = UserMessage.From($"Use at least {12} characters.");
        var thrown = Assert.Throws<ValidationException>(() => new Validator().Require("Password", problem).Require("Name", null).ThrowIfInvalid());
        Assert.Equal(["Use at least 12 characters."], thrown.Errors["Password"]);
        Assert.Equal("Use at least {0} characters.", thrown.Messages["Password"][0].Template);
        Assert.False(thrown.Errors.ContainsKey("Name"));
    }
}
