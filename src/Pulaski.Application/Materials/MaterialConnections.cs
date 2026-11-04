using System.Text.Json;
using System.Text.RegularExpressions;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;

namespace Pulaski.Application.Materials;

/// <summary>What a material connection reads (D-068, #72). Values match <c>core.material_connection.kind</c>; fixed once made.</summary>
public static class MaterialConnectionKinds
{
    /// <summary>An OData v4 service: an entity set, with <c>$select</c>, <c>$filter</c> and next links.</summary>
    public const string OData = "odata";

    /// <summary>A plain REST service answering JSON: the items at a path in the answer, with an optional next-page address.</summary>
    public const string Rest = "rest";

    /// <summary>A SOAP service: an envelope POSTed, the items the answer's elements of one name.</summary>
    public const string Soap = "soap";

    /// <summary>A PostgreSQL database: a SELECT whose rows are the items, run read-only.</summary>
    public const string Database = "database";

    public static IReadOnlyList<string> All { get; } = [OData, Rest, Soap, Database];
}

/// <summary>A material connection as the commands use it (D-068, #72). What <see cref="EntitySet"/> means depends on the kind.</summary>
public sealed record MaterialConnection(
    Guid Id, string Code, string Name, string Kind, string? BaseUrl, string EntitySet, string? Filter, string ExternalSystem,
    IReadOnlyDictionary<string, string> FieldMap, bool IsActive, string? NextLinkPath = null, string? RequestBody = null, string? SoapAction = null,
    string? Query = null);

/// <summary>What a pull fetched: the items as the service sent them, and problems with the fetch as a whole.</summary>
public sealed record MaterialConnectionFetch(IReadOnlyList<JsonElement> Items, IReadOnlyList<string> Problems);

/// <summary>Material connections, read and written in the command's transaction (D-068).</summary>
public interface IMaterialConnectionStore
{
    Task<IReadOnlyList<Versioned<MaterialConnection>>> ListAsync(CancellationToken cancellationToken);

    Task<Versioned<MaterialConnection>?> GetForUpdateAsync(Guid id, CancellationToken cancellationToken);

    Task<bool> CodeExistsAsync(string code, CancellationToken cancellationToken);

    Task<long> InsertAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken);

    Task<long> UpdateAsync(MaterialConnection connection, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The last import's (not a check's) time, who ran it and what it did.</summary>
    Task RecordRunAsync(Guid id, string actor, string summary, DateTimeOffset at, CancellationToken cancellationToken);

    /// <summary>The last run of each, for the list.</summary>
    Task<IReadOnlyDictionary<Guid, (DateTimeOffset At, string? By, string? Summary)>> LastRunsAsync(CancellationToken cancellationToken);
}

/// <summary>Fetches a connection's items (D-068): the API's OData client, with the connection's credentials from configuration.</summary>
public interface IMaterialConnectionFetcher
{
    /// <summary>Every item of the entity set (paged, up to <paramref name="maxItems"/>); problems are returned, never thrown.</summary>
    Task<MaterialConnectionFetch> FetchAsync(MaterialConnection connection, int maxItems, CancellationToken cancellationToken);

    /// <summary>Whether credentials are configured for the connection.</summary>
    bool HasCredentials(string code);
}

/// <summary>
/// Turns OData items into source-neutral import rows (D-068) through <see cref="MaterialImportFields.ToRow"/>, as every file adapter does
/// (D-053): it only reads values, never looks materials up or decides anything. Every row carries the connection's external system; a
/// property the item doesn't have leaves that field as the material has it, a null clears an optional field. Pure.
/// </summary>
public static partial class ODataMaterialRows
{
    /// <summary>The import fields a connection may map; the external system is the connection's own.</summary>
    public static IReadOnlyList<string> Mappable { get; } = [.. MaterialImportFields.Names.Keys.Where(k => k != "external_system")];

    /// <summary>Fields every connection maps: what the item is, and the key the ERP knows it by.</summary>
    public static IReadOnlyList<string> Required { get; } = ["code", "external_key"];

