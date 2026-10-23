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
            ClockOn or ClockOff when id is not null => new TerminalAction(parts[0], id),
            Break or PaidBreak or EndBreak or Done when parts.Length == 1 => new TerminalAction(parts[0], null),
            _ => null,
        };
    }

    /// <summary>What the terminal says once it is done, with the person's name.</summary>
    public string Confirmation(string person, string? lineCode) => Kind switch
    {
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
