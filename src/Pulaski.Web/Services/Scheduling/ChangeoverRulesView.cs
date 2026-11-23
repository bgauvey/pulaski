using System.Globalization;
using Pulaski.Contracts.Scheduling;
using Pulaski.Web.Services.Localization;

namespace Pulaski.Web.Services.Scheduling;

/// <summary>Changeover rules on screen (D-074): how a rule reads, filtering, and checking the form before it is sent.</summary>
public static class ChangeoverRulesView
{
    public const int MaxMinutes = 7 * 24 * 60;

    /// <summary>What a side of a rule may name.</summary>
    public const string Anything = "any";
    public const string Material = "material";
    public const string Class = "class";

    public static string From(Text l, ChangeoverRuleDto r) => Side(l, r.FromMaterialCode, r.FromMaterialClassCode);

    public static string To(Text l, ChangeoverRuleDto r) => Side(l, r.ToMaterialCode, r.ToMaterialClassCode);

    public static string Line(Text l, ChangeoverRuleDto r) => r.EquipmentCode ?? l["All lines"];

    /// <summary>"45 min", "2 h", "1 h 30 min".</summary>
    public static string Duration(int minutes) => (minutes / 60, minutes % 60) switch
    {
        (0, var m) => $"{m} min",
        (var h, 0) => $"{h} h",
        (var h, var m) => $"{h} h {m} min",
    };

    /// <summary>Rules whose line is <paramref name="lineId"/> (or every line) and whose sides mention <paramref name="search"/>, switched-off ones only when asked.</summary>
    public static IReadOnlyList<ChangeoverRuleDto> Filter(IEnumerable<ChangeoverRuleDto> rules, Guid? lineId, string? search, bool includeOff)
    {
        var text = search?.Trim() ?? "";
        return [.. rules.Where(r =>
            (includeOff || r.IsActive)
            && (lineId is null || r.EquipmentId is null || r.EquipmentId == lineId)
            && (text.Length == 0
                || Contains(r.FromMaterialCode, text) || Contains(r.FromMaterialName, text) || Contains(r.FromMaterialClassCode, text)
                || Contains(r.ToMaterialCode, text) || Contains(r.ToMaterialName, text) || Contains(r.ToMaterialClassCode, text)))];
    }

    /// <summary>Why the form can't be sent yet, or null.</summary>
    public static string? Problem(Text l, string fromKind, Guid? fromId, string toKind, Guid? toId, int? minutes)
    {
        if ((fromKind != Anything && fromId is null) || (toKind != Anything && toId is null))
        {
            return l["Choose the material or class on each side, or Anything."];
        }

        if (fromKind == Material && toKind == Material && fromId == toId)
        {
            return l["From a material to itself is no changeover."];
        }

        return minutes is not { } m || m < 0 || m > MaxMinutes ? l["Give the time in whole minutes, from 0 to {0} (7 days).", MaxMinutes] : null;
    }

    private static string Side(Text l, string? material, string? materialClass) =>
        material ?? (materialClass is { } c ? l["Class {0}", c] : l["Anything"]);

    private static bool Contains(string? value, string text) => value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    public static string Minutes(int value) => value.ToString(CultureInfo.InvariantCulture);
}