    public static MaterialImportSource Read(IReadOnlyList<JsonElement> items, MaterialConnection connection)
    {
        if (items.Count > MaterialImportFields.MaxRows)
        {
            return new MaterialImportSource([], [$"The service sent {items.Count} items; one import takes at most {MaterialImportFields.MaxRows}. Narrow it with a filter."]);
        }

        var rows = new List<MaterialImportRow>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var problems = new List<string>();
            string? Field(string name)
            {
                if (name == "external_system")
                {
                    return connection.ExternalSystem;
                }

                if (!connection.FieldMap.TryGetValue(name, out var path))
                {
                    return null;
                }

                if (Resolve(item, path) is not { } value)
                {
                    return null;
                }

                switch (value.ValueKind)
                {
                    case JsonValueKind.Null:
                        return "";
                    case JsonValueKind.String:
                        return value.GetString()!.Trim();
                    case JsonValueKind.Number:
                        return value.GetRawText();
                    case JsonValueKind.True:
                        return "true";
                    case JsonValueKind.False:
                        return "false";
                    default:
                        problems.Add($"{name}: {path} isn't a single value.");
                        return null;
                }
            }

            // Every field read first, so a value that isn't one is among the problems ToRow starts from.
            var values = MaterialImportFields.Names.Keys.ToDictionary(name => name, Field, StringComparer.Ordinal);
            rows.Add(MaterialImportFields.ToRow(i + 1, name => values.GetValueOrDefault(name), problems));
        }

        return new MaterialImportSource(rows, []);
    }

    /// <summary>Why a field map can't be used; empty when it can.</summary>
    public static IReadOnlyList<string> Problems(IReadOnlyDictionary<string, string> map)
    {
        var problems = new List<string>();
        foreach (var (field, path) in map)
        {
            if (!Mappable.Contains(field))
            {
                problems.Add($"'{field}' isn't an import field; the fields are {string.Join(", ", Mappable)}.");
            }
            else if (!PathPattern().IsMatch(path))
            {
                problems.Add($"{field}: '{path}' isn't a property name (letters, digits and _, with / for a property of a property).");
            }
        }

        problems.AddRange(Required.Where(r => !map.ContainsKey(r)).Select(r => $"Map {r}: every connection needs it."));
        return problems;
    }

    /// <summary>The service's top-level properties the map reads, for <c>$select</c>.</summary>
    public static IReadOnlyList<string> Selected(IReadOnlyDictionary<string, string> map) =>
        [.. map.Values.Select(p => p.Split('/')[0]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    private static JsonElement? Resolve(JsonElement item, string path)
    {
        var current = item;
        foreach (var part in path.Split('/'))
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(part, out current))
            {
                return null;
            }
        }

        return current;
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,99}(/[A-Za-z_][A-Za-z0-9_]{0,99}){0,3}$")]
    private static partial Regex PathPattern();
}

public sealed record ListMaterialConnectionsQuery : IQuery<IReadOnlyList<MaterialConnectionDto>>;

/// <summary>Adds a connection (<paramref name="Id"/> null) or changes one; its code is fixed once made.</summary>
public sealed record SaveMaterialConnectionCommand(Guid? Id, SaveMaterialConnectionRequest Connection) : ICommand<CreatedResponse>;

/// <summary>Pulls the connection's items and imports them, all or nothing; a dry run checks and saves nothing (D-068).</summary>
public sealed record RunMaterialConnectionCommand(Guid Id, bool DryRun, bool CreateClasses) : ICommand<ImportMaterialsResponse>;

