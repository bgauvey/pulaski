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

    [Fact]
    public void The_charts_limits_are_three_sigma_of_the_simulators_design()
    {
        var melt = DemoPlan.Characteristics.Single(c => c.Code == "MELT-TEMP").Limits();

        Assert.Equal(215.0, melt.Center);
        Assert.Equal(218.6, melt.Ucl, 2);
        Assert.Equal(211.4, melt.Lcl, 2);
        Assert.Equal(1.128 * 1.2, melt.MovingRange, 3);
        Assert.Equal(3.267 * 1.128 * 1.2, melt.MovingRangeUcl, 3);
    }

    [Fact]
    public void The_setup_charts_what_the_simulator_publishes_with_its_design()
    {
        foreach (var variable in Pulaski.ProcessSimulator.ProcessModel.Extruder)
        {
            var plan = DemoPlan.Characteristics.Single(c => c.TagAddress.EndsWith(":" + variable.Metric, StringComparison.Ordinal));
            Assert.Equal((variable.Target, variable.Sigma, variable.Decimals), (plan.Target, plan.Sigma, plan.Decimals));
            Assert.Contains(DemoPlan.Tags, t => t.Address == plan.TagAddress && t.Role == "process_value");
        }
    }

    [Fact]
    public void Melt_temperature_is_capable_and_the_profile_width_marginal()
    {
        static double Cpk(DemoPlan.Characteristic c) => Math.Min((double)c.Usl - c.Target, c.Target - (double)c.Lsl) / (3 * c.Sigma);

        Assert.True(Cpk(DemoPlan.Characteristics.Single(c => c.Code == "MELT-TEMP")) >= 1.33);
        var width = Cpk(DemoPlan.Characteristics.Single(c => c.Code == "PROFILE-WIDTH"));
        Assert.InRange(width, 1.00, 1.33);
    }
}
