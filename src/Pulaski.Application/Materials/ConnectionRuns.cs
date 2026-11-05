using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Domain;
using Pulaski.Application.Labor;
using Pulaski.Contracts.Labor;
using Pulaski.Contracts.Materials;

namespace Pulaski.Application.Materials;

/// <summary>What started a connection's run (#71). Values match <c>ts.connection_run.trigger</c>.</summary>
public static class ConnectionTriggers
{
    public const string Manual = "manual";
    public const string Schedule = "schedule";
}

/// <summary>How a connection's import ended (#71). Values match <c>ts.connection_run.outcome</c>.</summary>
public static class ConnectionOutcomes
{
    /// <summary>Imported: every row was valid and saved.</summary>
    public const string Applied = "applied";

    /// <summary>Read, but some rows had errors, so nothing was saved (all or nothing).</summary>
    public const string NotApplied = "not_applied";

    /// <summary>The source couldn't be read or the run stopped; nothing was saved.</summary>
    public const string Failed = "failed";
}

/// <summary>One import a connection ran (#71), as <c>ts.connection_run</c> keeps it.</summary>
public sealed record ConnectionRunRecord(
    Guid Id, Guid ConnectionId, DateTimeOffset StartedAt, DateTimeOffset FinishedAt, string Trigger, string Outcome,
    int Created, int Updated, int Unchanged, int Errors, string Summary, string Actor);

/// <summary>When a connection runs on its own (#71): every <see cref="EveryHours"/> hours, or daily at <see cref="DailyAt"/> (UTC), or never.</summary>
public sealed record ConnectionScheduleSpec(int? EveryHours, IReadOnlyList<TimeOnly> DailyAt)
{
    public static ConnectionScheduleSpec None { get; } = new(null, []);

    public bool IsScheduled => EveryHours is not null || DailyAt.Count > 0;

    public bool SameAs(ConnectionScheduleSpec other) => EveryHours == other.EveryHours && DailyAt.SequenceEqual(other.DailyAt);
}

/// <summary>Works out a connection's next run (#71). Pure.</summary>
public static class ConnectionSchedule
{
    public const int MaxDailyTimes = 4;

    /// <summary>Why the schedule can't be saved; null when it can.</summary>
    public static string? Problem(ConnectionScheduleSpec spec) =>
        spec.EveryHours is not null && spec.DailyAt.Count > 0 ? "Run every so many hours or at daily times, not both."
        : spec.EveryHours is < 1 or > 168 ? "Every 1 to 168 hours."
        : spec.DailyAt.Count > MaxDailyTimes ? $"At most {MaxDailyTimes} daily times."
        : null;

    /// <summary>
    /// The first run after <paramref name="after"/>: that plus the interval, or the next of the daily times (UTC) after it. Null when not
    /// scheduled. A run that comes late is followed by the next one after it, never by the ones it missed.
    /// </summary>
    public static DateTimeOffset? Next(ConnectionScheduleSpec spec, DateTimeOffset after)
    {
        if (spec.EveryHours is { } hours)
        {
            return after.ToUniversalTime().AddHours(hours);
        }

        var day = DateOnly.FromDateTime(after.UtcDateTime);
        for (var offset = 0; offset <= 1 && spec.DailyAt.Count > 0; offset++)
        {
            foreach (var time in spec.DailyAt.Order())
            {
                var at = new DateTimeOffset(day.AddDays(offset).ToDateTime(time), TimeSpan.Zero);
                if (at > after)
                {
                    return at;
                }
            }
        }

        return null;
    }
}

/// <summary>What every connection run shares (#71): loading the connection, its events' reason, and recording the import.</summary>
internal static class ConnectionRuns
{
    public static async Task<MaterialConnection> LoadAsync(IUnitOfWork unitOfWork, IMaterialConnectionStore store, Guid id, string target, CancellationToken cancellationToken)
    {
        MaterialConnection? connection;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            connection = (await store.GetForUpdateAsync(id, cancellationToken))?.Value;
        }

        if (connection is null || connection.Target != target)
        {
            throw new NotFoundException($"Connection {id} not found.");
        }

