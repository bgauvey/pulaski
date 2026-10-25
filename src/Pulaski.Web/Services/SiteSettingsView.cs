using System.Globalization;
using Pulaski.Contracts.EquipmentModel;
using Pulaski.Contracts.Shifts;

namespace Pulaski.Web.Services;

/// <summary>How the Site settings screen words its settings and checks what is typed (D-043).</summary>
public static class SiteSettingsView
{
    public const decimal MinTolerance = 100m;
    public const decimal MaxTolerance = 1000m;
    public const decimal DefaultTolerance = 110m;

    public static IReadOnlyList<(string Kind, string Label)> Kinds { get; } =
    [
        ("holiday", "Holiday"),
        ("planned_shutdown", "Planned shutdown"),
        ("no_demand", "No demand"),
        ("other", "Other"),
    ];

    public static string KindLabel(string kind) => Kinds.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    /// <summary>What a tolerance typed on the screen is wrong with; null when it can be saved.</summary>
    public static string? ToleranceProblem(decimal? value) => value switch
    {
        null => "Enter a percentage.",
        < MinTolerance => "At least 100%: reports are never held below what the line can do or what was planned.",
        > MaxTolerance => "At most 1000%.",
        _ => null,
    };

    /// <summary>"A run of 2 h at an ideal rate of 1,000 m/h can report up to 2,200 m." for the tolerance given.</summary>
    public static string RateExample(decimal? tolerancePct) =>
        tolerancePct is >= MinTolerance and <= MaxTolerance
            ? $"With {Pct(tolerancePct.Value)}, a 2-hour run on a line whose ideal rate is 1,000 m an hour can report up to {Qty(2000m * tolerancePct.Value / 100m)} m, good and scrap together; more is refused."
            : "";

    /// <summary>"A job planned to use 500 kg can consume 550 kg before a warning." for the tolerance given.</summary>
    public static string ConsumptionExample(decimal? tolerancePct) =>
        tolerancePct is >= MinTolerance and <= MaxTolerance
            ? $"With {Pct(tolerancePct.Value)}, a job planned to use 500 kg of a component can record up to {Qty(500m * tolerancePct.Value / 100m)} kg before Pulaski warns; more is still recorded, with a warning."
            : "";

    /// <summary>The equipment an exception can apply to: the site, its areas and the lines and departments planning uses.</summary>
    public static IReadOnlyList<EquipmentDto> Scopes(IReadOnlyList<EquipmentDto> equipment, Guid siteId) =>
        [.. equipment.Where(e => e.IsActive && (e.Id == siteId || e.SiteId == siteId)
                                 && (e.Level is "site" or "area" || e.IsSchedulable || e.CanRunJob))
                     .OrderBy(e => e.Level == "site" ? 0 : 1).ThenBy(e => e.Depth).ThenBy(e => e.Code, StringComparer.Ordinal)];

    /// <summary>"Sat 25 Dec 06:00 – Sun 26 Dec 06:00 (24 h)" in the site's time zone.</summary>
    public static string Window(CalendarExceptionDto exception, string? timeZone)
    {
        var start = Display.ToSite(exception.StartsAt, timeZone);
        var end = Display.ToSite(exception.EndsAt, timeZone);
        var length = exception.EndsAt - exception.StartsAt;
        var hours = length.TotalHours >= 48 ? $"{length.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} days" : $"{length.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} h";
        return $"{start.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} – {end.ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture)} ({hours})";
    }

    /// <summary>Whether it is over, under way or still to come at <paramref name="now"/>.</summary>
    public static string When(CalendarExceptionDto exception, DateTimeOffset now) =>
        exception.EndsAt <= now ? "Past" : exception.StartsAt <= now ? "Now" : "Coming";

    private static string Pct(decimal value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    private static string Qty(decimal value) => value.ToString("#,0.##", CultureInfo.InvariantCulture);

    /// <summary>The rules for ERP quantities that aren't whole batches (D-066), with what each does.</summary>
    public static IReadOnlyList<(string Rule, string Label)> ErpBatchRules { get; } =
    [
        ("reject", "Refuse the order: the ERP must send whole batches"),
        ("round_up", "Round up to whole batches"),
        ("round_down", "Round down to whole batches"),
        ("hold", "Import it held until a planner sets whole batches"),
    ];
}
