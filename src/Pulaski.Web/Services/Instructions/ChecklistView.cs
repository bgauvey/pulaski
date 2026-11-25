using System.Globalization;
using Pulaski.Contracts.Instructions;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Instructions;

/// <summary>How a checklist and its answers read (#126c). Pure.</summary>
public static class ChecklistView
{
    /// <summary>"Setup checklist" / "First-article checklist".</summary>
    public static string Title(Text l, string phase) => phase == "setup" ? l["Setup checklist"] : l["First-article checklist"];

    /// <summary>What the item allows, in words: its limits and unit, or the answer it expects; null when nothing.</summary>
    public static string? Allows(Text l, ChecklistItemDto item) => item.Kind switch
    {
        "yes_no" when item.Expected is { } expected => expected ? l["expects yes"] : l["expects no"],
        "number" when item.Min is { } min && item.Max is { } max => l["{0} to {1} {2}", Number(min), Number(max), item.Unit ?? ""].TrimEnd(),
        "number" when item.Min is { } min => l["at least {0} {1}", Number(min), item.Unit ?? ""].TrimEnd(),
        "number" when item.Max is { } max => l["at most {0} {1}", Number(max), item.Unit ?? ""].TrimEnd(),
        "number" when item.Unit is { } unit => unit,
        _ => null,
    };

    /// <summary>How it stands for the job on its line.</summary>
    public static string Standing(Text l, JobChecklistDto checklist, string? timeZone) => checklist.Result switch
    {
        "passed" => l["Passed {0} by {1}.", Display.SiteTime(checklist.CompletedAt, timeZone), checklist.CompletedByName ?? "?"],
        "failed" => checklist.Answers.Where(a => !a.Ok).Select(a => a.Item).ToList() is [var one]
            ? l["Failed on item {0} ({1}): put it right and answer it again.", one, Display.SiteTime(checklist.CompletedAt, timeZone)]
            : l["Failed on items {0} ({1}): put them right and answer it again.", string.Join(", ", checklist.Answers.Where(a => !a.Ok).Select(a => a.Item)), Display.SiteTime(checklist.CompletedAt, timeZone)],
        _ => checklist.Phase == "setup" ? l["To do before the job starts on this line."] : l["To do before the first report on this line."],
    };

    /// <summary>An answer as typed, made ready to send: numbers in the reader's culture become invariant ones; blanks are left out.</summary>
    public static Dictionary<int, string?> ToSend(IReadOnlyList<ChecklistItemDto> items, IReadOnlyDictionary<int, string?> answers)
    {
        var sent = new Dictionary<int, string?>();
        for (var i = 0; i < items.Count; i++)
        {
            if (!answers.TryGetValue(i + 1, out var raw) || string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var value = raw.Trim();
            sent[i + 1] = items[i].Kind == "number" && decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var n)
                ? n.ToString(CultureInfo.InvariantCulture)
                : value;
        }

        return sent;
    }

    private static string Number(decimal value) => value.ToString("0.####", CultureInfo.CurrentCulture);
}

/// <summary>A checklist item as edited in a draft (#126c): text fields that become a <see cref="ChecklistItemDto"/> on saving.</summary>
public sealed class ChecklistDraftItem
{
    public string Prompt { get; set; } = "";
    public string Kind { get; set; } = "yes_no";
    public bool Required { get; set; } = true;

    /// <summary>"" (anything), "yes" or "no".</summary>
    public string Expected { get; set; } = "";
    public string? Min { get; set; }
    public string? Max { get; set; }
    public string? Unit { get; set; }

    /// <summary>One choice per line.</summary>
    public string? Choices { get; set; }

    public static ChecklistDraftItem From(ChecklistItemDto item) => new()
    {
        Prompt = item.Prompt,
        Kind = item.Kind,
        Required = item.Required,
        Expected = item.Expected is { } e ? (e ? "yes" : "no") : "",
        Min = item.Min?.ToString("0.####", CultureInfo.CurrentCulture),
        Max = item.Max?.ToString("0.####", CultureInfo.CurrentCulture),
        Unit = item.Unit,
        Choices = item.Choices is { } c ? string.Join("\n", c) : null,
    };

    /// <summary>What is saved; a limit that isn't a number is sent as none, and a blank prompt is refused by the API.</summary>
    public ChecklistItemDto ToDto() => new()
    {
        Prompt = Prompt,
        Kind = Kind,
        Required = Required,
        Expected = Kind == "yes_no" && Expected.Length > 0 ? Expected == "yes" : null,
        Min = Kind == "number" ? Parse(Min) : null,
        Max = Kind == "number" ? Parse(Max) : null,
        Unit = Kind == "number" ? Unit : null,
        Choices = Kind == "choice" ? (Choices ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) : null,
    };

    private static decimal? Parse(string? text) =>
        decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var value) ? value : null;
}
