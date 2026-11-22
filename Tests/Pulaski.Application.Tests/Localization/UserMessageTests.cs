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
}
