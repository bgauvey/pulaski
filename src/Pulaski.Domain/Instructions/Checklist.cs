using System.Globalization;

namespace Pulaski.Domain.Instructions;

/// <summary>When a checklist is answered (#126c, D-116): before the job starts on a line, or before its first report there.</summary>
public static class ChecklistPhases
{
    public const string Setup = "setup";
    public const string FirstArticle = "first_article";

    public static readonly IReadOnlyList<string> All = [Setup, FirstArticle];
}

/// <summary>What an item asks for.</summary>
public static class ChecklistItemKinds
{
    public const string YesNo = "yes_no";
    public const string Number = "number";
    public const string Choice = "choice";
    public const string Text = "text";

    public static readonly IReadOnlyList<string> All = [YesNo, Number, Choice, Text];
}

/// <summary>
/// One question of a checklist (#126c). A yes/no item may expect an answer (<see cref="Expected"/>); a number may have limits and a unit; a choice
/// offers its <see cref="Choices"/>. An answer against what is expected, or outside the limits, fails the checklist.
/// </summary>
public sealed record ChecklistItem(
    string Prompt, string Kind, bool Required, bool? Expected = null, decimal? Min = null, decimal? Max = null, string? Unit = null,
    IReadOnlyList<string>? Choices = null);

/// <summary>A version's checklist for one phase (#126c): its items, numbered from 1 in order.</summary>
public sealed record Checklist(string Phase, IReadOnlyList<ChecklistItem> Items);

/// <summary>One item's answer as kept with the run: the item's number, the answer as given (yes/no, an invariant number, the choice, the text), and whether it passed.</summary>
public sealed record ChecklistAnswer(int Item, string? Value, bool Ok);

/// <summary>A checklist answered: passed when every answer is within what its item allows.</summary>
public sealed record ChecklistResult(bool Passed, IReadOnlyList<ChecklistAnswer> Answers);

/// <summary>Checks checklists as written in a draft, and judges their answers (#126c). Pure.</summary>
public static class Checklists
{
    public const int MaxItems = 50;
    public const int MaxPrompt = 300;
    public const int MaxChoices = 20;
    public const int MaxChoice = 100;
    public const int MaxUnit = 20;
    public const int MaxText = 500;

    /// <summary>The checklists cleaned up: one per phase, setup first, items trimmed; a checklist with no items is left out.</summary>
    /// <exception cref="DomainRuleException">A phase or kind isn't known, a phase comes twice, or an item isn't complete.</exception>
    public static IReadOnlyList<Checklist> Check(IReadOnlyList<Checklist>? checklists)
    {
        var result = new List<Checklist>();
        foreach (var checklist in checklists ?? [])
        {
            if (!ChecklistPhases.All.Contains(checklist.Phase))
            {
                throw new DomainRuleException($"'{checklist.Phase}' isn't a checklist's phase; use setup or first_article.");
            }

            if (result.Any(c => c.Phase == checklist.Phase))
            {
                throw new DomainRuleException($"A version has one {UserMessage.Code(checklist.Phase)} checklist.");
            }

            if (checklist.Items.Count > MaxItems)
            {
                throw new DomainRuleException($"A checklist has at most {MaxItems} items.");
            }

            if (checklist.Items.Count > 0)
            {
                result.Add(checklist with { Items = [.. checklist.Items.Select((item, i) => Item(item, i + 1))] });
            }
        }

        return [.. result.OrderBy(c => c.Phase == ChecklistPhases.Setup ? 0 : 1)];
    }

