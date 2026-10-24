using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Materials;

namespace Pulaski.Application.Materials;

/// <summary>
/// An external source of material master data (D-053). It only translates: a file in its format becomes source-neutral
/// <see cref="MaterialImportRow"/>s, and <see cref="ImportMaterialsCommand"/> applies the same rules whatever the source. An adapter
/// never looks materials up or decides anything. Register one with <c>AddSingleton&lt;IMaterialSourceAdapter, …&gt;</c>.
/// </summary>
public interface IMaterialSourceAdapter
{
    /// <summary>Lower-case name in the URL, <c>POST /api/materials/import/{name}</c>.</summary>
    string Name { get; }

    /// <summary>Short name for screens and each event's reason ("<c>{DisplayName} import</c>").</summary>
    string DisplayName { get; }

    string Description { get; }

    /// <summary>File extensions and media types it reads, for a file picker.</summary>
    IReadOnlyList<string> FileTypes { get; }

    /// <summary>Whether the import names the external system the items belong to, because the file doesn't.</summary>
    bool TakesExternalSystem { get; }

    /// <summary>The rows in <paramref name="text"/>; problems are reported in the result, never thrown.</summary>
    MaterialImportSource Read(string text, string? externalSystem);
}

/// <summary>A CSV file, with any external system in its own columns (D-034).</summary>
internal sealed class CsvMaterialSource : IMaterialSourceAdapter
{
    public string Name => "csv";

    public string DisplayName => "CSV";

    public string Description => "A spreadsheet saved as CSV; the first line names the columns.";

    public IReadOnlyList<string> FileTypes { get; } = [".csv", "text/csv", "text/plain"];

    public bool TakesExternalSystem => false;

    public MaterialImportSource Read(string text, string? externalSystem) => MaterialCsv.Parse(text);
}

/// <summary>ISA-95 material information in B2MML XML, as ERPs and integration tools export it (D-053).</summary>
internal sealed class B2mmlMaterialSource : IMaterialSourceAdapter
{
    public string Name => "b2mml";

    public string DisplayName => "B2MML";

    public string Description => "ISA-95 material information (MaterialDefinition elements) in MESA B2MML XML.";

    public IReadOnlyList<string> FileTypes { get; } = [".xml", "application/xml", "text/xml"];

    public bool TakesExternalSystem => true;

    public MaterialImportSource Read(string text, string? externalSystem) => MaterialB2mml.Parse(text, externalSystem);
}

/// <summary>The sources materials can be imported from.</summary>
public sealed record ListMaterialSourcesQuery : IQuery<IReadOnlyList<MaterialSourceDto>>;

/// <summary>
/// Reads a file with the named adapter and imports its rows all or nothing (D-034, D-053).
/// </summary>
/// <param name="Source">The adapter's <see cref="IMaterialSourceAdapter.Name"/>, such as <c>csv</c> or <c>b2mml</c>.</param>
/// <param name="Text">The file's contents.</param>
/// <param name="ExternalSystem">The system the file's items belong to, for an adapter that <see cref="IMaterialSourceAdapter.TakesExternalSystem"/>.</param>
/// <param name="DryRun">True to check the file and report what would happen, saving nothing.</param>
public sealed record ImportMaterialFileCommand(string Source, string Text, string? ExternalSystem, bool DryRun) : ICommand<ImportMaterialsResponse>;

internal sealed class ListMaterialSourcesHandler(IEnumerable<IMaterialSourceAdapter> adapters)
    : IQueryHandler<ListMaterialSourcesQuery, IReadOnlyList<MaterialSourceDto>>
{
    public Task<IReadOnlyList<MaterialSourceDto>> HandleAsync(ListMaterialSourcesQuery query, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<MaterialSourceDto>>([.. adapters.Select(a => new MaterialSourceDto
        {
            Name = a.Name,
            DisplayName = a.DisplayName,
            Description = a.Description,
            FileTypes = a.FileTypes,
            TakesExternalSystem = a.TakesExternalSystem,
        })]);
}

internal sealed class ImportMaterialFileHandler(
    IEnumerable<IMaterialSourceAdapter> adapters,
    ICommandHandler<ImportMaterialsCommand, ImportMaterialsResponse> import) : ICommandHandler<ImportMaterialFileCommand, ImportMaterialsResponse>
{
    public Task<ImportMaterialsResponse> HandleAsync(ImportMaterialFileCommand command, CancellationToken cancellationToken)
    {
        var adapter = adapters.FirstOrDefault(a => string.Equals(a.Name, command.Source, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotFoundException($"No material source '{command.Source}'. The sources are {string.Join(", ", adapters.Select(a => a.Name))}.");
        var system = string.IsNullOrWhiteSpace(command.ExternalSystem) ? null : command.ExternalSystem.Trim();
        new Validator()
            .Require(system is null || adapter.TakesExternalSystem, "externalSystem", $"A {adapter.DisplayName} file names its external system in its own columns.")
            .Require(system is null || system.Length <= 100, "externalSystem", "At most 100 characters.")
            .ThrowIfInvalid();

        return import.HandleAsync(new ImportMaterialsCommand(adapter.Read(command.Text, system), command.DryRun, $"{adapter.DisplayName} import"), cancellationToken);
    }
}
