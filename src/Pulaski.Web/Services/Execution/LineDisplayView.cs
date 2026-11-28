using System.Globalization;
using Pulaski.Contracts.Execution;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Execution;

/// <summary>The words and classes of a line display board (#120): readable from across the floor, color only where something needs attention.</summary>
public static class LineDisplayView
{
    /// <summary>How often the board reloads.</summary>
    public const int RefreshSeconds = 15;

    public static string StatusLabel(string status) => status switch
    {
        "running" => Term.Mark("Running"),
        "down" => Term.Mark("Down"),
        "waiting" => Term.Mark("Waiting"),
        "no_signal" => Term.Mark("No signal"),
        _ => Term.Mark("Idle"),
    };

    /// <summary>Running is quiet; down and no signal stand out; waiting is a caution (ISA-101: color for the abnormal).</summary>
    public static string StatusClass(string status) => status switch
    {
        "down" => "is-down",
        "no_signal" => "is-nosignal",
        "waiting" => "is-waiting",
        "running" => "is-running",
        _ => "is-idle",
    };

    /// <summary>"45 s", "12 min", "2 h 05 min"; null without a start.</summary>
    public static string? Duration(DateTimeOffset? since, DateTimeOffset now)
    {
        if (since is not { } start)
        {
            return null;
        }

        var span = now - start;
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        return span.TotalMinutes < 1 ? $"{(int)span.TotalSeconds} s"
            : span.TotalHours < 1 ? $"{(int)span.TotalMinutes} min"
            : $"{(int)span.TotalHours} h {span.Minutes:00} min";
    }

    /// <summary>Minutes as a board reads them: "45 min", "21 h 02 min".</summary>
    public static string Minutes(decimal minutes)
    {
        var whole = (int)Math.Round(minutes);
        return whole < 60 ? $"{whole} min" : $"{whole / 60} h {whole % 60:00} min";
    }

    /// <summary>How far the job is: good against planned, 0 to 100; null without a plan.</summary>
    public static int? Progress(LineDisplayJobDto job) =>
        job.PlannedQty is { } planned and > 0 ? (int)Math.Clamp(Math.Round(job.GoodQty / planned * 100), 0, 100) : null;

    // In the reader's region (#128): the same as before unless they chose one.
    public static string Qty(decimal value) => Math.Round(value, 1).ToString("#,0.#", CultureInfo.CurrentCulture);

    public static string Pct(decimal? value) => value is { } v ? $"{Math.Round(v, 0).ToString(CultureInfo.CurrentCulture)}%" : "—";

    /// <summary>Below its target, OEE shows as a caution; without a target or a figure, as normal.</summary>
    public static bool BelowTarget(LineDisplayLineDto line) => line is { OeePct: { } oee, OeeTargetPct: { } target } && oee < target;

    /// <summary>
    /// The board's columns: one line fills the screen, two or four share it two by two, three sit side by side (two rows of busy cards
    /// don't fit a 1080p TV's height, and a TV can't scroll), more fill rows of three.
    /// </summary>
    public static string GridClass(int lines) => lines switch
    {
        <= 1 => "board-grid-1",
        2 or 4 => "board-grid-2",
        _ => "board-grid-3",
    };
}
