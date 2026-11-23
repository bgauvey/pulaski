using System.Globalization;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.Materials;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Materials;

/// <summary>How the connections panel words a check or an import (D-068).</summary>
public static class MaterialConnectionView
{
    public static string Summary(Text l, string code, ImportMaterialsResponse r) =>
        r.FileProblems.Count > 0 ? l["{0} can't be imported: {1}", code, string.Join(" ", r.FileProblems)]
        : r.Applied ? l["Imported from {0}: {1} created, {2} updated, {3} unchanged.", code, r.Created, r.Updated, r.Unchanged]
        : r.Errors > 0 ? l["{0}: items with errors: {1}. Nothing can be imported yet; fix them at the source or in the field map, then check again.", code, r.Errors]
        : r.Created + r.Updated == 0 ? l["{0} is up to date: {1} unchanged.", code, r.Unchanged]
        : l["{0} is ready: {1} to create, {2} to update, {3} unchanged. Click Import.", code, r.Created, r.Updated, r.Unchanged];

    /// <summary>Where a connection reads from, by its kind (#72).</summary>
    public static string Source(Text l, MaterialConnectionDto c) => c.Kind switch
    {
        "scim" => $"SCIM {c.BaseUrl}/Users{(c.Filter is { } scimFilter ? $" · {scimFilter}" : "")}",
        "database" => l["PostgreSQL query: {0}", Shorten(c.Query)],
        "soap" => l["SOAP {0} · items <{1}>", c.BaseUrl, c.EntitySet],
        "rest" => $"REST {c.BaseUrl}{(c.EntitySet.Length > 0 ? l[" · items at {0}", c.EntitySet] : "")}{(c.NextLinkPath is { } next ? l[" · next at {0}", next] : "")}",
        _ => $"{c.BaseUrl}/{c.EntitySet}{(c.Filter is { } f ? $" · {f}" : "")}",
    };

    /// <summary>When it runs on its own (#71).</summary>
    public static string Schedule(Text l, MaterialConnectionDto c) =>
        c.ScheduleEveryHours is { } hours ? (hours == 1 ? l["Every hour"] : l["Every {0} hours", hours])
        : c.ScheduleDailyAt.Count > 0 ? l["Daily at {0} UTC", string.Join(", ", c.ScheduleDailyAt.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)))]
        : l["On demand"];

    public static string Outcome(string? outcome) => outcome switch
    {
        "applied" => Term.Mark("Imported"),
        "not_applied" => Term.Mark("Not imported (errors)"),
        "failed" => Term.Mark("Failed"),
        _ => "",
    };

    /// <summary>Times typed as <c>02:00, 14:30</c>; null when one isn't a time.</summary>
    public static IReadOnlyList<TimeOnly>? ParseTimes(string? text)
    {
        var times = new List<TimeOnly>();
        foreach (var part in (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TimeOnly.TryParseExact(part, ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
            {
                return null;
            }

            times.Add(time);
        }

        return times;
    }

    /// <summary>A people import's result in the shape the panel shows (#71).</summary>
    public static ImportMaterialsResponse AsImport(ImportPeopleResponse r) => new()
    {
        Applied = r.Applied,
        Created = r.Created,
        Updated = r.Updated,
        Unchanged = r.Unchanged,
        Errors = r.Errors,
        FileProblems = r.FileProblems,
        Rows = r.Rows,
    };

    private static string Shorten(string? text) =>
        text is null ? "" : text.Length <= 80 ? text : string.Concat(text.AsSpan(0, 79), "…");
}
