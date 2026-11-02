using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Inventory;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Sets lot attribute values at a site from a CSV file (#78): all or nothing, so one bad cell leaves everything as it was. A blank cell leaves a
/// value alone; <see cref="LotAttributeCsv.Clear"/> clears it. <see cref="DryRun"/> works everything out and keeps nothing.
/// </summary>
public sealed record ImportLotAttributeValuesCommand(Guid SiteId, string Csv, bool DryRun) : ICommand<ImportMaterialsResponse>;

/// <summary>The site's recorded attribute values of numbered lots, for exports (#78).</summary>
public sealed record ListLotAttributeValuesQuery(Guid SiteId) : IQuery<IReadOnlyList<LotAttributeExportDto>>;

/// <summary>The site's recorded attribute values as the import reads them: lot number, material, a column per attribute (#78).</summary>
public sealed record ExportLotAttributeValuesQuery(Guid SiteId) : IQuery<string>;

internal sealed class ImportLotAttributeValuesHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    ILotRecordsRepository records,
    IEventLog eventLog,
    IEventFactory events,
    Quality.IQualityRepository? quality = null,
    IIdGenerator? ids = null,
    IRequestContext? request = null) : ICommandHandler<ImportLotAttributeValuesCommand, ImportMaterialsResponse>
{
    private const string Update = "update";
    private const string Unchanged = "unchanged";
    private const string Error = "error";
    private const string Reason = "CSV import";

    public async Task<ImportMaterialsResponse> HandleAsync(ImportLotAttributeValuesCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await records.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        var attributes = (await records.ListAttributeDefinitionsAsync(command.SiteId, cancellationToken)).ToDictionary(a => a.Code, StringComparer.Ordinal);
        var source = LotAttributeCsv.Parse(command.Csv, [.. attributes.Keys]);
        if (source.Problems.Count > 0)
        {
            return new ImportMaterialsResponse { FileProblems = source.Problems, Errors = source.Problems.Count };
        }

        // Find every lot first, then lock them all in id order (D-030), as any change to several lots does.
        var found = await records.FindLotsByNumberAsync(command.SiteId, [.. source.Rows.Select(r => r.LotNumber).Where(n => n.Length > 0).Distinct()], cancellationToken);
        var locked = (await inventory.LockLotsAsync([.. found.Select(f => f.LotId)], cancellationToken)).ToDictionary(l => l.Lot.Id, l => l.Lot);
        var writer = new LotAttributeWriter(inventory, records, eventLog, events, quality, ids, request);
        var seen = new HashSet<Guid>();
        var results = new List<ImportRowResultDto>();
        foreach (var row in source.Rows)
        {
            try
            {
                var lot = Match(row, found);
                if (!seen.Add(lot.LotId))
                {
                    throw new DomainRuleException("The same lot appears earlier in the file.");
                }

                var context = new LotRecordContext(command.SiteId, lot.MaterialId, lot.MaterialClassId);
                var changes = new List<string>();
                foreach (var (code, cell) in row.Cells)
                {
                    var attribute = attributes[code];
                    var value = cell.Equals(LotAttributeCsv.Clear, StringComparison.OrdinalIgnoreCase) ? LotAttributeValue.Empty : Parse(attribute, cell);
                    var before = (await records.GetValueForUpdateAsync(lot.LotId, attribute.Id, cancellationToken))?.Value ?? LotAttributeValue.Empty;
                    if (await writer.SetAsync(locked[lot.LotId], context, attribute, before, value, Reason, cancellationToken) is not null)
                    {
                        changes.Add($"{code}: {before.Display ?? "—"} → {value.Display ?? "cleared"}");
                    }
                }

                results.Add(new ImportRowResultDto { Line = row.Line, Code = lot.LotNumber, Action = changes.Count > 0 ? Update : Unchanged, Changes = changes });
            }
            catch (DomainRuleException ex)
            {
                results.Add(new ImportRowResultDto { Line = row.Line, Code = row.LotNumber, Action = Error, Message = ex.Message });
            }
        }

        // All or nothing: anything wrong, or a dry run, and the transaction ends without a commit.
        var errors = results.Count(r => r.Action == Error);
        var applied = errors == 0 && !command.DryRun;
        if (applied)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ImportMaterialsResponse
        {
            Applied = applied,
            Updated = results.Count(r => r.Action == Update),
            Unchanged = results.Count(r => r.Action == Unchanged),
            Errors = errors,
            Rows = results,
        };
    }

    private static LotByNumber Match(LotAttributeImportRow row, IReadOnlyList<LotByNumber> found)
    {
        if (row.LotNumber.Length == 0)
        {
            throw new DomainRuleException("The lot number is blank.");
        }

        var matches = found.Where(f => string.Equals(f.LotNumber, row.LotNumber, StringComparison.OrdinalIgnoreCase)
                && (row.MaterialCode.Length == 0 || string.Equals(f.MaterialCode, row.MaterialCode, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return matches.Count switch
        {
            1 => matches[0],
            0 => throw new DomainRuleException(row.MaterialCode.Length == 0
                ? $"There is no lot {row.LotNumber} at this site."
                : $"There is no lot {row.LotNumber} of {row.MaterialCode} at this site."),
            _ => throw new DomainRuleException($"Lots of {string.Join(", ", matches.Select(m => m.MaterialCode))} are numbered {row.LotNumber}; fill in material_code."),
        };
    }

    private static LotAttributeValue Parse(LotAttributeDefinition attribute, string cell)
    {
        try
        {
            return attribute.Parse(cell);
        }
        catch (DomainRuleException ex)
        {
            throw new DomainRuleException($"{attribute.Code}: {ex.Message}");
        }
    }
}

internal sealed class ListLotAttributeValuesHandler(ILotRecordsReadStore store) : IQueryHandler<ListLotAttributeValuesQuery, IReadOnlyList<LotAttributeExportDto>>
{
    public async Task<IReadOnlyList<LotAttributeExportDto>> HandleAsync(ListLotAttributeValuesQuery query, CancellationToken cancellationToken) =>
        [.. (await store.ListValuesAtSiteAsync(query.SiteId, cancellationToken)).Select(v => new LotAttributeExportDto(v.LotId, v.LotNumber, v.MaterialCode, v.AttributeCode, v.Value))];
}

internal sealed class ExportLotAttributeValuesHandler(ILotRecordsReadStore store) : IQueryHandler<ExportLotAttributeValuesQuery, string>
{
    public async Task<string> HandleAsync(ExportLotAttributeValuesQuery query, CancellationToken cancellationToken)
    {
        var codes = (await store.ListAttributesAsync(query.SiteId, cancellationToken)).Where(a => a.IsActive).OrderBy(a => a.SortOrder).ThenBy(a => a.Code)
            .Select(a => a.Code).ToList();
        var values = await store.ListValuesAtSiteAsync(query.SiteId, cancellationToken);
        var rows = values
            .GroupBy(v => v.LotId)
            .Select(lot =>
            {
                var byCode = lot.ToDictionary(v => v.AttributeCode, v => v.Value, StringComparer.Ordinal);
                return (IReadOnlyList<string?>)[lot.First().LotNumber, lot.First().MaterialCode, .. codes.Select(c => byCode.GetValueOrDefault(c))];
            });
        return CsvText.Write([LotAttributeCsv.LotColumn, LotAttributeCsv.MaterialColumn, .. codes], rows);
    }
}
