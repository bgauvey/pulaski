namespace Pulaski.Domain.Boms;

/// <summary>
/// A bill of materials for one material, optionally for one process (D-025). Its content lives in
/// versions; there is at most one BOM per material and process, and one with no process.
/// </summary>
public sealed record Bom(Guid Id, Guid MaterialId, Guid? ProcessId, bool IsActive);

/// <summary>
/// One version of a BOM (D-025), as process versions (D-023): a draft is edited freely; a released
/// version is fixed and what orders are planned with; releasing a newer one makes it obsolete.
/// </summary>
public sealed record BomVersion(
    Guid Id,
    Guid BomId,
    int Number,
    string Status,
    BomDefinition Definition,
    DateTimeOffset? ReleasedAt,
    string? Note)
{
    /// <summary>An empty first draft per one of <paramref name="baseUomId"/>, the material's base unit.</summary>
    public static BomVersion FirstDraft(Guid id, Guid bomId, Guid baseUomId) =>
        new(id, bomId, 1, BomVersionStatus.Draft, new BomDefinition(1m, baseUomId, []), null, null);

    /// <exception cref="DomainRuleException">The version is not a draft, or the definition is malformed.</exception>
    public BomVersion SaveDraft(BomDefinition definition)
    {
        EnsureDraft("changed");
        if (definition.StructuralProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(problems[0].Message);
        }

        return this with { Definition = definition };
    }

    /// <summary>Fixes the version for orders. The caller checks the components and operations.</summary>
    /// <exception cref="DomainRuleException">The version is not a draft, or the definition is incomplete.</exception>
    public BomVersion Release(DateTimeOffset at, string? note)
    {
        EnsureDraft("released");
        if (Definition.ReleaseProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(string.Join(" ", problems.Select(p => p.Message)));
        }

        return this with { Status = BomVersionStatus.Released, ReleasedAt = at, Note = note };
    }

    /// <exception cref="DomainRuleException">The version is not released.</exception>
    public BomVersion MakeObsolete()
    {
        if (Status != BomVersionStatus.Released)
        {
            throw new DomainRuleException($"Version {Number} is {Status}; only a released version becomes obsolete.");
        }

        return this with { Status = BomVersionStatus.Obsolete };
    }

    /// <summary>The next draft, a copy of this version with new item ids, since released items are rows that planned components point to.</summary>
    public BomVersion NextDraft(Guid id, Func<Guid> newId) =>
        new(id, BomId, Number + 1, BomVersionStatus.Draft,
            Definition with { Items = [.. Definition.Items.Select(i => i with { Id = newId() })] }, null, null);

    private void EnsureDraft(string action)
    {
        if (Status != BomVersionStatus.Draft)
        {
            throw new DomainRuleException($"Version {Number} is {Status}; start a new draft to have it {action}.");
        }
    }
}
