namespace Pulaski.Domain.Maintenance;

/// <summary>What a preventive plan counts (#127d, D-115). Values match <c>core.maintenance_plan.meter</c>.</summary>
public static class MaintenanceMeters
{
    /// <summary>Hours the equipment spent executing (PackML Execute), from its machine state.</summary>
    public const string RunHours = "run_hours";

    /// <summary>What its machine counted (processed counts from its counter tag).</summary>
    public const string Counts = "counts";

    public static IReadOnlyList<string> All { get; } = [RunHours, Counts];
}

/// <summary>
/// Preventive maintenance for a line or machine (#127d, D-115): every so many run hours or machine counts, a request is raised with the plan's
/// name as what needs doing. Its meter counts from <see cref="StartedAt"/>, or from when its last request was closed if later; while a request of
/// the plan is open, no other is raised. The meter is fixed once made; switched off rather than deleted, since requests refer to it.
/// </summary>
public sealed record MaintenancePlan(
    Guid Id, Guid SiteId, Guid EquipmentId, string Name, string Meter, decimal Interval, string Priority, bool IsActive, DateTimeOffset StartedAt)
{
    public const int MaxName = 200;
    public const decimal MaxRunHours = 100_000m;
    public const decimal MaxCounts = 1_000_000_000m;

    /// <exception cref="DomainRuleException">The meter, name, interval or priority can't be used.</exception>
    public static MaintenancePlan Create(Guid id, Guid siteId, Guid equipmentId, string? name, string? meter, decimal interval, string? priority, DateTimeOffset at)
    {
        var chosen = (meter ?? "").Trim();
        if (!MaintenanceMeters.All.Contains(chosen))
        {
            throw new DomainRuleException($"'{chosen}' isn't a meter; use run_hours or counts.");
        }

        return new MaintenancePlan(id, siteId, equipmentId, "", chosen, 1m, MaintenancePriorities.Normal, true, at).Edit(name, interval, priority, true, at);
    }

    /// <summary>A new name, interval, priority or whether it is on. Switching it on again starts its meter from <paramref name="at"/>.</summary>
    /// <exception cref="DomainRuleException">The name, interval or priority can't be used.</exception>
    public MaintenancePlan Edit(string? name, decimal interval, string? priority, bool isActive, DateTimeOffset at)
    {
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxName)
        {
            throw new DomainRuleException($"Say what is done, in at most {MaxName} characters, such as Grease the gearbox.");
        }

        var most = Meter == MaintenanceMeters.RunHours ? MaxRunHours : MaxCounts;
        if (interval <= 0 || interval > most)
        {
            throw Meter == MaintenanceMeters.RunHours
                ? new DomainRuleException($"Every 1 to {MaxRunHours:0} run hours.")
                : new DomainRuleException($"Every 1 to {MaxCounts:0} counts.");
        }

        var chosen = string.IsNullOrWhiteSpace(priority) ? MaintenancePriorities.Normal : priority.Trim();
        if (!MaintenancePriorities.All.Contains(chosen))
        {
            throw new DomainRuleException($"'{chosen}' isn't a priority; use low, normal or high.");
        }

        return this with
        {
            Name = trimmed,
            Interval = interval,
            Priority = chosen,
            IsActive = isActive,
            StartedAt = isActive && !IsActive ? at : StartedAt,
        };
    }

    /// <summary>Where its meter counts from: its start, or when its last request was closed if later.</summary>
    public DateTimeOffset CountsFrom(DateTimeOffset? lastClosedAt) => lastClosedAt is { } closed && closed > StartedAt ? closed : StartedAt;

    /// <summary>Whether <paramref name="reading"/> (the meter since <see cref="CountsFrom"/>) has reached the interval.</summary>
    public bool IsDue(decimal reading) => IsActive && reading >= Interval;
}
