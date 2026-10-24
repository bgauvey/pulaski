using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;

namespace Pulaski.Application.Labor;

/// <summary>
/// Loads many people into a site at once (D-054), from any source that produces <see cref="PersonImportRow"/>s. A row with an external
/// system and key is that system's person: matched on them, so importing again never duplicates anyone, and a person kept by hand
/// with the same employee number is linked to it. The source then owns the employee number, name and whether they are active. A row
/// without them is a Pulaski person matched on the employee number. All or nothing, like the materials import (D-034); each change
/// records its usual event, with the source as the reason, so every update is in the audit trail.
/// </summary>
/// <param name="SiteId">The site the people belong to.</param>
/// <param name="Rows">The rows, and any problem with their source as a whole.</param>
/// <param name="DryRun">True to check the rows and report what would happen, saving nothing.</param>
/// <param name="Source">What the rows came from, such as "CSV import"; recorded as each event's reason.</param>
public sealed record ImportPeopleCommand(Guid SiteId, PersonImportSource Rows, bool DryRun, string Source) : ICommand<ImportPeopleResponse>;

internal sealed class ImportPeopleHandler(
    IUnitOfWork unitOfWork,
    ILaborRepository labor,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<ImportPeopleCommand, ImportPeopleResponse>
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Unchanged = "unchanged";
    public const string Error = "error";

    public async Task<ImportPeopleResponse> HandleAsync(ImportPeopleCommand command, CancellationToken cancellationToken)
    {
        if (command.Rows.Problems.Count > 0)
        {
            return new ImportPeopleResponse { FileProblems = command.Rows.Problems, Errors = command.Rows.Problems.Count };
        }

        var results = new List<ImportRowResultDto>();
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<(string, string)>();

        // One transaction: the rows see each other's changes, and an error anywhere leaves nothing saved.
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await labor.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new DomainRuleException("People are imported into a site; choose one.");
        }

        foreach (var row in command.Rows.Rows)
        {
            var number = row.EmployeeNumber.Trim();
            try
            {
                // Counted before any other check, so a later row with the same number is caught even when this one fails.
                if (!numbers.Add(number))
                {
                    throw new DomainRuleException($"Employee number {number} appears earlier in the file.");
                }

                if (row.Problems.Count > 0)
                {
                    throw new DomainRuleException(string.Join(" ", row.Problems));
                }

                if (row.ExternalSystem is { } system && row.ExternalKey is { } key && !keys.Add((system.Trim(), key.Trim())))
                {
                    throw new DomainRuleException($"{system} key {key} appears earlier in the file.");
                }

                results.Add(await ImportRowAsync(command.SiteId, row, number, command.Source, cancellationToken));
            }
            catch (Exception ex) when (ex is DomainRuleException or ValidationException)
            {
                results.Add(new ImportRowResultDto { Line = row.Line, Code = number, Action = Error, Message = Message(ex) });
            }
        }

        var errors = results.Count(r => r.Action == Error);
        var apply = errors == 0 && !command.DryRun;
        if (apply)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ImportPeopleResponse
        {
            Applied = apply,
            Created = results.Count(r => r.Action == Create),
            Updated = results.Count(r => r.Action == Update),
            Unchanged = results.Count(r => r.Action == Unchanged),
            Errors = errors,
            Rows = results,
        };
    }

    private async Task<ImportRowResultDto> ImportRowAsync(Guid siteId, PersonImportRow row, string number, string source, CancellationToken cancellationToken)
    {
        var name = row.DisplayName.Trim();
        var system = row.ExternalSystem?.Trim();
        var key = row.ExternalKey?.Trim();
        var version = row.ExternalVersion?.Trim();
        new Validator()
            .RequireText(number, "employee_number", 50)
            .RequireText(name, "name", 200)
            .Require((system is null) == (key is null), "external_key", "Give both external_system and external_key, or neither.")
            .Require(system is null || system.Length <= 100, "external_system", "At most 100 characters.")
            .Require(key is null || key.Length <= 100, "external_key", "At most 100 characters.")
            .Require(version is null || version.Length <= 100, "external_version", "At most 100 characters.")
            .Require(row.Origin is null || (system is not null && row.Origin is PersonOrigins.Hr or PersonOrigins.Erp), "origin",
                "hr or erp, for a person from an external system.")
            .ThrowIfInvalid();

        var current = await FindAsync(siteId, number, system, key, cancellationToken);
        var basis = current?.Value ?? new PersonRecord(ids.NewId(), siteId, number, name, null, null, IsActive: true);
        if (number != basis.EmployeeNumber && await labor.FindPersonByNumberForUpdateAsync(siteId, number, cancellationToken) is { } other && other.Value.Id != basis.Id)
        {
            throw new DomainRuleException($"Employee number {number} is already {other.Value.DisplayName}'s.");
        }

        var crewId = row.CrewCode is { } crew ? await CrewAsync(siteId, crew.Value, cancellationToken) : basis.CrewId;
        if (crewId != basis.CrewId)
        {
            await PeopleRules.EnsureCrewAsync(labor, crewId, siteId, cancellationToken);
        }

        var updated = basis with
        {
            EmployeeNumber = number,
            DisplayName = name,
            CrewId = crewId,
            AppUserId = row.Username is { } user ? await PeopleRules.UserAsync(labor, user.Value, basis.Id, cancellationToken) : basis.AppUserId,
            IsActive = row.IsActive?.Value ?? basis.IsActive,
            Origin = system is null ? PersonOrigins.Local : row.Origin ?? (basis.IsImported ? basis.Origin : PersonOrigins.Hr),
            ExternalSystem = system,
            ExternalKey = key,
            ExternalVersion = system is null ? null : version ?? basis.ExternalVersion,
        };

        if (current is null)
        {
            var created = events.Create(EventTypes.PersonCreated, "person", updated.Id, new { After = updated }, source);
            await labor.InsertPersonAsync(updated, created.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(created, cancellationToken);
            return new ImportRowResultDto { Line = row.Line, Code = number, Action = Create };
        }

        if (updated == current.Value)
        {
            return new ImportRowResultDto { Line = row.Line, Code = number, Action = Unchanged };
        }

        // Someone who leaves stops their time: they can't be switched off while on a line, from any source.
        if (current.Value.IsActive && !updated.IsActive && (await labor.ListOpenSessionsOfPersonForUpdateAsync(updated.Id, cancellationToken)).Count > 0)
        {
            throw new DomainRuleException($"{name} is still clocked on; end their time first.");
        }

        var envelope = events.Create(EventTypes.PersonUpdated, "person", updated.Id, new { Before = current.Value, After = updated }, source);
        await labor.UpdatePersonAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        return new ImportRowResultDto { Line = row.Line, Code = number, Action = Update, Changes = Changes(current.Value, updated) };
    }

    /// <summary>The person a row is: by external key, else (to link them) a hand-kept person with its employee number.</summary>
    private async Task<Versioned<PersonRecord>?> FindAsync(Guid siteId, string number, string? system, string? key, CancellationToken cancellationToken)
    {
        if (system is null || key is null)
        {
            var local = await labor.FindPersonByNumberForUpdateAsync(siteId, number, cancellationToken);
            if (local?.Value is { IsImported: true } imported)
            {
                throw new DomainRuleException(
                    $"{number} comes from {imported.ExternalSystem} (key {imported.ExternalKey}); give its external_system and external_key to import it, or change it there.");
            }

            return local;
        }

        if (await labor.FindPersonByExternalKeyForUpdateAsync(system, key, cancellationToken) is { } byKey)
        {
            return byKey.Value.SiteId == siteId
                ? byKey
                : throw new DomainRuleException($"{system} key {key} is {byKey.Value.DisplayName} ({byKey.Value.EmployeeNumber}) at another site.");
        }

        var byNumber = await labor.FindPersonByNumberForUpdateAsync(siteId, number, cancellationToken);
        return byNumber?.Value is { IsImported: true } taken
            ? throw new DomainRuleException($"Employee number {number} is {taken.DisplayName}, from {taken.ExternalSystem} (key {taken.ExternalKey}).")
            : byNumber;
    }

    private async Task<Guid?> CrewAsync(Guid siteId, string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        var crew = await labor.FindCrewByCodeAsync(siteId, code.Trim().ToUpperInvariant(), cancellationToken);
        return crew?.Id ?? throw new DomainRuleException($"The site has no crew {code.Trim()}.");
    }

    private static List<string> Changes(PersonRecord before, PersonRecord after)
    {
        var changes = new List<string>();
        void Add(bool changed, string field)
        {
            if (changed)
            {
                changes.Add(field);
            }
        }

        Add(before.EmployeeNumber != after.EmployeeNumber, "employee_number");
        Add(before.DisplayName != after.DisplayName, "name");
        Add(before.IsActive != after.IsActive, "active");
        Add(before.CrewId != after.CrewId, "crew");
        Add(before.AppUserId != after.AppUserId, "username");
        Add(before.Origin != after.Origin, "origin");
        Add(before.ExternalSystem != after.ExternalSystem || before.ExternalKey != after.ExternalKey, "external_key");
        Add(before.ExternalVersion != after.ExternalVersion, "external_version");
        return changes;
    }

    private static string Message(Exception ex) => ex switch
    {
        ValidationException v => string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))),
        _ => ex.Message,
    };
}
