using System.Text.RegularExpressions;

namespace Pulaski.Domain.MachineData;

/// <summary>What a utilization reason means for OEE (D-041): values of <c>core.reason_code.utilization_class</c>.</summary>
public static class UtilizationClasses
{
    /// <summary>Producing, or running slow: run time, whose losses show in performance and quality.</summary>
    public const string Runtime = "runtime";

    /// <summary>An unplanned stop while the machine should run: availability loss.</summary>
    public const string Downtime = "downtime";

    /// <summary>Scheduled non-production (setup, teardown, cleaning, planned maintenance): left out of planned time.</summary>
    public const string Planned = "planned";

    public static IReadOnlyList<string> All { get; } = [Runtime, Downtime, Planned];
}

/// <summary>The kinds of code a machine reports (D-041): PackML <c>Admin.StopReason</c>, <c>Admin.Alarm</c> and <c>Admin.Warning</c> ids.</summary>
public static class StopCodeKinds
{
    public const string StopReason = "stop_reason";
    public const string Alarm = "alarm";
    public const string Warning = "warning";

    public static IReadOnlyList<string> All { get; } = [StopReason, Alarm, Warning];
}

/// <summary>Where a piece of equipment's state comes from (D-041).</summary>
public static class StateDerivations
{
    /// <summary>Its own state tag.</summary>
    public const string OwnTags = "own_tags";

    /// <summary>Its machines run one after another: the worst of their states.</summary>
    public const string Series = "series";

    /// <summary>Its machines run side by side: the best of their states.</summary>
    public const string Parallel = "parallel";

    /// <summary>The state of one machine below it, the line's constraint.</summary>
    public const string ConstraintMachine = "constraint_machine";

    public static IReadOnlyList<string> All { get; } = [OwnTags, Series, Parallel, ConstraintMachine];
}

/// <summary>The 18 PackML states (ISA-TR88.00.02) by their StateCurrent number, unrenamed (D-005).</summary>
public static class PackMlStates
{
    /// <summary>State 0: no signal from the machine (D-005).</summary>
    public const int Undefined = 0;

    public static IReadOnlyList<string> Names { get; } =
    [
        "Undefined", "Clearing", "Stopped", "Starting", "Idle", "Suspended", "Execute", "Stopping", "Aborting",
        "Aborted", "Holding", "Held", "Unholding", "Suspending", "Unsuspending", "Resetting", "Completing", "Complete",
    ];

    public static bool IsValid(int state) => state >= 0 && state < Names.Count;

    public static string Name(int state) => IsValid(state) ? Names[state] : $"State {state}";

    /// <summary>A state from its number or its name, in any case; null when it is neither.</summary>
    public static int? Parse(string value)
    {
        var text = value.Trim();
        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            return IsValid(number) ? number : null;
        }

        for (var i = 0; i < Names.Count; i++)
        {
            if (string.Equals(Names[i], text, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }
}

/// <summary>Rules for machine profiles and the machine data on equipment (D-041).</summary>
public static partial class MachineProfileRules
{
    /// <summary>A new piece of equipment's micro-stop threshold.</summary>
    public static readonly TimeSpan DefaultMicroStop = TimeSpan.FromMinutes(5);

    /// <summary>The longest micro-stop threshold allowed, matching <c>equipment_micro_stop_ck</c>.</summary>
    public static readonly TimeSpan MaxMicroStop = TimeSpan.FromHours(1);

    /// <summary>Upper-case letters, digits and '-', '_', '.', up to 20.</summary>
    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    /// <summary>The states of a new profile: PackML's own numbers and names.</summary>
    public static IReadOnlyList<(int RawValue, string Name, int PackMlState)> StandardStates { get; } =
        [.. PackMlStates.Names.Select((name, i) => (i, name, i))];

    /// <exception cref="DomainRuleException">The threshold is negative or longer than an hour.</exception>
    public static void EnsureMicroStop(TimeSpan threshold)
    {
        if (threshold < TimeSpan.Zero || threshold > MaxMicroStop)
        {
            throw new DomainRuleException("A micro-stop threshold is between 0 and 60 minutes.");
        }
    }

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,19}$")]
    private static partial Regex CodePattern();
}