internal static partial class MaterialConnectionRules
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{0,29}$")]
    public static partial Regex CodePattern();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_.]{0,99}$")]
    public static partial Regex EntitySetPattern();

    /// <summary>An https service, or http on this machine only, for trying one out; a query only where the kind allows one (REST).</summary>
    public static bool IsAllowedUrl(string url, bool allowQuery = false) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && (allowQuery || uri.Query.Length == 0) && uri.Fragment.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_-]{0,99}([./][A-Za-z_][A-Za-z0-9_-]{0,99}){0,5}$")]
    public static partial Regex PathPattern();

    [GeneratedRegex(@"^\s*(SELECT|WITH)\b", RegexOptions.IgnoreCase)]
    public static partial Regex ReadQuery();

    /// <summary>What a connection of its kind still needs; empty when it can be saved.</summary>
    public static IReadOnlyList<string> KindProblems(MaterialConnection c)
    {
        var problems = new List<string>();
        void Need(bool ok, string problem)
        {
            if (!ok)
            {
                problems.Add(problem);
            }
        }

        switch (c.Kind)
        {
            case MaterialConnectionKinds.Database:
                Need(c.Query is { Length: > 0 and <= 4000 } q && ReadQuery().IsMatch(q) && !q.TrimEnd().TrimEnd(';').Contains(';', StringComparison.Ordinal),
                    "Query: one SELECT (or WITH … SELECT) statement, at most 4000 characters.");
                break;
            case MaterialConnectionKinds.Soap:
                Need(c.BaseUrl is { } soapUrl && IsAllowedUrl(soapUrl), "BaseUrl: the service's https address.");
                Need(c.RequestBody is { Length: > 0 and <= 20000 } body && body.TrimStart().StartsWith('<'), "RequestBody: the SOAP envelope to post, at most 20,000 characters.");
                Need(EntitySetPattern().IsMatch(c.EntitySet), "EntitySet: the name of the element that holds one item, such as Material.");
                Need(c.SoapAction is null || c.SoapAction.Length <= 500, "SoapAction: at most 500 characters.");
                break;
            case MaterialConnectionKinds.Rest:
                Need(c.BaseUrl is { } restUrl && IsAllowedUrl(restUrl, allowQuery: true), "BaseUrl: the https address that answers with the items.");
                Need(c.EntitySet.Length == 0 || PathPattern().IsMatch(c.EntitySet), "EntitySet: where the items are in the answer, such as data.items, or blank for the whole answer.");
                Need(c.NextLinkPath is null || PathPattern().IsMatch(c.NextLinkPath), "NextLinkPath: where the answer gives the next page's address, such as links.next.");
                break;
            default:
                Need(c.BaseUrl is { } odataUrl && IsAllowedUrl(odataUrl), "BaseUrl: the service root as an https address, without a query (http only on this machine).");
                Need(EntitySetPattern().IsMatch(c.EntitySet), "EntitySet: the entity set's name, such as Products.");
                break;
        }

        Need(c.BaseUrl is null || c.BaseUrl.Length <= 500, "BaseUrl: at most 500 characters.");
        Need(c.Kind == MaterialConnectionKinds.OData || c.Filter is null, "Filter: only an OData connection takes a filter.");
        return problems;
    }

    public static string Summary(ImportMaterialsResponse result) =>
        result.Applied
            ? $"{result.Created} created, {result.Updated} updated, {result.Unchanged} unchanged."
            : result.FileProblems.Count > 0 ? string.Join(" ", result.FileProblems) : $"Not imported: {result.Errors} items with errors.";
}

internal sealed class ListMaterialConnectionsHandler(IUnitOfWork unitOfWork, IMaterialConnectionStore store, IMaterialConnectionFetcher fetcher)
    : IQueryHandler<ListMaterialConnectionsQuery, IReadOnlyList<MaterialConnectionDto>>
{
    public async Task<IReadOnlyList<MaterialConnectionDto>> HandleAsync(ListMaterialConnectionsQuery query, CancellationToken cancellationToken)
    {
        // A handful of rows, read through the write store's session, which works only inside a unit of work.
        await using var reading = await unitOfWork.BeginAsync(cancellationToken);
        var runs = await store.LastRunsAsync(cancellationToken);
        return [.. (await store.ListAsync(cancellationToken)).Select(c => new MaterialConnectionDto
        {
            Id = c.Value.Id,
            Code = c.Value.Code,
            Name = c.Value.Name,
            Kind = c.Value.Kind,
            BaseUrl = c.Value.BaseUrl,
            EntitySet = c.Value.EntitySet,
            Filter = c.Value.Filter,
            NextLinkPath = c.Value.NextLinkPath,
            RequestBody = c.Value.RequestBody,
            SoapAction = c.Value.SoapAction,
            Query = c.Value.Query,
            ExternalSystem = c.Value.ExternalSystem,
            FieldMap = c.Value.FieldMap,
            IsActive = c.Value.IsActive,
            HasCredentials = fetcher.HasCredentials(c.Value.Code),
            LastRunAt = runs.TryGetValue(c.Value.Id, out var run) ? run.At : null,
            LastRunBy = run.By,
            LastRunSummary = run.Summary,
            Version = c.Version,
        })];
    }
}

