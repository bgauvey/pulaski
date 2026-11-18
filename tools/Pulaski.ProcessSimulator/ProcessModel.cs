namespace Pulaski.ProcessSimulator;

/// <summary>One simulated process value: a target with normal common-cause noise, rounded as a gauge would read it.</summary>
public sealed record ProcessVariable(string Metric, double Target, double Sigma, int Decimals);

/// <summary>
/// A special cause, now and then: for <see cref="Length"/> at the end of every <see cref="Every"/>, the variable's mean moves by
/// <see cref="SizeInSigma"/> of its sigma, enough for a control chart's run rules to notice, then the process recovers. Stable from the
/// start for <c>Every - Length</c>, long enough to see the chart in control first. <see cref="Every"/> of zero means never.
/// </summary>
public sealed record ProcessShift(string Metric, TimeSpan Every, TimeSpan Length, double SizeInSigma)
{
    public bool IsActive(TimeSpan sinceStart) =>
        Every > TimeSpan.Zero && Length > TimeSpan.Zero
        && TimeSpan.FromTicks(sinceStart.Ticks % Every.Ticks) >= Every - Length;
}

/// <summary>The simulated extruder's readings at a moment.</summary>
public sealed class ProcessModel(IReadOnlyList<ProcessVariable> variables, ProcessShift? shift, Random random)
{
    /// <summary>The demo extruder: melt temperature in °C and the profile's width in mm.</summary>
    public static IReadOnlyList<ProcessVariable> Extruder { get; } =
    [
        new("Process/MeltTemperature", 215.0, 1.2, 1),
        new("Process/ProfileWidth", 42.00, 0.04, 2),
    ];

    public IReadOnlyList<ProcessVariable> Variables => variables;

    public IReadOnlyList<(string Metric, double Value)> Read(TimeSpan sinceStart) =>
        [.. variables.Select(v => (v.Metric, Math.Round(Mean(v, sinceStart) + (v.Sigma * Normal()), v.Decimals)))];

    public double Mean(ProcessVariable variable, TimeSpan sinceStart) =>
        shift is not null && shift.Metric == variable.Metric && shift.IsActive(sinceStart)
            ? variable.Target + (shift.SizeInSigma * variable.Sigma)
            : variable.Target;

    // Box-Muller: a standard normal from two uniforms.
    private double Normal()
    {
        var u1 = 1.0 - random.NextDouble();
        var u2 = random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
