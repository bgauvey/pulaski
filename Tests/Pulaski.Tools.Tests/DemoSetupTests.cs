using Pulaski.DemoSetup;

namespace Pulaski.Tools.Tests;

/// <summary>What the demo setup binds and how it reads the simulators' state numbers (D-101).</summary>
public class DemoSetupTests
{
    [Fact]
    public void The_simulators_number_their_states_their_own_way()
    {
        Assert.Equal(1, DemoPlan.RawState(DemoPlan.ProfileCsv, "Idle"));
        Assert.Equal(2, DemoPlan.RawState(DemoPlan.ProfileCsv, "Stopped"));
        Assert.Equal(6, DemoPlan.RawState(DemoPlan.ProfileCsv, "Execute"));
        Assert.Null(DemoPlan.RawState(DemoPlan.ProfileCsv, "Nonsense"));
    }

    [Fact]
    public void Every_machine_reports_its_state_and_the_line_counts_at_one_machine()
    {
        Assert.All(DemoPlan.Machines, m => Assert.Contains(DemoPlan.Tags, t => t.EquipmentCode == m.Device && t.Role == "state"));
        Assert.DoesNotContain(DemoPlan.Tags, t => t.EquipmentCode == "EXT-01-EXT" && t.Role == "processed_count");
        Assert.Contains(DemoPlan.Tags, t => t.EquipmentCode == "EXT-01-SAW" && t.Role == "processed_count");
        Assert.Equal(
            ["Plant1/ExtruderProcess/EXT-01-EXT:Process/MeltTemperature", "Plant1/ExtruderProcess/EXT-01-EXT:Process/ProfileWidth"],
            DemoPlan.Tags.Where(t => t.Role == "process_value").Select(t => t.Address));
        Assert.Equal(DemoPlan.Tags.Count, DemoPlan.Tags.Select(t => (t.EquipmentCode, t.Role, t.Address)).Distinct().Count());
    }

    [Fact]
    public void A_new_password_meets_the_rules()
    {
        var password = DemoPlan.NewPassword();
        Assert.True(password.Length >= 10);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsDigit);
        Assert.NotEqual(password, DemoPlan.NewPassword());
    }
}
