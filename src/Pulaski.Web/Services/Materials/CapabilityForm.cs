using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Materials;

namespace Pulaski.Web.Services.Materials;

/// <summary>
/// The add or edit form for a capability (D-034). Dates are days in the line's site: a capability
/// starts at the beginning of its first day and stops at the beginning of its end day. A date left as
/// it was keeps the exact time stored, so editing the rate doesn't move a period by a few hours.
/// </summary>
public sealed class CapabilityForm
{
    public Guid? CapabilityId { get; private init; }
    public long Version { get; private init; }
    public Guid EquipmentId { get; set; }
    public decimal? IdealRatePerHour { get; set; }
    public Guid RateUomId { get; set; }
    public int? ChangeoverMinutes { get; set; }
    public decimal? CrewSize { get; set; }
    public bool IsPreferred { get; set; }

    /// <summary>The line's own batch size (D-065), in the material's base unit; blank for the material's.</summary>
    public decimal? BatchSize { get; set; }
    public DateTime? From { get; set; }
    public DateTime? EndsOn { get; set; }

    private DateTimeOffset? _originalFrom;
    private DateTimeOffset? _originalTo;
    private DateTime? _originalFromDay;
    private DateTime? _originalEndsOn;

    public bool IsNew => CapabilityId is null;

    /// <summary>A blank form for a new line, rated in the material's base unit, starting today.</summary>
    public static CapabilityForm New(Guid baseUomId, DateTimeOffset now, string? timeZone) => new()
    {
        RateUomId = baseUomId,
        From = Display.ToSite(now, timeZone).Date,
    };

    public static CapabilityForm For(MaterialCapabilityDto c)
    {
        var fromDay = Display.ToSite(c.EffectiveFrom, c.SiteTimeZone).Date;
        var endsOn = c.EffectiveTo is { } to ? Display.ToSite(to, c.SiteTimeZone).Date : (DateTime?)null;
        return new CapabilityForm
        {
            CapabilityId = c.Id,
            Version = c.Version,
            EquipmentId = c.EquipmentId,
            IdealRatePerHour = Trim(c.IdealRatePerHour),
            RateUomId = c.RateUomId,
            ChangeoverMinutes = c.StandardChangeover is { } ch ? (int)ch.TotalMinutes : null,
            CrewSize = c.StandardCrewSize is { } crew ? Trim(crew) : null,
            IsPreferred = c.IsPreferred,
            BatchSize = c.BatchSize is { } batch ? Trim(batch) : null,
            From = fromDay,
            EndsOn = endsOn,
            _originalFrom = c.EffectiveFrom,
            _originalTo = c.EffectiveTo,
            _originalFromDay = fromDay,
            _originalEndsOn = endsOn,
        };
    }

    /// <summary>The value without the trailing zeros of its stored scale (900.000000 becomes 900), for an input box.</summary>
    public static decimal Trim(decimal value) => value / 1.000000000000000000000000000000000m;

    /// <summary>Lines a capability can go on: active equipment that runs jobs, by site and code.</summary>
    public static IReadOnlyList<(EquipmentDto Line, string Label)> Lines(IReadOnlyList<EquipmentDto> equipment)
    {
        var sites = equipment.Where(e => e.Level == "site").ToDictionary(e => e.Id);
        return [.. equipment
            .Where(e => e.CanRunJob && e.IsActive)
            .Select(e => (e, Label: $"{(e.SiteId is { } s && sites.TryGetValue(s, out var site) ? site.Code + " · " : "")}{e.Code} · {e.Name}"))
            .OrderBy(x => x.Label, StringComparer.CurrentCultureIgnoreCase)];
    }

    /// <summary>The time zone of the line's site.</summary>
    public static string? TimeZoneOf(Guid lineId, IReadOnlyList<EquipmentDto> equipment) =>
        equipment.FirstOrDefault(e => e.Id == lineId)?.SiteId is { } siteId ? equipment.FirstOrDefault(e => e.Id == siteId)?.TimeZone : null;

    public bool CanSubmit => EquipmentId != Guid.Empty && IdealRatePerHour > 0 && RateUomId != Guid.Empty && From is not null;

    /// <summary>The new capability; one starting today starts now, so it can follow straight on from one ended earlier today.</summary>
    public AddCapabilityRequest ToAdd(string? timeZone, DateTimeOffset now) => new()
    {
        EquipmentId = EquipmentId,
        IdealRatePerHour = IdealRatePerHour ?? 0,
        RateUomId = RateUomId,
        StandardChangeoverMinutes = ChangeoverMinutes,
        StandardCrewSize = CrewSize,
        IsPreferred = IsPreferred,
        BatchSize = BatchSize,
        EffectiveFrom = From == Display.ToSite(now, timeZone).Date ? null : Display.FromSiteLocal(From, timeZone),
        EffectiveTo = Display.FromSiteLocal(EndsOn, timeZone),
    };

    public UpdateCapabilityRequest ToUpdate(string? timeZone) => new()
    {
        Version = Version,
        IdealRatePerHour = IdealRatePerHour ?? 0,
        RateUomId = RateUomId,
        StandardChangeoverMinutes = ChangeoverMinutes,
        StandardCrewSize = CrewSize,
        IsPreferred = IsPreferred,
        BatchSize = BatchSize,
        EffectiveFrom = From == _originalFromDay && _originalFrom is { } f ? f : Display.FromSiteLocal(From, timeZone) ?? DateTimeOffset.MinValue,
        EffectiveTo = EndsOn == _originalEndsOn ? _originalTo : Display.FromSiteLocal(EndsOn, timeZone),
    };
}
