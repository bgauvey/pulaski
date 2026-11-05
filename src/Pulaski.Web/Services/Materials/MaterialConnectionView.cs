using System.Globalization;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.Materials;

namespace Pulaski.Web.Services.Materials;

/// <summary>How the connections panel words a check or an import (D-068).</summary>
public static class MaterialConnectionView
{
    public static string Summary(string code, ImportMaterialsResponse r) =>
        r.FileProblems.Count > 0 ? $"{code} can't be imported: {string.Join(" ", r.FileProblems)}"
        : r.Applied ? $"Imported from {code}: {r.Created} created, {r.Updated} updated, {r.Unchanged} unchanged."
        : r.Errors > 0 ? $"{code}: {r.Errors} {(r.Errors == 1 ? "item has" : "items have")} errors, so nothing can be imported yet. Fix them at the source or in the field map, then check again."
        : r.Created + r.Updated == 0 ? $"{code} is up to date: {r.Unchanged} unchanged."
        : $"{code} is ready: {r.Created} to create, {r.Updated} to update, {r.Unchanged} unchanged. Click Import.";

    /// <summary>Where a connection reads from, by its kind (#72).</summary>
    public static string Source(MaterialConnectionDto c) => c.Kind switch
    {
        "scim" => $"SCIM {c.BaseUrl}/Users{(c.Filter is { } scimFilter ? $" · {scimFilter}" : "")}",
        "database" => $"PostgreSQL query: {Shorten(c.Query)}",
        "soap" => $"SOAP {c.BaseUrl} · items <{c.EntitySet}>",
        "rest" => $"REST {c.BaseUrl}{(c.EntitySet.Length > 0 ? $" · items at {c.EntitySet}" : "")}{(c.NextLinkPath is { } next ? $" · next at {next}" : "")}",
        _ => $"{c.BaseUrl}/{c.EntitySet}{(c.Filter is { } f ? $" · {f}" : "")}",
    };

    /// <summary>When it runs on its own (#71).</summary>
    public static string Schedule(MaterialConnectionDto c) =>
        c.ScheduleEveryHours is { } hours ? (hours == 1 ? "Every hour" : $"Every {hours} hours")
        : c.ScheduleDailyAt.Count > 0 ? $"Daily at {string.Join(", ", c.ScheduleDailyAt.Select(t => t.ToString("HH:mm", CultureInfo.InvariantCulture)))} UTC"
        : "On demand";

    public static string Outcome(string? outcome) => outcome switch
    {
        "applied" => "Imported",
        "not_applied" => "Not imported (errors)",
        "failed" => "Failed",
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