        return connection.IsActive ? connection : throw new DomainRuleException($"{connection.Code} is switched off.");
    }

    public static string Reason(MaterialConnection connection, string trigger) =>
        trigger == ConnectionTriggers.Schedule ? $"{connection.Name} import (scheduled)" : $"{connection.Name} import";

    public static string Outcome(bool applied, IReadOnlyList<string> problems) =>
        problems.Count > 0 ? ConnectionOutcomes.Failed : applied ? ConnectionOutcomes.Applied : ConnectionOutcomes.NotApplied;

    public static async Task RecordAsync(
        IUnitOfWork unitOfWork, IMaterialConnectionStore store, IIdGenerator ids, TimeProvider time, Guid connectionId, string trigger, DateTimeOffset started,
        string actor, bool applied, int created, int updated, int unchanged, int errors, IReadOnlyList<string> problems, CancellationToken cancellationToken)
    {
        var summary = MaterialConnectionRules.Summary(applied, created, updated, unchanged, errors, problems);
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await store.RecordRunAsync(
            new ConnectionRunRecord(
                ids.NewId(), connectionId, started, time.GetUtcNow(), trigger, Outcome(applied, problems), created, updated, unchanged, errors,
                summary.Length <= 2000 ? summary : summary[..2000], actor),
            cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

/// <summary>
/// Turns a REST, SOAP, OData or database connection's items into people import rows (#71) through <see cref="PersonImportFields.ToRow"/>, as
/// the file adapters do (D-054): every row is the connection's external system's person. Pure; a SCIM connection uses <see cref="PeopleScim.Read"/>.
/// </summary>
public static class ConnectionPeopleRows
{
    /// <summary>The people fields a connection may map; the external system is the connection's own.</summary>
    public static IReadOnlyList<string> Mappable { get; } = [.. PersonImportFields.Names.Keys.Where(k => k != "external_system")];

    /// <summary>Fields every people connection maps: who the person is here, and the key the source knows them by.</summary>
    public static IReadOnlyList<string> Required { get; } = ["employee_number", "name", "external_key"];

    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string> map) => ConnectionFields.Problems(map, Mappable, Required);

    public static PersonImportSource Read(IReadOnlyList<JsonElement> items, MaterialConnection connection)
    {
        if (items.Count > PersonImportFields.MaxRows)
        {
            return new PersonImportSource([], [$"The service sent {items.Count} people; one import takes at most {PersonImportFields.MaxRows}. Narrow it with a filter."]);
        }

        var rows = new List<PersonImportRow>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var problems = new List<string>();
            var item = items[i];
            var values = PersonImportFields.Names.Keys.ToDictionary(
                name => name, name => name == "external_system" ? connection.ExternalSystem : ConnectionFields.Text(item, connection.FieldMap, name, problems),
                StringComparer.Ordinal);
            rows.Add(PersonImportFields.ToRow(i + 1, name => values.GetValueOrDefault(name), problems));
        }

        return new PersonImportSource(rows, []);
    }
}

/// <summary>Pulls a people connection's people and imports them into its site, all or nothing (#71); a dry run checks and saves nothing.</summary>
public sealed record RunPeopleConnectionCommand(Guid Id, bool DryRun, string Trigger = ConnectionTriggers.Manual) : ICommand<ImportPeopleResponse>;

