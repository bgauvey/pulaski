using System.Globalization;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.MachineData;

namespace Pulaski.Web.Services.MachineData;

/// <summary>OEE targets on screen (D-073): which periods are in force, the form's figures, and how a figure compares with its target.</summary>
public static class OeeTargetsView
{
    /// <summary>Equipment that can have targets of its own: the site's active equipment that tracks state, in tree order.</summary>
    public static IReadOnlyList<EquipmentDto> Equipment(IReadOnlyList<EquipmentDto> equipment, Guid siteId) =>
        [.. equipment.Where(e => e.IsActive && e.TracksState && e.SiteId == siteId && e.Id != siteId)
                     .OrderBy(e => e.Depth).ThenBy(e => e.Code, StringComparer.Ordinal)];

    /// <summary>The periods in force on the day: the site's default first, then equipment by code.</summary>
    public static IReadOnlyList<OeeTargetDto> InForce(IEnumerable<OeeTargetDto> targets, DateOnly day) =>
        [.. targets.Where(t => t.EffectiveFrom <= day && (t.EffectiveTo is null || day < t.EffectiveTo))
                   .OrderBy(t => t.EquipmentId is not null).ThenBy(t => t.EquipmentCode, StringComparer.Ordinal)];

    /// <summary>The scope's latest period, whose version a change of the scope sends; null when it has none.</summary>
    public static OeeTargetDto? Latest(IEnumerable<OeeTargetDto> targets, Guid? equipmentId) =>
        targets.Where(t => t.EquipmentId == equipmentId).MaxBy(t => t.EffectiveFrom);

    /// <summary>Why the form's figures can't be saved, or null: each blank or above 0 and at most 100 with one decimal, one at least.</summary>
    public static string? Problem(decimal? availability, decimal? performance, decimal? quality, decimal? oee)
    {
        decimal?[] values = [availability, performance, quality, oee];
        if (values.All(v => v is null))
        {
            return "Give at least one target.";
        }

        return values.Any(v => v is { } x && (x <= 0 || x > 100 || decimal.Round(x, 1) != x))
            ? "Each target is a percentage above 0 and at most 100, with one decimal at most."
            : null;
    }

    public static string Pct(decimal? value) => value is { } v ? v.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";

    /// <summary>"from Mon 3 Nov 2026" or "Mon 3 Nov – Sun 30 Nov 2026" (the last day in force).</summary>
    public static string Period(OeeTargetDto target) => target.EffectiveTo is { } to
        ? $"{target.EffectiveFrom.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)} – {to.AddDays(-1).ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}"
        : $"from {target.EffectiveFrom.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>True when the figure is below its target, false when it meets it, null when either is missing.</summary>
    public static bool? Below(decimal? figure, decimal? target) => figure is { } f && target is { } t ? f < t : null;
}
