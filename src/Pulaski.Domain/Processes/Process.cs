namespace Pulaski.Domain.Processes;

/// <summary>A reusable process route for a site, such as "Extrude and Pack" (D-023). Its content lives in versions.</summary>
public sealed record Process(Guid Id, Guid SiteId, string Code, string Name, string? Description, bool IsActive);

/// <summary>
/// One version of a process (D-023). A draft is edited freely; a released version is fixed, and
/// what orders are made from; releasing a newer one makes it obsolete. Changing a released
/// process means starting a new draft from it.
/// </summary>
public sealed record ProcessVersion(
    Guid Id,
    Guid ProcessId,
    int Number,
    string Status,
    ProcessDefinition Definition,
    DateTimeOffset? ReleasedAt,
    string? Note)
{
    public static ProcessVersion FirstDraft(Guid id, Guid processId) =>
        new(id, processId, 1, ProcessVersionStatus.Draft, ProcessDefinition.Empty, null, null);

    /// <exception cref="DomainRuleException">The version is not a draft, or the definition is malformed.</exception>
    public ProcessVersion SaveDraft(ProcessDefinition definition)
    {
        EnsureDraft("changed");
        if (definition.StructuralProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(problems[0].Message);
        }

        return this with { Definition = definition };
    }

    /// <summary>Fixes the version for use by orders. The caller checks the equipment it names.</summary>
    /// <exception cref="DomainRuleException">The version is not a draft, or the definition is incomplete.</exception>
    public ProcessVersion Release(DateTimeOffset at, string? note)
    {
        EnsureDraft("released");
        if (Definition.ReleaseProblems() is { Count: > 0 } problems)
        {
            throw new DomainRuleException(string.Join(" ", problems.Select(p => p.Message)));
        }

        return this with { Status = ProcessVersionStatus.Released, ReleasedAt = at, Note = note };
    }

    /// <exception cref="DomainRuleException">The version is not released.</exception>
    public ProcessVersion MakeObsolete()
    {
        if (Status != ProcessVersionStatus.Released)
        {
            throw new DomainRuleException($"Version {Number} is {Status}; only a released version becomes obsolete.");
        }

        return this with { Status = ProcessVersionStatus.Obsolete };
    }

    /// <summary>
    /// The next draft, a copy of this version. Operations and links get new ids, since a released
    /// version's operations are kept as rows that jobs will point to.
    /// </summary>
    public ProcessVersion NextDraft(Guid id, Func<Guid> newId)
    {
        var ids = Definition.Operations.ToDictionary(o => o.Id, _ => newId());
        var definition = new ProcessDefinition(
            [.. Definition.Operations.Select(o => o with { Id = ids[o.Id] })],
            [.. Definition.Dependencies.Select(d => d with
            {
                Id = newId(),
                FromOperationId = ids[d.FromOperationId],
                ToOperationId = ids[d.ToOperationId],
            })]);
        return new ProcessVersion(id, ProcessId, Number + 1, ProcessVersionStatus.Draft, definition, null, null);
    }

    private void EnsureDraft(string action)
    {
        if (Status != ProcessVersionStatus.Draft)
        {
            throw new DomainRuleException($"Version {Number} is {Status}; start a new draft to have it {action}.");
        }
    }
}
