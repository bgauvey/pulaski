using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Labor;

namespace Pulaski.Application.Labor;

/// <summary>
/// An external source of people, such as an HR system or the ERP (D-054). It only translates: a file in its format becomes
/// source-neutral <see cref="PersonImportRow"/>s, and <see cref="ImportPeopleCommand"/> applies the same rules whatever the source.
/// An adapter never looks people up or decides anything. Register one with <c>AddSingleton&lt;IPeopleSourceAdapter, …&gt;</c>.
/// </summary>
public interface IPeopleSourceAdapter
{
    /// <summary>Lower-case name in the URL, <c>POST /api/people/import/{name}</c>.</summary>
    string Name { get; }

    /// <summary>Short name for screens and each event's reason ("<c>{DisplayName} import</c>").</summary>
    string DisplayName { get; }

    string Description { get; }

    /// <summary>File extensions and media types it reads, for a file picker.</summary>
    IReadOnlyList<string> FileTypes { get; }

    /// <summary>The rows in <paramref name="text"/>, of <paramref name="externalSystem"/> when given; problems are reported, never thrown.</summary>
    PersonImportSource Read(string text, string? externalSystem);
}

/// <summary>A CSV file, such as an HR system's report (D-054).</summary>
internal sealed class CsvPeopleSource : IPeopleSourceAdapter
{
    public string Name => "csv";

    public string DisplayName => "CSV";

    public string Description => "A spreadsheet or HR report saved as CSV; the first line names the columns.";

    public IReadOnlyList<string> FileTypes { get; } = [".csv", "text/csv", "text/plain"];

    public PersonImportSource Read(string text, string? externalSystem) => PeopleCsv.Parse(text, externalSystem);
}

/// <summary>SCIM 2.0 users (RFC 7643), as HR and identity systems exchange them (D-054).</summary>
internal sealed class ScimPeopleSource : IPeopleSourceAdapter
{
    public string Name => "scim";

    public string DisplayName => "SCIM";

    public string Description => "SCIM 2.0 users (RFC 7643) in JSON: a ListResponse, an array of users, or one user.";

    public IReadOnlyList<string> FileTypes { get; } = [".json", "application/scim+json", "application/json"];

    public PersonImportSource Read(string text, string? externalSystem) => PeopleScim.Parse(text, externalSystem);
}

/// <summary>The sources people can be imported from.</summary>
public sealed record ListPeopleSourcesQuery : IQuery<IReadOnlyList<PeopleSourceDto>>;

/// <summary>Reads a file with the named adapter and imports its people into the site, all or nothing (D-054).</summary>
/// <param name="SiteId">The site the people belong to.</param>
/// <param name="Source">The adapter's <see cref="IPeopleSourceAdapter.Name"/>, such as <c>csv</c> or <c>scim</c>.</param>
/// <param name="Text">The file's contents.</param>
/// <param name="ExternalSystem">The HR or ERP system the file's people belong to, such as <c>HR1</c>; null for Pulaski people.</param>
/// <param name="DryRun">True to check the file and report what would happen, saving nothing.</param>
public sealed record ImportPeopleFileCommand(Guid SiteId, string Source, string Text, string? ExternalSystem, bool DryRun) : ICommand<ImportPeopleResponse>;

internal sealed class ListPeopleSourcesHandler(IEnumerable<IPeopleSourceAdapter> adapters) : IQueryHandler<ListPeopleSourcesQuery, IReadOnlyList<PeopleSourceDto>>
{
    public Task<IReadOnlyList<PeopleSourceDto>> HandleAsync(ListPeopleSourcesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PeopleSourceDto>>([.. adapters.Select(a => new PeopleSourceDto
        {
            Name = a.Name,
            DisplayName = a.DisplayName,
            Description = a.Description,
            FileTypes = a.FileTypes,
        })]);
}

internal sealed class ImportPeopleFileHandler(
    IEnumerable<IPeopleSourceAdapter> adapters,
    ICommandHandler<ImportPeopleCommand, ImportPeopleResponse> import) : ICommandHandler<ImportPeopleFileCommand, ImportPeopleResponse>
{
    public Task<ImportPeopleResponse> HandleAsync(ImportPeopleFileCommand command, CancellationToken cancellationToken)
    {
        var adapter = adapters.FirstOrDefault(a => string.Equals(a.Name, command.Source, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException($"No people source '{command.Source}'. The sources are {string.Join(", ", adapters.Select(a => a.Name))}.");
        var system = string.IsNullOrWhiteSpace(command.ExternalSystem) ? null : command.ExternalSystem.Trim();
        new Validator().Require(system is null || system.Length <= 100, "externalSystem", "At most 100 characters.").ThrowIfInvalid();

        return import.HandleAsync(
            new ImportPeopleCommand(command.SiteId, adapter.Read(command.Text, system), command.DryRun, $"{adapter.DisplayName} import"), cancellationToken);
    }
}
