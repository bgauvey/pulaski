using System.Globalization;

namespace Pulaski.Web.Services.Labor;

/// <summary>A button pressed at a terminal (D-049): what to do, and for which line, session or break.</summary>
public sealed record TerminalAction(string Kind, Guid? Id)
{
    public const string ClockOn = "on";
    public const string ClockOff = "off";
    public const string Break = "break";
    public const string PaidBreak = "paidbreak";
    public const string EndBreak = "endbreak";
    public const string Done = "done";

    /// <summary>Choosing what the time is for before the line (#84): <c>kind:setup</c>; the kind rides along as <see cref="Kind"/>'s text.</summary>
    public const string ChooseKind = "kind";

    /// <summary>Choosing an indirect reason (#84): <c>reason:{id}</c>.</summary>
    public const string ChooseReason = "reason";

    /// <summary>The kinds of time a terminal offers, with their button words.</summary>
    public static IReadOnlyList<(string Code, string Label)> Kinds { get; } =
        [("direct", "Production"), ("setup", "Setup"), ("indirect", "Indirect"), ("training", "Training")];

    /// <summary>The API action it is, or null for <see cref="Done"/>.</summary>
    public string? ApiAction => Kind switch
    {
        ClockOn => "clock-on",
        ClockOff => "clock-off",
        Break or PaidBreak => "breaks/start",
        EndBreak => "breaks/end",
        _ => null,
    };

    /// <summary>A button's value, such as <c>on:0192…</c>.</summary>
    public string Value => Id is { } id ? $"{Kind}:{id}" : Kind;

    /// <summary>The button value posted back; null for anything not a known action.</summary>
    public static TerminalAction? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split(':', 2);
        Guid? id = parts.Length == 2 && Guid.TryParse(parts[1], out var parsed) ? parsed : null;
        return parts[0] switch
        {
            ClockOn or ClockOff or ChooseReason when id is not null => new TerminalAction(parts[0], id),
            Break or PaidBreak or EndBreak or Done when parts.Length == 1 => new TerminalAction(parts[0], null),
            _ => null,
        };
    }

    /// <summary>A kind button's value: <c>kind:setup</c>.</summary>
    public static string KindValue(string kind) => $"{ChooseKind}:{kind}";

    /// <summary>The kind a <see cref="KindValue"/> names; null for anything else.</summary>
    public static string? ParseKind(string? value) =>
        value?.Split(':', 2) is [ChooseKind, var kind] && Kinds.Any(k => k.Code == kind) ? kind : null;

    /// <summary>What the terminal says once it is done, with the person's name.</summary>
    public string Confirmation(string person, string? lineCode, string laborKind = "direct") => Kind switch
    {
        ClockOn when laborKind != "direct" => $"{person}, you're on {lineCode} for {Kinds.First(k => k.Code == laborKind).Label.ToLowerInvariant()}.",
        ClockOn => $"{person}, you're on {lineCode}.",
        ClockOff => $"{person}, you're clocked off.",
        Break => $"{person}, enjoy your break.",
        PaidBreak => $"{person}, enjoy your break (paid).",
        EndBreak => $"Welcome back, {person}.",
        _ => "",
    };

    /// <summary>"since 06:12" in the site's time zone.</summary>
    public static string Since(DateTimeOffset at, string? timeZone) => "since " + Display.ToSite(at, timeZone).ToString("HH:mm", CultureInfo.InvariantCulture);
}
