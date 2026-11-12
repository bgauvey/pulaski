namespace Pulaski.Domain.Quality;

/// <summary>What kind of result a characteristic takes (#65). Values match <c>core.quality_characteristic.data_kind</c>.</summary>
public static class CharacteristicKinds
{
    /// <summary>Measured values (a diameter, a melt flow index), checked against lower and upper limits.</summary>
    public const string Variable = "variable";

    /// <summary>Inspected items and how many were defective, checked against the most defects allowed.</summary>
    public const string Attribute = "attribute";

    public static IReadOnlyList<string> All { get; } = [Variable, Attribute];
}

/// <summary>
/// Something measured or inspected (#65, D-085), enterprise-wide so any material's specification can name it. Code and kind are fixed once
/// created, since results and specifications depend on them; it is switched off, never deleted.
/// </summary>
public sealed record Characteristic(Guid Id, string Code, string Name, string? Description, string DataKind, Guid? UomId, int Decimals, bool IsActive)
{
    public const int MaxCode = 30;
    public const int MaxName = 100;
    public const int MaxDescription = 500;

    private readonly string? _chartType;

    /// <summary>
    /// The control chart its results are drawn on (D-099); until chosen, individuals for a variable and proportion defective for an attribute.
    /// </summary>
    public string ChartType
    {
        get => _chartType ?? DefaultChart(DataKind);
        init => _chartType = value;
    }

    /// <summary>Values a subgroup holds on its chart: 1 for individuals, 2 to 25 for X-bar charts; for np and c charts, items inspected a sample.</summary>
    public int SubgroupSize { get; init; } = 1;

    public static string DefaultChart(string dataKind) => dataKind == CharacteristicKinds.Variable ? Spc.ChartTypes.IMr : Spc.ChartTypes.P;

    /// <returns>Why the code isn't acceptable, or null: 1 to 30 upper-case letters, digits, '-' or '_', starting with a letter or digit.</returns>
    public static string? CodeProblem(string code) =>
        code.Length is < 1 or > MaxCode
            ? $"Use 1 to {MaxCode} characters."
            : !char.IsAsciiLetterOrDigit(code[0]) || !code.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c is '-' or '_')
                ? "Upper-case letters, digits, '-' and '_' only."
                : null;
}

/// <summary>Specification version lifecycle (#65), as BOMs (D-025). Values match <c>core.material_spec_version.status</c>.</summary>
public static class SpecVersionStatus
{
    public const string Draft = "draft";
    public const string Released = "released";
    public const string Obsolete = "obsolete";
}

/// <summary>A material's specification (#65, D-085): at most one per material; its content lives in versions.</summary>
public sealed record MaterialSpec(Guid Id, Guid MaterialId, bool IsActive);

/// <summary>
/// One characteristic of a specification and its limits. A variable has a lower and/or upper limit (and an optional target); an attribute
/// has the most defects a sample may have. <see cref="HoldOnFail"/>: a failing result for a lot puts the lot on hold.
/// </summary>
public sealed record SpecItem(
    Guid Id,
    Guid CharacteristicId,
    decimal? Lsl,
    decimal? Target,
    decimal? Usl,
    int? MaxDefects,
    bool HoldOnFail,
    int SortOrder);

/// <summary>Something wrong with a specification, pinned to the item it concerns when there is one.</summary>
public sealed record SpecProblem(string Code, string Message, Guid? ItemId = null);

/// <summary>A specification's characteristics and limits. A draft may be incomplete but never malformed; a release must be complete.</summary>
public sealed record SpecDefinition(IReadOnlyList<SpecItem> Items)
{
    public static SpecDefinition Empty { get; } = new([]);

    /// <summary>Problems that make the definition unusable even as a draft; kinds are checked against the characteristics by the caller.</summary>
    public IReadOnlyList<SpecProblem> StructuralProblems()
    {
        var problems = new List<SpecProblem>();
        var ids = new HashSet<Guid>();
        foreach (var item in Items)
        {
            if (!ids.Add(item.Id))
            {
                problems.Add(new("duplicate_item", "An item appears twice.", item.Id));
            }

            if (item.Lsl is { } lsl && item.Usl is { } usl && lsl > usl)
            {
                problems.Add(new("limits_order", "The lower limit is above the upper limit.", item.Id));
            }

            if (item.Target is { } target && (item.Lsl is { } low && target < low || item.Usl is { } high && target > high))
            {
                problems.Add(new("target_outside", "The target is outside the limits.", item.Id));
            }

            if (item.MaxDefects is < 0)
            {
                problems.Add(new("max_defects", "The most defects allowed can't be negative.", item.Id));
            }
        }

        foreach (var repeated in Items.GroupBy(i => i.CharacteristicId).Where(g => g.Count() > 1))
        {
            problems.Add(new("duplicate_characteristic", "The same characteristic is listed twice.", repeated.First().Id));
        }

        return problems;
    }

