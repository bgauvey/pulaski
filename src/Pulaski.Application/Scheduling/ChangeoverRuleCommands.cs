using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.Scheduling;
using Pulaski.Domain;
using Pulaski.Domain.Scheduling;

namespace Pulaski.Application.Scheduling;

/// <summary>Adds a rule to a site's changeover matrix (D-015, D-074).</summary>
public sealed record CreateChangeoverRuleCommand(
    Guid SiteId, Guid? EquipmentId, Guid? FromMaterialId, Guid? FromMaterialClassId, Guid? ToMaterialId, Guid? ToMaterialClassId, int DurationMinutes, string? Note)
    : ICommand<CreatedResponse>;

/// <summary>Changes a rule's time and note, or switches it off or on; what it matches is fixed.</summary>
public sealed record UpdateChangeoverRuleCommand(Guid Id, long ExpectedVersion, int DurationMinutes, string? Note, bool IsActive) : ICommand<VersionResponse>;

/// <summary>
/// Loads a site's changeover rules from a file (D-074), all or nothing: a row changes the rule with the same line and sides, else adds one.
/// <paramref name="DryRun"/> checks and reports, saving nothing.
/// </summary>
public sealed record ImportChangeoverRulesCommand(Guid SiteId, ChangeoverRuleImportSource Rows, bool DryRun) : ICommand<ImportMaterialsResponse>;

/// <summary>The site's changeover rules, those switched on first, by line, then sides.</summary>
public sealed record ListChangeoverRulesQuery(Guid SiteId) : IQuery<IReadOnlyList<ChangeoverRuleDto>>;

/// <summary>Write side for changeover rules. The site's row is locked first, so rule changes at a site serialize.</summary>
public interface IChangeoverRuleRepository
{
    /// <returns>False when the site isn't there.</returns>
    Task<bool> LockSiteAsync(Guid siteId, CancellationToken cancellationToken);

    Task<Guid?> SiteOfRuleAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>Every rule of the site, on or off, after the site is locked.</summary>
    Task<IReadOnlyList<Versioned<ChangeoverRuleRecord>>> ListSiteRulesAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>The site's active lines (equipment that can run jobs), by lower-case code.</summary>
    Task<IReadOnlyDictionary<string, Guid>> ListLinesAsync(Guid siteId, CancellationToken cancellationToken);

    /// <summary>Materials by lower-case code, for the codes given.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindMaterialsAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Material classes by lower-case code, for the codes given.</summary>
    Task<IReadOnlyDictionary<string, Guid>> FindClassesAsync(IReadOnlyCollection<string> codes, CancellationToken cancellationToken);

    /// <summary>Which of the ids are materials and which are material classes.</summary>
    Task<(IReadOnlySet<Guid> Materials, IReadOnlySet<Guid> Classes)> ExistingAsync(IReadOnlyCollection<Guid> materialIds, IReadOnlyCollection<Guid> classIds, CancellationToken cancellationToken);

    Task<long> InsertAsync(ChangeoverRuleRecord rule, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(ChangeoverRuleRecord rule, DateTimeOffset at, CancellationToken cancellationToken);
}

public interface IChangeoverRuleReadStore
{
    Task<IReadOnlyList<ChangeoverRuleDto>> ListAsync(Guid siteId, CancellationToken cancellationToken);
}

internal sealed class CreateChangeoverRuleHandler(
    IUnitOfWork unitOfWork, IChangeoverRuleRepository rules, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<CreateChangeoverRuleCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateChangeoverRuleCommand command, CancellationToken cancellationToken)
    {
        var pattern = new ChangeoverPattern(command.EquipmentId, command.FromMaterialId, command.FromMaterialClassId, command.ToMaterialId, command.ToMaterialClassId);
        var duration = TimeSpan.FromMinutes(command.DurationMinutes);
        var note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim();
        if (ChangeoverRules.Problem(pattern, duration, note) is { } problem)
        {
            throw new DomainRuleException(problem);
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await rules.LockSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        await ChangeoverRuleChecks.EnsureRefsAsync(rules, command.SiteId, pattern, cancellationToken);
        var site = await rules.ListSiteRulesAsync(command.SiteId, cancellationToken);
        ChangeoverRuleChecks.EnsureNoDuplicate(site.Select(r => r.Value), pattern, null);

        var rule = new ChangeoverRuleRecord(ids.NewId(), command.SiteId, pattern, duration, note, true);
        var version = await rules.InsertAsync(rule, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.ChangeoverRuleCreated, "changeover_rule", rule.Id, rule), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(rule.Id, version);
    }
}

internal sealed class UpdateChangeoverRuleHandler(
    IUnitOfWork unitOfWork, IChangeoverRuleRepository rules, IEventLog eventLog, IEventFactory events, TimeProvider time)
    : ICommandHandler<UpdateChangeoverRuleCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateChangeoverRuleCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var siteId = await rules.SiteOfRuleAsync(command.Id, cancellationToken) ?? throw new NotFoundException($"Changeover rule {command.Id} not found.");
        await rules.LockSiteAsync(siteId, cancellationToken);
        var site = await rules.ListSiteRulesAsync(siteId, cancellationToken);
        var current = site.FirstOrDefault(r => r.Value.Id == command.Id) ?? throw new NotFoundException($"Changeover rule {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException("The changeover rule was changed by someone else. Reload and try again.");
        }

