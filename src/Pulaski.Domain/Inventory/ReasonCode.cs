using System.Text.RegularExpressions;

namespace Pulaski.Domain.Inventory;

/// <summary>
/// A reason people choose (D-030): for an adjustment, a hold, a machine's utilization and so on. The code and
/// the kind never change once created, since history refers to them; the rest can. A utilization reason has a
/// class (D-041), which may change: OEE is worked out from it on read.
/// </summary>
public sealed partial record ReasonCode(
    Guid Id,
    Guid? SiteId,
    string Kind,
    string Code,
    string Name,
    bool RequiresComment,
    int SortOrder,
    bool IsActive,
    string? UtilizationClass = null)
{
    public const string Utilization = "utilization";

    /// <summary>Values of <c>core.reason_code.reason_kind</c>.</summary>
    public static IReadOnlyList<string> Kinds { get; } =
        ["inventory_adjustment", "hold", "utilization", "scrap", "indirect_labor", "run_end", "spc_cause", "sample_exclusion"];

    /// <summary>Letters, digits and '-', '_', '.', up to 30, as the seed's codes are.</summary>
    public static bool IsValidCode(string code) => CodePattern().IsMatch(code);

    /// <summary>A utilization reason has a class (D-041); no other kind has one.</summary>
    public static bool IsValidClass(string kind, string? utilizationClass) =>
        kind == Utilization ? utilizationClass is not null && MachineData.UtilizationClasses.All.Contains(utilizationClass) : utilizationClass is null;

    [GeneratedRegex("^[A-Z0-9][A-Z0-9._-]{0,29}$")]
    private static partial Regex CodePattern();
}
