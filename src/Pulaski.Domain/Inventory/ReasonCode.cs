using System.Text.RegularExpressions;

namespace Pulaski.Domain.Inventory;

/// <summary>
/// A reason people choose (D-030): for an adjustment, a hold, downtime and so on. The code and the
/// kind never change once created, since history refers to them; the rest can.
/// </summary>
public sealed partial record ReasonCode(
    Guid Id,
    Guid? SiteId,
    string Kind,
    string Code,
    string Name,
    bool RequiresComment,
    int SortOrder,
    bool IsActive)
{
    /// <summary>Values of <c>core.reason_code.reason_kind</c>.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        ["inventory_adjustment", "hold", "downtime", "scrap", "indirect_labor", "run_end", "spc_cause", "sample_exclusion"];

    /// <summary>Letters, digits and '-', '_', '.', up to 30, as the seed's codes are.</summary>
    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,29}$")]
    private static partial Regex CodePattern();
}
