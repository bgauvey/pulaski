using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Materials;
using Pulaski.Web.Services.Materials;

namespace Pulaski.Web.Tests;

/// <summary>The capability form: site dates, and lines a capability can go on (D-034).</summary>
public class CapabilityFormTests
{
    private const string Chicago = "America/Chicago";

    private static readonly MaterialCapabilityDto Existing = new()
    {
        Id = Guid.CreateVersion7(),
        EquipmentId = Guid.CreateVersion7(),
        EquipmentCode = "EXT-01",
        EquipmentName = "Extrusion line 1",
        SiteTimeZone = Chicago,
        IdealRatePerHour = 900m,
        RateUomId = Guid.CreateVersion7(),
        RateUomCode = "m",
        StandardChangeover = TimeSpan.FromMinutes(45),
        EffectiveFrom = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        Version = 7,
    };

    [Fact]
    public void Dates_left_alone_keep_the_stored_instant_and_new_ones_start_the_site_day()
    {
        var form = CapabilityForm.For(Existing);
        Assert.Equal((new DateTime(2025, 12, 31), 45), (form.From, form.ChangeoverMinutes));
        Assert.Equal("900", CapabilityForm.Trim(900.000000m).ToString(System.Globalization.CultureInfo.InvariantCulture));

        form.IdealRatePerHour = 950m;
        var kept = form.ToUpdate(Chicago);
        Assert.Equal((Existing.EffectiveFrom, (DateTimeOffset?)null, 7L, 950m), (kept.EffectiveFrom, kept.EffectiveTo, kept.Version, kept.IdealRatePerHour));

        form.EndsOn = new DateTime(2026, 11, 1);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-5)), form.ToUpdate(Chicago).EffectiveTo);
    }

    [Fact]
    public void A_new_form_starts_today_in_the_base_unit_and_needs_a_line_and_a_rate()
    {
        var kg = Guid.CreateVersion7();
        var form = CapabilityForm.New(kg, new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero), Chicago);

        Assert.Equal((new DateTime(2026, 10, 16), kg, true), (form.From, form.RateUomId, form.IsNew));
        Assert.False(form.CanSubmit);
        form.EquipmentId = Existing.EquipmentId;
        form.IdealRatePerHour = 100m;
        Assert.True(form.CanSubmit);

        var now = new DateTimeOffset(2026, 10, 17, 3, 0, 0, TimeSpan.Zero);
        Assert.Null(form.ToAdd(Chicago, now).EffectiveFrom);
        form.From = new DateTime(2026, 10, 20);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.FromHours(-5)), form.ToAdd(Chicago, now).EffectiveFrom);
    }

    [Fact]
    public void Only_active_equipment_that_runs_jobs_is_offered_with_its_site_and_time_zone()
    {
        var site = new EquipmentDto { Id = Guid.CreateVersion7(), Code = "PLT1", Name = "Plant", Level = "site", EquipmentRole = "site", TimeZone = Chicago, IsActive = true };
        EquipmentDto Line(string code, bool runs = true, bool active = true) => new()
        {
            Id = Guid.CreateVersion7(),
            SiteId = site.Id,
            Code = code,
            Name = code,
            Level = "work_center",
            EquipmentRole = "production_line",
            CanRunJob = runs,
            IsActive = active,
        };
        var b = Line("EXT-02");
        var equipment = new[] { site, b, Line("EXT-01"), Line("AREA", runs: false), Line("OLD", active: false) };

        Assert.Equal(["PLT1 · EXT-01 · EXT-01", "PLT1 · EXT-02 · EXT-02"], CapabilityForm.Lines(equipment).Select(l => l.Label));
        Assert.Equal(Chicago, CapabilityForm.TimeZoneOf(b.Id, equipment));
    }
}
