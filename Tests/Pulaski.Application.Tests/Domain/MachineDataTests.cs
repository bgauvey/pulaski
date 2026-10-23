using Pulaski.Domain;
using Pulaski.Domain.Inventory;
using Pulaski.Domain.MachineData;

namespace Pulaski.Application.Tests.Domain;

/// <summary>PackML states, utilization classes and profile rules (D-050).</summary>
public class MachineDataTests
{
    [Theory]
    [InlineData("6", 6)]
    [InlineData("execute", 6)]
    [InlineData(" Suspended ", 5)]
    [InlineData("0", 0)]
    [InlineData("17", 17)]
    [InlineData("18", null)]
    [InlineData("-1", null)]
    [InlineData("Running", null)]
    public void A_PackML_state_is_read_from_its_number_or_its_name(string text, int? expected) =>
        Assert.Equal(expected, PackMlStates.Parse(text));

    [Fact]
    public void A_new_profile_has_PackMLs_own_numbers()
    {
        Assert.Equal(18, MachineProfileRules.StandardStates.Count);
        Assert.All(MachineProfileRules.StandardStates, s => Assert.Equal(s.RawValue, s.PackMlState));
        Assert.Equal((6, "Execute"), (MachineProfileRules.StandardStates[6].RawValue, MachineProfileRules.StandardStates[6].Name));
    }

    [Fact]
    public void Only_a_utilization_reason_has_a_class()
    {
        Assert.True(ReasonCode.IsValidClass("utilization", UtilizationClasses.Planned));
        Assert.False(ReasonCode.IsValidClass("utilization", null));
        Assert.False(ReasonCode.IsValidClass("utilization", "idle"));
        Assert.True(ReasonCode.IsValidClass("scrap", null));
        Assert.False(ReasonCode.IsValidClass("scrap", UtilizationClasses.Downtime));
    }

    [Fact]
    public void A_micro_stop_threshold_is_up_to_an_hour()
    {
        MachineProfileRules.EnsureMicroStop(TimeSpan.Zero);
        MachineProfileRules.EnsureMicroStop(TimeSpan.FromMinutes(60));
        Assert.Throws<DomainRuleException>(() => MachineProfileRules.EnsureMicroStop(TimeSpan.FromMinutes(61)));
        Assert.Throws<DomainRuleException>(() => MachineProfileRules.EnsureMicroStop(TimeSpan.FromSeconds(-1)));
        Assert.Equal(TimeSpan.FromMinutes(5), MachineProfileRules.DefaultMicroStop);
    }

    [Theory]
    [InlineData("PACKER-A", true)]
    [InlineData("EXT.V2_1", true)]
    [InlineData("packer", false)]
    [InlineData("-A", false)]
    [InlineData("A23456789012345678901", false)]
    public void A_profile_code_is_upper_case_letters_digits_and_marks(string code, bool valid) =>
        Assert.Equal(valid, MachineProfileRules.IsValidCode(code));
}
