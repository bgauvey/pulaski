namespace Pulaski.Domain.Quality.Spc;

/// <summary>
/// The Nelson rules for a control chart (D-099), numbered as Nelson numbered them. Each characteristic checks rule 1 and those it chooses; until
/// chosen, the Western Electric set: 1, 2, 5 and 6.
/// </summary>
public static class RunRules
{
    /// <summary>One point beyond 3 sigma (beyond the control limits). Always checked.</summary>
    public const int BeyondLimits = 1;

    /// <summary>Nine points in a row on one side of the centre line.</summary>
    public const int RunOfNine = 2;

    /// <summary>Six points in a row steadily increasing or decreasing.</summary>
    public const int TrendOfSix = 3;

    /// <summary>Fourteen points in a row alternating up and down.</summary>
    public const int Alternating = 4;

    /// <summary>Two of three points in a row beyond 2 sigma on the same side.</summary>
    public const int TwoOfThree = 5;

    /// <summary>Four of five points in a row beyond 1 sigma on the same side.</summary>
    public const int FourOfFive = 6;

    /// <summary>Fifteen points in a row within 1 sigma of the centre line (stratification).</summary>
    public const int Hugging = 7;

    /// <summary>Eight points in a row beyond 1 sigma, on either side (mixture).</summary>
    public const int Mixture = 8;

    public static IReadOnlyList<int> All { get; } = [1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>The Western Electric set in Nelson's numbering.</summary>
    public static IReadOnlyList<int> Default { get; } = [BeyondLimits, RunOfNine, TwoOfThree, FourOfFive];

    /// <summary>The rules to store: rule 1 always, the rest known, in order, once each.</summary>
    /// <exception cref="DomainRuleException">A rule isn't one of Nelson's 1–8.</exception>
    public static IReadOnlyList<int> Normalize(IEnumerable<int> rules)
    {
        var chosen = rules.ToHashSet();
        if (chosen.Any(r => !All.Contains(r)))
        {
            throw new DomainRuleException("Run rules are Nelson's 1 to 8.");
        }

        chosen.Add(BeyondLimits);
        return [.. chosen.Order()];
    }

    /// <summary>
    /// The rules each point breaks, by index, marked at the point that completes the pattern. Zone rules measure each point against its own centre
    /// line and sigma, so a run can span a change of limits; a point without limits (or without a value) breaks every run.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<int>> Check(IReadOnlyList<RulePoint> points, IReadOnlyCollection<int> rules)
    {
        var found = points.Select(_ => new List<int>()).ToArray();
        var z = points.Select(p => p is { Value: { } v, Center: { } c, Sigma: { } s } && s > 0 ? (v - c) / s : (double?)null).ToArray();
        for (var i = 0; i < points.Count; i++)
        {
            void Mark(int rule)
            {
                if (rules.Contains(rule))
                {
                    found[i].Add(rule);
                }
            }

            if (z[i] is { } zi)
            {
                if (Math.Abs(zi) > 3)
                {
                    Mark(BeyondLimits);
                }

                if (Run(z, i, 9, x => x > 0) || Run(z, i, 9, x => x < 0))
                {
                    Mark(RunOfNine);
                }

                if (zi > 2 && Count(z, i, 3, x => x > 2) >= 2 || zi < -2 && Count(z, i, 3, x => x < -2) >= 2)
                {
                    Mark(TwoOfThree);
                }

                if (zi > 1 && Count(z, i, 5, x => x > 1) >= 4 || zi < -1 && Count(z, i, 5, x => x < -1) >= 4)
                {
                    Mark(FourOfFive);
                }

                if (Run(z, i, 15, x => Math.Abs(x) < 1))
                {
                    Mark(Hugging);
                }

                if (Run(z, i, 8, x => Math.Abs(x) > 1))
                {
                    Mark(Mixture);
                }
            }

            // Trends and alternation look at the values alone.
            if (Trend(points, i, 6))
            {
                Mark(TrendOfSix);
            }

            if (Alternates(points, i, 14))
            {
                Mark(Alternating);
            }
        }

        return found;
    }

    // The n points ending at i all have a zone and all pass.
    private static bool Run(double?[] z, int i, int n, Func<double, bool> test)
    {
        if (i + 1 < n)
        {
            return false;
        }

        for (var k = i - n + 1; k <= i; k++)
        {
            if (z[k] is not { } x || !test(x))
            {
                return false;
            }
        }

        return true;
    }

    // Of the n points ending at i, how many pass; 0 if any of them has no zone.
    private static int Count(double?[] z, int i, int n, Func<double, bool> test)
    {
        if (i + 1 < n)
        {
            return 0;
        }

        var count = 0;
        for (var k = i - n + 1; k <= i; k++)
        {
            if (z[k] is not { } x)
            {
                return 0;
            }

            count += test(x) ? 1 : 0;
        }

        return count;
    }

    private static bool Trend(IReadOnlyList<RulePoint> points, int i, int n)
    {
        if (i + 1 < n || points.Skip(i - n + 1).Take(n).Any(p => p.Value is null))
        {
            return false;
        }

        var window = points.Skip(i - n + 1).Take(n).Select(p => p.Value!.Value).ToList();
        var steps = window.Zip(window.Skip(1), (a, b) => b - a).ToList();
        return steps.All(d => d > 0) || steps.All(d => d < 0);
    }

    private static bool Alternates(IReadOnlyList<RulePoint> points, int i, int n)
    {
        if (i + 1 < n || points.Skip(i - n + 1).Take(n).Any(p => p.Value is null))
        {
            return false;
        }

        var window = points.Skip(i - n + 1).Take(n).Select(p => p.Value!.Value).ToList();
        var steps = window.Zip(window.Skip(1), (a, b) => Math.Sign(b - a)).ToList();
        return steps.All(s => s != 0) && steps.Zip(steps.Skip(1), (a, b) => a != b).All(x => x);
    }
}

/// <summary>A point as the rules see it: its value, and its centre line and sigma (null without limits).</summary>
public sealed record RulePoint(double? Value, double? Center, double? Sigma);