    /// <summary>Everything that stops a release on the definition alone.</summary>
    public IReadOnlyList<SpecProblem> ReleaseProblems()
    {
        var problems = StructuralProblems().ToList();
        if (Items.Count == 0)
        {
            problems.Add(new("no_items", "Add at least one characteristic."));
        }

        return problems;
    }

    /// <summary>What a characteristic of each kind needs: limits for a variable, the most defects for an attribute, never both.</summary>
    public static IReadOnlyList<SpecProblem> KindProblems(SpecItem item, Characteristic characteristic)
    {
        if (characteristic.DataKind == CharacteristicKinds.Variable)
        {
            return item.MaxDefects is not null
                ? [new("kind_mismatch", $"{characteristic.Code} is measured, so it takes limits, not a number of defects.", item.Id)]
                : item.Lsl is null && item.Usl is null
                    ? [new("no_limits", $"{characteristic.Code}: give a lower limit, an upper limit or both.", item.Id)]
                    : [];
        }

        return item.Lsl is not null || item.Usl is not null || item.Target is not null
            ? [new("kind_mismatch", $"{characteristic.Code} is inspected, so it takes the most defects allowed, not limits.", item.Id)]
            : item.MaxDefects is null
                ? [new("no_max_defects", $"{characteristic.Code}: give the most defects a sample may have (0 for none).", item.Id)]
                : [];
    }
}

/// <summary>
/// One version of a specification (#65), as BOM versions (D-025): a draft is edited freely; a released version is fixed and what results are
/// checked against; releasing a newer one makes it obsolete.
/// </summary>
public sealed record SpecVersion(
    Guid Id,
    Guid SpecId,
    int Number,
    string Status,
    SpecDefinition Definition,
    DateTimeOffset? ReleasedAt,
    string? ReleasedBy,
    string? Note)
{
    public static SpecVersion FirstDraft(Guid id, Guid specId) =>
        new(id, specId, 1, SpecVersionStatus.Draft, SpecDefinition.Empty, null, null, null);

    /// <exception cref="DomainRuleException">The version is not a draft, or the definition is malformed.</exception>
    public SpecVersion SaveDraft(SpecDefinition definition)
    {
        EnsureDraft("changed");
        if (definition.StructuralProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(problems[0].Message);
        }

        return this with { Definition = definition };
    }

    /// <summary>Fixes the version: results are checked against it from now on. The caller checks the characteristics.</summary>
    /// <exception cref="DomainRuleException">The version is not a draft, or the definition is incomplete.</exception>
    public SpecVersion Release(DateTimeOffset at, string by, string? note)
    {
        EnsureDraft("released");
        if (Definition.ReleaseProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(string.Join(" ", problems.Select(p => p.Message)));
        }

        return this with { Status = SpecVersionStatus.Released, ReleasedAt = at, ReleasedBy = by, Note = note };
    }

    /// <exception cref="DomainRuleException">The version is not released.</exception>
    public SpecVersion MakeObsolete() =>
        Status == SpecVersionStatus.Released
            ? this with { Status = SpecVersionStatus.Obsolete }
            : throw new DomainRuleException($"Version {Number} is {Status}; only a released version becomes obsolete.");

    /// <summary>The next draft: a copy with new item ids, since released items are rows that results point to.</summary>
    public SpecVersion NextDraft(Guid id, Func<Guid> newId) =>
        new(id, SpecId, Number + 1, SpecVersionStatus.Draft,
            new SpecDefinition([.. Definition.Items.Select(i => i with { Id = newId() })]), null, null, null);

    private void EnsureDraft(string action)
    {
        if (Status != SpecVersionStatus.Draft)
        {
            throw new DomainRuleException($"Version {Number} is {Status}; start a new draft to have it {action}.");
        }
    }
}