    private static ChecklistItem Item(ChecklistItem item, int number)
    {
        var prompt = (item.Prompt ?? "").Trim();
        if (prompt.Length is 0 or > MaxPrompt)
        {
            throw new DomainRuleException($"Item {number}: ask something, in at most {MaxPrompt} characters.");
        }

        var unit = string.IsNullOrWhiteSpace(item.Unit) ? null : item.Unit.Trim();
        switch (item.Kind)
        {
            case ChecklistItemKinds.YesNo:
                return new ChecklistItem(prompt, item.Kind, item.Required, Expected: item.Expected);

            case ChecklistItemKinds.Number:
                if (item.Min is { } min && item.Max is { } max && min > max)
                {
                    throw new DomainRuleException($"Item {number}: the lowest allowed is above the highest.");
                }

                if (unit is { Length: > MaxUnit })
                {
                    throw new DomainRuleException($"Item {number}: keep the unit to {MaxUnit} characters.");
                }

                return new ChecklistItem(prompt, item.Kind, item.Required, Min: item.Min, Max: item.Max, Unit: unit);

            case ChecklistItemKinds.Choice:
                var choices = (item.Choices ?? []).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
                if (choices.Count is < 2 or > MaxChoices || choices.Any(c => c.Length > MaxChoice))
                {
                    throw new DomainRuleException($"Item {number}: offer 2 to {MaxChoices} choices of at most {MaxChoice} characters.");
                }

                return new ChecklistItem(prompt, item.Kind, item.Required, Choices: choices);

            case ChecklistItemKinds.Text:
                return new ChecklistItem(prompt, item.Kind, item.Required);

            default:
                throw new DomainRuleException($"Item {number}: '{item.Kind}' isn't a kind of item; use yes_no, number, choice or text.");
        }
    }

    /// <summary>
    /// Judges <paramref name="answers"/> (by item number; blank = not answered) against <paramref name="checklist"/>: an item answered against
    /// what it expects, or outside its limits, fails it. Unanswered optional items are left out.
    /// </summary>
    /// <exception cref="DomainRuleException">A required item isn't answered, or an answer isn't one the item takes.</exception>
    public static ChecklistResult Evaluate(Checklist checklist, IReadOnlyDictionary<int, string?> answers)
    {
        var kept = new List<ChecklistAnswer>();
        for (var i = 0; i < checklist.Items.Count; i++)
        {
            var number = i + 1;
            var item = checklist.Items[i];
            var given = answers.TryGetValue(number, out var raw) ? raw?.Trim() : null;
            if (string.IsNullOrEmpty(given))
            {
                if (item.Required)
                {
                    throw new DomainRuleException($"Answer item {number}: {item.Prompt}");
                }

                continue;
            }

            kept.Add(item.Kind switch
            {
                ChecklistItemKinds.YesNo => YesNo(item, number, given),
                ChecklistItemKinds.Number => Number(item, number, given),
                ChecklistItemKinds.Choice => item.Choices!.Contains(given, StringComparer.Ordinal)
                    ? new ChecklistAnswer(number, given, true)
                    : throw new DomainRuleException($"Item {number}: choose one of {string.Join(", ", item.Choices!)}."),
                _ => given.Length > MaxText
                    ? throw new DomainRuleException($"Item {number}: keep the answer to {MaxText} characters.")
                    : new ChecklistAnswer(number, given, true),
            });
        }

        return new ChecklistResult(kept.All(a => a.Ok), kept);
    }

    private static ChecklistAnswer YesNo(ChecklistItem item, int number, string given)
    {
        var yes = given.ToLowerInvariant() switch
        {
            "yes" or "true" => true,
            "no" or "false" => false,
            _ => throw new DomainRuleException($"Item {number}: answer yes or no."),
        };
        return new ChecklistAnswer(number, yes ? "yes" : "no", item.Expected is not { } expected || expected == yes);
    }

    private static ChecklistAnswer Number(ChecklistItem item, int number, string given)
    {
        if (!decimal.TryParse(given, NumberStyles.Number, CultureInfo.InvariantCulture, out var value))
        {
            throw new DomainRuleException($"Item {number}: give a number.");
        }

        var ok = (item.Min is not { } min || value >= min) && (item.Max is not { } max || value <= max);
        return new ChecklistAnswer(number, value.ToString(CultureInfo.InvariantCulture), ok);
    }
}
