using System.Globalization;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Shifts;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services;

/// <summary>How the Site settings screen words its settings and checks what is typed (D-043).</summary>
public static class SiteSettingsView
{
    public const decimal MinTolerance = 100m;
    public const decimal MaxTolerance = 1000m;
    public const decimal DefaultTolerance = 110m;

    public static IReadOnlyList<(string Kind, string Label)> Kinds { get; } =
    [
        ("holiday", Term.Mark("Holiday")),
        ("planned_shutdown", Term.Mark("Planned shutdown")),
        ("no_demand", Term.Mark("No demand")),
        ("other", Term.Mark("Other")),
    ];

    public static string KindLabel(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    /// <summary>What a tolerance typed on the screen is wrong with; null when it can be saved.</summary>
    public static string? ToleranceProblem(Text l, decimal? value) => value switch
    {
        null => l["Enter a percentage."],
        < MinTolerance => l["At least 100%: reports are never held below what the line can do or what was planned."],
        > MaxTolerance => l["At most 1000%."],
        _ => null,
    };

    /// <summary>"A run of 2 h at an ideal rate of 1,000 m/h can report up to 2,200 m." for the tolerance given.</summary>
    public static string RateExample(Text l, decimal? tolerancePct) =>
        tolerancePct is >= MinTolerance and <= MaxTolerance
            ? l["With {0}, a 2-hour run on a line whose ideal rate is 1,000 m an hour can report up to {1} m, good and scrap together; more is refused.", Pct(tolerancePct.Value), Qty(2000m * tolerancePct.Value / 100m)]
            : "";

    /// <summary>"A job planned to use 500 kg can consume 550 kg before a warning." for the tolerance given.</summary>
    public static string ConsumptionExample(Text l, decimal? tolerancePct) =>
        tolerancePct is >= MinTolerance and <= MaxTolerance
            ? l["With {0}, a job planned to use 500 kg of a component can record up to {1} kg before Pulaski warns; more is still recorded, with a warning.", Pct(tolerancePct.Value), Qty(500m * tolerancePct.Value / 100m)]
            : "";

    /// <summary>The equipment an exception can apply to: the site, its areas and the lines and departments planning uses.</summary>
    public static IReadOnlyList<EquipmentDto> Scopes(IReadOnlyList<EquipmentDto> equipment, Guid siteId) =>
        [.. equipment.Where(e => e.IsActive && (e.Id == siteId || e.SiteId == siteId)
                                 && (e.Level is "site" or "area" || e.IsSchedulable || e.CanRunJob))
                     .OrderBy(e => e.Level == "site" ? 0 : 1).ThenBy(e => e.Depth).ThenBy(e => e.Code, StringComparer.Ordinal)];

    /// <summary>"Sat 25 Dec 06:00 – Sun 26 Dec 06:00 (24 h)" in the site's time zone.</summary>
    public static string Window(Text l, CalendarExceptionDto exception, string? timeZone)
    {
        var start = Display.ToSite(exception.StartsAt, timeZone);
        var end = Display.ToSite(exception.EndsAt, timeZone);
        var length = exception.EndsAt - exception.StartsAt;
        var hours = length.TotalHours >= 48 ? l["{0} days", length.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)] : $"{length.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
        return $"{start.ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture)} – {end.ToString("ddd d MMM yyyy HH:mm", CultureInfo.CurrentCulture)} ({hours})";
    }

    /// <summary>Whether it is over, under way or still to come at <paramref name="now"/>.</summary>
    public static string When(Text l, CalendarExceptionDto exception, DateTimeOffset now) =>
        exception.EndsAt <= now ? l["Past"] : exception.StartsAt <= now ? l["Now"] : l["Coming"];

    private static string Pct(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Qty(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>The rules for ERP quantities that aren't whole batches (D-066), with what each does.</summary>
    public static IReadOnlyList<(string Rule, string Label)> ErpBatchRules { get; } =
    [
        ("reject", Term.Mark("Refuse the order: the ERP must send whole batches")),
        ("round_up", Term.Mark("Round up to whole batches")),
        ("round_down", Term.Mark("Round down to whole batches")),
        ("hold", Term.Mark("Import it held until a planner sets whole batches")),
    ];
}
