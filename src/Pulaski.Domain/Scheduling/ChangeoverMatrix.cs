namespace Pulaski.Domain.Scheduling;

/// <summary>A material as the changeover matrix sees it: its id and optional class.</summary>
public sealed record MaterialRef(Guid Id, Guid? ClassId);

/// <summary>
/// One cell pattern of the changeover matrix (<c>core.changeover_rule</c>). Each side matches a
/// material, a material class, or anything when both are null. <see cref="EquipmentId"/> null
/// means every line in the site.
/// </summary>
public sealed record ChangeoverRule(
    Guid Id,
    Guid? EquipmentId,
    Guid? FromMaterialId,
    Guid? FromMaterialClassId,
    Guid? ToMaterialId,
    Guid? ToMaterialClassId,
    TimeSpan Duration);

/// <summary>Where a changeover duration came from.</summary>
public static class ChangeoverSource
{
    /// <summary>Same material: nothing to change.</summary>
    public const string None = "none";

    /// <summary>A matrix rule matched; see <see cref="ChangeoverResolution.RuleId"/>.</summary>
    public const string Rule = "rule";

    /// <summary>No rule matched; the line's standard changeover for the incoming material applied.</summary>
    public const string Standard = "standard";

    /// <summary>No rule and no standard: assumed zero.</summary>
    public const string Unknown = "unknown";
}

public sealed record ChangeoverResolution(TimeSpan Duration, string Source, Guid? RuleId);

/// <summary>
/// Resolves the changeover time between two materials on one line (D-015).
/// <para>
/// The most specific matching rule wins: first by how exactly both sides match (material beats
/// class beats anything), then by how exactly the incoming side matches (it usually dictates the
/// setup), then a rule for this line over a site-wide one, and finally the longer duration, so ties
/// are resolved conservatively and the result never depends on load order.
/// </para>
/// </summary>
public sealed class ChangeoverMatrix
{
    private readonly ChangeoverRule[] _rules;
    private readonly IReadOnlyDictionary<Guid, TimeSpan> _standardByMaterial;

    /// <param name="lineId">The line; rules for other lines are ignored.</param>
    /// <param name="rules">Active rules for the line's site.</param>
    /// <param name="standardByMaterial">The capability's <c>standard_changeover</c> per incoming material.</param>
    public ChangeoverMatrix(
        Guid lineId,
        IEnumerable<ChangeoverRule> rules,
        IReadOnlyDictionary<Guid, TimeSpan>? standardByMaterial = null)
    {
        _rules = [.. rules.Where(r => r.EquipmentId is null || r.EquipmentId == lineId)];
        _standardByMaterial = standardByMaterial ?? new Dictionary<Guid, TimeSpan>();
        LineId = lineId;
    }

    public static ChangeoverMatrix Empty(Guid lineId) => new(lineId, []);

    public Guid LineId { get; }

    public ChangeoverResolution Resolve(MaterialRef from, MaterialRef to)
    {
        if (from.Id == to.Id)
        {
            return new ChangeoverResolution(TimeSpan.Zero, ChangeoverSource.None, null);
        }

        ChangeoverRule? best = null;
        (int Total, int To, int Line, TimeSpan Duration) bestKey = default;

        foreach (var rule in _rules)
        {
            var fromScore = Score(rule.FromMaterialId, rule.FromMaterialClassId, from);
            var toScore = Score(rule.ToMaterialId, rule.ToMaterialClassId, to);
            if (fromScore < 0 || toScore < 0)
            {
                continue;
            }

            var key = (fromScore + toScore, toScore, rule.EquipmentId is null ? 0 : 1, rule.Duration);
            if (best is null || key.CompareTo(bestKey) > 0)
            {
                best = rule;
                bestKey = key;
            }
        }

        if (best is not null)
        {
            return new ChangeoverResolution(best.Duration, ChangeoverSource.Rule, best.Id);
        }

        return _standardByMaterial.TryGetValue(to.Id, out var standard)
            ? new ChangeoverResolution(standard, ChangeoverSource.Standard, null)
            : new ChangeoverResolution(TimeSpan.Zero, ChangeoverSource.Unknown, null);
    }

    /// <returns>2 for a material match, 1 for a class match, 0 for a wildcard, -1 for no match.</returns>
    private static int Score(Guid? materialId, Guid? classId, MaterialRef material) =>
        (materialId, classId) switch
        {
            ({ } m, _) => m == material.Id ? 2 : -1,
            (null, { } c) => c == material.ClassId ? 1 : -1,
            _ => 0,
        };
}