        var after = current.Value with
        {
            Duration = TimeSpan.FromMinutes(command.DurationMinutes),
            Note = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim(),
            IsActive = command.IsActive,
        };
        if (ChangeoverRules.Problem(after.Pattern, after.Duration, after.Note) is { } problem)
        {
            throw new DomainRuleException(problem);
        }

        if (after == current.Value)
        {
            return new VersionResponse(current.Version);
        }

        if (after.IsActive)
        {
            ChangeoverRuleChecks.EnsureNoDuplicate(site.Select(r => r.Value), after.Pattern, after.Id);
        }

        var version = await rules.UpdateAsync(after, time.GetUtcNow(), cancellationToken);
        await eventLog.AppendAsync(events.Create(EventTypes.ChangeoverRuleUpdated, "changeover_rule", after.Id, new { Before = current.Value, After = after }), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new VersionResponse(version);
    }
}

internal sealed class ImportChangeoverRulesHandler(
    IUnitOfWork unitOfWork, IChangeoverRuleRepository rules, IEventLog eventLog, IEventFactory events, IIdGenerator ids, TimeProvider time)
    : ICommandHandler<ImportChangeoverRulesCommand, ImportMaterialsResponse>
{
    private const string Create = "create";
    private const string Update = "update";
    private const string Unchanged = "unchanged";
    private const string Error = "error";

    public async Task<ImportMaterialsResponse> HandleAsync(ImportChangeoverRulesCommand command, CancellationToken cancellationToken)
    {
        if (command.Rows.Problems.Count > 0)
        {
            return new ImportMaterialsResponse { FileProblems = command.Rows.Problems, Errors = command.Rows.Problems.Count };
        }

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await rules.LockSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        static string Key(string code) => code.Trim().ToLowerInvariant();
        var rows = command.Rows.Rows;
        var lines = await rules.ListLinesAsync(command.SiteId, cancellationToken);
        var materials = await rules.FindMaterialsAsync([.. rows.SelectMany(r => new[] { r.FromMaterial, r.ToMaterial }).Where(c => c.Length > 0).Select(Key).Distinct()], cancellationToken);
        var classes = await rules.FindClassesAsync([.. rows.SelectMany(r => new[] { r.FromClass, r.ToClass }).Where(c => c.Length > 0).Select(Key).Distinct()], cancellationToken);

        // The site's rules as the file leaves them, so later rows are checked against earlier ones.
        var site = (await rules.ListSiteRulesAsync(command.SiteId, cancellationToken)).ToDictionary(r => r.Value.Id, r => r.Value);
        var seen = new HashSet<ChangeoverPattern>();
        var results = new List<ImportRowResultDto>();
        var now = time.GetUtcNow();
        foreach (var row in rows)
        {
            try
            {
                if (row.Problems.Count > 0)
                {
                    throw new DomainRuleException(string.Join(" ", row.Problems));
                }

                Guid? Find(IReadOnlyDictionary<string, Guid> known, string code, string what) =>
                    code.Length == 0 ? null : known.TryGetValue(Key(code), out var id) ? id : throw new DomainRuleException($"There is no {what} {code}.");
                var pattern = new ChangeoverPattern(
                    Find(lines, row.LineCode, "line at this site"),
                    Find(materials, row.FromMaterial, "material"),
                    Find(classes, row.FromClass, "material class"),
                    Find(materials, row.ToMaterial, "material"),
                    Find(classes, row.ToClass, "material class"));
                if (!seen.Add(pattern))
                {
                    throw new DomainRuleException("The same line and sides appear earlier in the file.");
                }

                // A rule switched on wins over one switched off with the same pattern.
                var existing = site.Values.Where(r => r.Pattern == pattern).OrderByDescending(r => r.IsActive).FirstOrDefault();
                var duration = TimeSpan.FromMinutes(row.Minutes!.Value);
                var note = row.Note is { } supplied ? supplied.Value : existing?.Note;
                var active = row.IsActive ?? true;
                if (ChangeoverRules.Problem(pattern, duration, note) is { } problem)
                {
                    throw new DomainRuleException(problem);
                }

                if (existing is null)
                {
                    if (!active)
                    {
                        results.Add(Row(row, Unchanged, ["no such rule to switch off"]));
                        continue;
                    }

                    var rule = new ChangeoverRuleRecord(ids.NewId(), command.SiteId, pattern, duration, note, true);
                    site[rule.Id] = rule;
                    if (!command.DryRun)
                    {
                        await rules.InsertAsync(rule, now, cancellationToken);
                        await eventLog.AppendAsync(events.Create(EventTypes.ChangeoverRuleCreated, "changeover_rule", rule.Id, rule, reason: "CSV import"), cancellationToken);
                    }

                    results.Add(Row(row, Create, []));
                    continue;
                }

                var after = existing with { Duration = duration, Note = note, IsActive = active };
                var changes = Changes(existing, after);
                if (changes.Count == 0)
                {
                    results.Add(Row(row, Unchanged, []));
                    continue;
                }

                site[after.Id] = after;
                if (!command.DryRun)
                {
                    await rules.UpdateAsync(after, now, cancellationToken);
                    await eventLog.AppendAsync(events.Create(EventTypes.ChangeoverRuleUpdated, "changeover_rule", after.Id, new { Before = existing, After = after }, reason: "CSV import"),
                        cancellationToken);
                }

                results.Add(Row(row, Update, changes));
            }
            catch (DomainRuleException ex)
            {
                results.Add(new ImportRowResultDto { Line = row.Line, Code = row.Label, Action = Error, Message = ex.Message });
            }
        }

        var errors = results.Count(r => r.Action == Error);
        var apply = errors == 0 && !command.DryRun;
        if (apply)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ImportMaterialsResponse
        {
            Applied = apply,
            Created = results.Count(r => r.Action == Create),
            Updated = results.Count(r => r.Action == Update),
            Unchanged = results.Count(r => r.Action == Unchanged),
            Errors = errors,
            Rows = results,
        };
    }

    private static ImportRowResultDto Row(ChangeoverRuleImportRow row, string action, IReadOnlyList<string> changes) =>
        new() { Line = row.Line, Code = row.Label, Action = action, Changes = changes };

    private static List<string> Changes(ChangeoverRuleRecord before, ChangeoverRuleRecord after)
    {
        var changes = new List<string>();
        if (before.Duration != after.Duration)
        {
            changes.Add("minutes");
        }

        if (before.Note != after.Note)
        {
            changes.Add("note");
        }

        if (before.IsActive != after.IsActive)
        {
            changes.Add(after.IsActive ? "switched on" : "switched off");
        }

        return changes;
    }
}