internal sealed class RunPeopleConnectionHandler(
    IUnitOfWork unitOfWork,
    IMaterialConnectionStore store,
    IMaterialConnectionFetcher fetcher,
    ICommandHandler<ImportPeopleCommand, ImportPeopleResponse> import,
    IRequestContext request,
    IIdGenerator ids,
    TimeProvider time) : ICommandHandler<RunPeopleConnectionCommand, ImportPeopleResponse>
{
    public async Task<ImportPeopleResponse> HandleAsync(RunPeopleConnectionCommand command, CancellationToken cancellationToken)
    {
        var connection = await ConnectionRuns.LoadAsync(unitOfWork, store, command.Id, ConnectionTargets.People, cancellationToken);
        var started = time.GetUtcNow();
        var fetched = await fetcher.FetchAsync(connection, PersonImportFields.MaxRows, cancellationToken);
        var source = connection.Kind == MaterialConnectionKinds.Scim
            ? PeopleScim.Read(fetched.Items, connection.ExternalSystem)
            : ConnectionPeopleRows.Read(fetched.Items, connection);
        var result = fetched.Problems.Count > 0
            ? new ImportPeopleResponse { FileProblems = fetched.Problems, Errors = fetched.Problems.Count }
            : await import.HandleAsync(
                new ImportPeopleCommand(connection.SiteId!.Value, source, command.DryRun, ConnectionRuns.Reason(connection, command.Trigger))
                {
                    SwitchOffMissing = connection.SwitchOffMissing,
                },
                cancellationToken);

        if (!command.DryRun)
        {
            await ConnectionRuns.RecordAsync(
                unitOfWork, store, ids, time, connection.Id, command.Trigger, started, request.ActorId,
                result.Applied, result.Created, result.Updated, result.Unchanged, result.Errors, result.FileProblems, cancellationToken);
        }

        return result;
    }
}

/// <summary>
/// Runs the connections whose schedule is due (#71), called by the API's scheduler. Each due run is claimed first, so with several API instances
/// it happens once; a run that throws is recorded as failed, and the next is planned either way.
/// </summary>
public sealed class ScheduledConnections(
    IUnitOfWork unitOfWork,
    IMaterialConnectionStore store,
    ICommandHandler<RunMaterialConnectionCommand, ImportMaterialsResponse> materials,
    ICommandHandler<RunPeopleConnectionCommand, ImportPeopleResponse> people,
    IRequestContext request,
    IIdGenerator ids,
    TimeProvider time,
    ILogger<ScheduledConnections>? logger = null)
{
    /// <returns>How many connections it ran.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        IReadOnlyList<MaterialConnection> due;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            due = await store.ListDueAsync(now, cancellationToken);
        }

        var ran = 0;
        foreach (var connection in due)
        {
            bool claimed;
            await using (var claiming = await unitOfWork.BeginAsync(cancellationToken))
            {
                claimed = await store.ClaimAsync(connection.Id, connection.NextRunAt!.Value, ConnectionSchedule.Next(connection.Schedule, now), cancellationToken);
                if (claimed)
                {
                    await claiming.CommitAsync(cancellationToken);
                }
            }

            if (!claimed)
            {
                continue;
            }

            ran++;
            var started = time.GetUtcNow();
            try
            {
                var (outcome, summary) = connection.IsPeople
                    ? Describe(await people.HandleAsync(new RunPeopleConnectionCommand(connection.Id, DryRun: false, ConnectionTriggers.Schedule), cancellationToken))
                    : Describe(await materials.HandleAsync(new RunMaterialConnectionCommand(connection.Id, DryRun: false, CreateClasses: false, ConnectionTriggers.Schedule), cancellationToken));
                if (outcome != ConnectionOutcomes.Applied)
                {
                    logger?.LogWarning("Scheduled run of connection {Code}: {Outcome}. {Summary}", connection.Code, outcome, summary);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger?.LogError(ex, "Scheduled run of connection {Code} failed.", connection.Code);
                await ConnectionRuns.RecordAsync(
                    unitOfWork, store, ids, time, connection.Id, ConnectionTriggers.Schedule, started, request.ActorId, false, 0, 0, 0, 0,
                    [$"The run stopped: {ex.Message}"], cancellationToken);
            }
        }

        return ran;
    }

    private static (string Outcome, string Summary) Describe(ImportMaterialsResponse r) =>
        (ConnectionRuns.Outcome(r.Applied, r.FileProblems), MaterialConnectionRules.Summary(r));

    private static (string Outcome, string Summary) Describe(ImportPeopleResponse r) =>
        (ConnectionRuns.Outcome(r.Applied, r.FileProblems), MaterialConnectionRules.Summary(r.Applied, r.Created, r.Updated, r.Unchanged, r.Errors, r.FileProblems));
}
