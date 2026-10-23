using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Shifts;
using Pulaski.Web.Services;

namespace Pulaski.Web.Tests;

/// <summary>How the Site settings screen explains and checks its settings (D-043).</summary>
public class SiteSettingsViewTests
{
    [Theory]
    [InlineData(null, "Enter a percentage.")]
    [InlineData(99.9, "At least 100%")]
    [InlineData(1000.1, "At most 1000%.")]
    public void Tolerances_outside_100_to_1000_percent_are_refused(double? value, string problem) =>
        Assert.StartsWith(problem, SiteSettingsView.ToleranceProblem((decimal?)value), StringComparison.Ordinal);

    [Fact]
    public void The_examples_show_what_the_tolerance_allows()
    {
        Assert.Null(SiteSettingsView.ToleranceProblem(110m));
        Assert.Contains("up to 2,200 m", SiteSettingsView.RateExample(110m), StringComparison.Ordinal);
        Assert.Contains("more is refused", SiteSettingsView.RateExample(110m), StringComparison.Ordinal);
        Assert.Contains("up to 625 kg", SiteSettingsView.ConsumptionExample(125m), StringComparison.Ordinal);
        Assert.Contains("with a warning", SiteSettingsView.ConsumptionExample(125m), StringComparison.Ordinal);
        Assert.Equal("", SiteSettingsView.RateExample(50m));
    }

    [Fact]
    public void Exceptions_apply_to_the_site_its_areas_and_the_equipment_planning_uses()
    {
        var site = Guid.CreateVersion7();
        EquipmentDto E(string code, string level, bool schedulable = false, bool active = true, Guid? siteId = null, int depth = 2) => new()
        {
            Id = level == "site" ? site : Guid.CreateVersion7(),
            SiteId = siteId ?? site,
            Code = code,
            Name = code,
            Level = level,
            EquipmentRole = level,
            IsSchedulable = schedulable,
            IsActive = active,
            Depth = level == "site" ? 1 : depth,
        };

        var scopes = SiteSettingsView.Scopes(
            [E("EXT-01", "work_center", schedulable: true, depth: 3), E("STORE", "storage_zone"), E("PLT1", "site"), E("EXTRUSION", "area"),
             E("OLD", "work_center", schedulable: true, active: false), E("ELSEWHERE", "area", siteId: Guid.CreateVersion7())],
            site);

        Assert.Equal(["PLT1", "EXTRUSION", "EXT-01"], scopes.Select(s => s.Code));
    }

    [Fact]
    public void An_exception_reads_as_its_window_in_site_time_and_whether_it_is_over()
    {
        var start = new DateTimeOffset(2026, 12, 25, 12, 0, 0, TimeSpan.Zero);
        var holiday = new CalendarExceptionDto
        {
            Id = Guid.CreateVersion7(),
            SiteId = Guid.CreateVersion7(),
            ScopeEquipmentId = Guid.CreateVersion7(),
            Kind = "holiday",
            StartsAt = start,
            EndsAt = start.AddDays(3),
            IsActive = true,
        };

        Assert.Equal("Fri 25 Dec 2026 06:00 – Mon 28 Dec 2026 06:00 (3 days)", SiteSettingsView.Window(holiday, "America/Chicago"));
        Assert.Equal(("Coming", "Now", "Past"), (SiteSettingsView.When(holiday, start.AddHours(-1)), SiteSettingsView.When(holiday, start), SiteSettingsView.When(holiday, start.AddDays(3))));
        Assert.Equal("Planned shutdown", SiteSettingsView.KindLabel("planned_shutdown"));
    }
}
