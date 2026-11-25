using Pulaski.Domain.Security;

namespace Pulaski.Domain.Alerts;

/// <summary>
/// What an e-mail alert is about (#121, D-109): its code (<c>core.alert_subscription.kind</c>), what it says, whether it takes a number of
/// minutes and what it defaults to, and the view permission the subscriber needs at the site, checked again when it is sent.
/// </summary>
public sealed record AlertKind(string Code, string Name, string? MinutesMeans, int? DefaultMinutes, string ViewPermission, bool IsBuilt)
{
    public const string LongStop = "long_stop";
    public const string UncodedStop = "uncoded_stop";
    public const string AndonEscalated = "andon_escalated";
    public const string SpcAlert = "spc_alert";
    public const string LotHold = "lot_hold";
    public const string Shortage = "shortage";

    /// <summary>Every kind, in the order offered; one not offered (<see cref="IsBuilt"/> false) keeps its rows valid but takes no new ones.</summary>
    public static readonly IReadOnlyList<AlertKind> All =
    [
        new(LongStop, Term.Mark("A machine stopped longer than"), Term.Mark("minutes stopped"), 15, Permissions.MachinesView, true),
        new(UncodedStop, Term.Mark("A stop still without a reason"), Term.Mark("minutes after it ended"), 30, Permissions.MachinesView, true),
        new(AndonEscalated, Term.Mark("An andon call escalated"), null, null, Permissions.FloorView, true),
        new(SpcAlert, Term.Mark("A control chart signalled (SPC alert)"), null, null, Permissions.QualityView, true),
        new(LotHold, Term.Mark("A lot put on hold"), null, null, Permissions.InventoryView, true),
        new(Shortage, Term.Mark("A job will be short of a component"), Term.Mark("minutes before it is needed"), 480, Permissions.InventoryView, true),
    ];

    public static AlertKind? Find(string? code) => All.FirstOrDefault(k => k.Code == code);
}

/// <summary>A user's subscription to an alert at a site (#121, D-109). Only its minutes and whether it is on change.</summary>
public sealed record AlertSubscription(Guid Id, Guid UserId, Guid SiteId, Guid? EquipmentId, string Kind, int? Minutes, bool IsActive)
{
    public const int MaxMinutes = 10080;

    /// <exception cref="DomainRuleException">The kind isn't offered, or its minutes can't be used.</exception>
    public static AlertSubscription Create(Guid id, Guid userId, Guid siteId, Guid? equipmentId, string kind, int? minutes)
    {
        var known = AlertKind.Find(kind) is { IsBuilt: true } k ? k : throw new DomainRuleException("Choose one of the alerts offered.");
        return new AlertSubscription(id, userId, siteId, equipmentId, known.Code, null, true).Edit(minutes, true);
    }

    /// <exception cref="DomainRuleException">The minutes can't be used for this kind.</exception>
    public AlertSubscription Edit(int? minutes, bool isActive)
    {
        var kind = AlertKind.Find(Kind) ?? throw new DomainRuleException("This alert is no longer offered.");
        if (kind.MinutesMeans is null)
        {
            return this with { Minutes = null, IsActive = isActive };
        }

        var value = minutes ?? kind.DefaultMinutes;
        if (value is not { } m || m < 1 || m > MaxMinutes)
        {
            throw new DomainRuleException($"Give the {kind.MinutesMeans} as 1 to {MaxMinutes} (a week).");
        }

        return this with { Minutes = m, IsActive = isActive };
    }
}