internal sealed class SaveMaterialConnectionHandler(
    IUnitOfWork unitOfWork, IMaterialConnectionStore store, IEventLog eventLog, IEventFactory events, IIdGenerator ids)
    : ICommandHandler<SaveMaterialConnectionCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(SaveMaterialConnectionCommand command, CancellationToken cancellationToken)
    {
        var r = command.Connection;
        static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        var code = r.Code.Trim().ToUpperInvariant();
        var name = r.Name.Trim();
        var kind = (r.Kind ?? MaterialConnectionKinds.OData).Trim().ToLowerInvariant();
        var system = r.ExternalSystem.Trim();
        var map = r.FieldMap.Where(m => !string.IsNullOrWhiteSpace(m.Value))
            .ToDictionary(m => m.Key.Trim().ToLowerInvariant(), m => m.Value.Trim(), StringComparer.Ordinal);
        var asked = new MaterialConnection(
            Guid.Empty, code, name, kind, kind == MaterialConnectionKinds.Database ? null : Text(r.BaseUrl)?.TrimEnd('/'), (r.EntitySet ?? "").Trim(),
            kind == MaterialConnectionKinds.OData ? Text(r.Filter) : null, system, map, r.IsActive,
            kind == MaterialConnectionKinds.Rest ? Text(r.NextLinkPath) : null,
            kind == MaterialConnectionKinds.Soap ? Text(r.RequestBody) : null,
            kind == MaterialConnectionKinds.Soap ? Text(r.SoapAction) : null,
            kind == MaterialConnectionKinds.Database ? Text(r.Query) : null);
        var mapProblems = ODataMaterialRows.Problems(map);
        var kindProblems = MaterialConnectionKinds.All.Contains(kind) ? MaterialConnectionRules.KindProblems(asked) : ["odata, rest, soap or database."];
        new Validator()
            .Require(MaterialConnectionRules.CodePattern().IsMatch(code), "Code", "Letters, digits, '-' or '_', up to 30.")
            .RequireText(name, "Name", 100)
            .Require(kindProblems.Count == 0, "Kind", string.Join(" ", kindProblems))
            .Require(asked.Filter is null || asked.Filter.Length <= 1000, "Filter", "At most 1000 characters.")
            .RequireText(system, "ExternalSystem", 100)
            .Require(mapProblems.Count == 0, "FieldMap", string.Join(" ", mapProblems))
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        long version;
        Guid id;
        if (command.Id is { } existingId)
        {
            var current = await store.GetForUpdateAsync(existingId, cancellationToken) ?? throw new NotFoundException($"Material connection {existingId} not found.");
            if (current.Version != r.Version)
            {
                throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
            }

            if (current.Value.Code != code)
            {
                throw new DomainRuleException($"A connection's code is fixed once made: it names its credentials in the configuration ({current.Value.Code}).");
            }

            if (current.Value.Kind != kind)
            {
                throw new DomainRuleException($"A connection's kind is fixed once made ({current.Value.Kind}); add another connection instead.");
            }

            var after = asked with { Id = current.Value.Id };
            var envelope = events.Create(EventTypes.MaterialConnectionChanged, "material_connection", after.Id, new { Before = current.Value, After = after });
            version = await store.UpdateAsync(after, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            id = after.Id;
        }
        else
        {
            if (await store.CodeExistsAsync(code, cancellationToken))
            {
                throw new ConflictException($"There is already a material connection {code}.");
            }

            var made = asked with { Id = ids.NewId() };
            var envelope = events.Create(EventTypes.MaterialConnectionAdded, "material_connection", made.Id, new { After = made });
            version = await store.InsertAsync(made, envelope.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(envelope, cancellationToken);
            id = made.Id;
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatedResponse(id, version);
    }
}

/// <summary>
/// Fetches, maps and hands the rows to <see cref="ImportMaterialsCommand"/>: the same rules, events and all-or-nothing transaction as a
/// file (D-053), with the reason "<c>{name} import</c>". The fetch happens outside any transaction.
/// </summary>
internal sealed class RunMaterialConnectionHandler(
    IUnitOfWork unitOfWork,
    IMaterialConnectionStore store,
    IMaterialConnectionFetcher fetcher,
    ICommandHandler<ImportMaterialsCommand, ImportMaterialsResponse> import,
    IRequestContext request,
    TimeProvider time) : ICommandHandler<RunMaterialConnectionCommand, ImportMaterialsResponse>
{
    public async Task<ImportMaterialsResponse> HandleAsync(RunMaterialConnectionCommand command, CancellationToken cancellationToken)
    {
        MaterialConnection connection;
        await using (var reading = await unitOfWork.BeginAsync(cancellationToken))
        {
            connection = (await store.GetForUpdateAsync(command.Id, cancellationToken))?.Value ?? throw new NotFoundException($"Material connection {command.Id} not found.");
        }

        if (!connection.IsActive)
        {
            throw new DomainRuleException($"{connection.Code} is switched off.");
        }

        var fetched = await fetcher.FetchAsync(connection, MaterialImportFields.MaxRows, cancellationToken);
        var result = fetched.Problems.Count > 0
            ? new ImportMaterialsResponse { FileProblems = fetched.Problems }
            : await import.HandleAsync(
                new ImportMaterialsCommand(ODataMaterialRows.Read(fetched.Items, connection), command.DryRun, $"{connection.Name} import", command.CreateClasses),
                cancellationToken);

        if (!command.DryRun)
        {
            await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
            await store.RecordRunAsync(connection.Id, request.ActorId, MaterialConnectionRules.Summary(result), time.GetUtcNow(), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        return result;
    }
}