internal sealed class ListChangeoverRulesHandler(IChangeoverRuleReadStore reads) : IQueryHandler<ListChangeoverRulesQuery, IReadOnlyList<ChangeoverRuleDto>>
{
    public Task<IReadOnlyList<ChangeoverRuleDto>> HandleAsync(ListChangeoverRulesQuery query, CancellationToken cancellationToken) =>
        reads.ListAsync(query.SiteId, cancellationToken);
}

/// <summary>Checks shared by adding and importing rules.</summary>
internal static class ChangeoverRuleChecks
{
    /// <summary>The line is an active line of the site, and the materials and classes exist.</summary>
    public static async Task EnsureRefsAsync(IChangeoverRuleRepository rules, Guid siteId, ChangeoverPattern pattern, CancellationToken cancellationToken)
    {
        if (pattern.EquipmentId is { } line && !(await rules.ListLinesAsync(siteId, cancellationToken)).Values.Contains(line))
        {
            throw new DomainRuleException("The line isn't an active line of this site that runs jobs.");
        }

        Guid[] materials = [.. new[] { pattern.FromMaterialId, pattern.ToMaterialId }.OfType<Guid>()];
        Guid[] classes = [.. new[] { pattern.FromMaterialClassId, pattern.ToMaterialClassId }.OfType<Guid>()];
        var (knownMaterials, knownClasses) = await rules.ExistingAsync(materials, classes, cancellationToken);
        if (materials.Any(m => !knownMaterials.Contains(m)))
        {
            throw new NotFoundException("A material of the rule doesn't exist.");
        }

        if (classes.Any(c => !knownClasses.Contains(c)))
        {
            throw new NotFoundException("A material class of the rule doesn't exist.");
        }
    }

    public static void EnsureNoDuplicate(IEnumerable<ChangeoverRuleRecord> site, ChangeoverPattern pattern, Guid? exceptId)
    {
        if (ChangeoverRules.Duplicate(site, pattern, exceptId) is { } other)
        {
            throw new DomainRuleException(
                $"A rule with the same line and sides is already on ({(int)other.Duration.TotalMinutes} min). Change that one, or switch it off first.");
        }
    }
}
