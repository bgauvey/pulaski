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

    /// <summary>Every kind, in the order offered; those not yet sent (#165) are kept so their rows stay valid, but not offered.</summary>
    public static readonly IReadOnlyList<AlertKind> All =
    [
        new(LongStop, "A machine stopped longer than", "minutes stopped", 15, Permissions.MachinesView, true),
        new(UncodedStop, "A stop still without a reason", "minutes after it ended", 30, Permissions.MachinesView, true),
        new(AndonEscalated, "An andon call escalated", null, null, Permissions.FloorView, true),
        new(SpcAlert, "An SPC alert", null, null, Permissions.QualityView, false),
        new(LotHold, "A lot put on hold", null, null, Permissions.InventoryView, false),
        new(Shortage, "A shortage due soon", "minutes ahead", 240, Permissions.InventoryView, false),
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
