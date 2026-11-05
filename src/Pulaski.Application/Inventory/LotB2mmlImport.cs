using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>
/// Material lots from a B2MML file at a site (#72), all or nothing. A lot Pulaski doesn't have is received: its quantity and unit at the
/// <c>StorageLocation</c> with that code, with its expiry and supplier lot. A lot it has keeps its quantity; for either, properties whose ID
/// is a lot attribute's code set that attribute (D-056), and any others are reported as left out. <see cref="DryRun"/> keeps nothing.
/// </summary>
public sealed record ImportB2mmlLotsCommand(Guid SiteId, string Text, bool DryRun) : ICommand<ImportMaterialsResponse>;

internal sealed class ImportB2mmlLotsHandler(
    IUnitOfWork unitOfWork,
    IInventoryRepository inventory,
    ILotRecordsRepository records,
    IUnitRepository units,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids,
    TimeProvider timeProvider,
    IExpectedReceiptRepository expectedReceipts,
    Quality.IQualityRepository? quality = null,
    IRequestContext? request = null) : ICommandHandler<ImportB2mmlLotsCommand, ImportMaterialsResponse>
{
    private const string Create = "create";
    private const string Update = "update";
    private const string Unchanged = "unchanged";
    private const string Error = "error";
    private const string Reason = "B2MML import";

    public async Task<ImportMaterialsResponse> HandleAsync(ImportB2mmlLotsCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        if (!await records.IsSiteAsync(command.SiteId, cancellationToken))
        {
            throw new NotFoundException($"Site {command.SiteId} not found.");
        }

        var source = LotB2mml.Parse(command.Text);
        if (source.Problems.Count > 0)
        {
            return new ImportMaterialsResponse { FileProblems = source.Problems, Errors = source.Problems.Count };
        }

        var attributes = (await records.ListAttributeDefinitionsAsync(command.SiteId, cancellationToken))
            .ToDictionary(a => a.Code, StringComparer.OrdinalIgnoreCase);
        var numbers = source.Rows.Select(r => r.LotNumber).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var existing = await records.FindLotsByNumberAsync(command.SiteId, numbers, cancellationToken);
        var receiver = new ReceiveLotHandler(unitOfWork, inventory, units, eventLog, events, ids, timeProvider, expectedReceipts);

        // First every receipt, then every attribute, so the lots whose attributes change are locked together, in id order (D-030).
        var states = new List<RowState>();
        var seen = new HashSet<(string, string)>();
        foreach (var row in source.Rows)
        {
            var state = new RowState(row);
            states.Add(state);
            try
            {
                if (row.Problems.Count > 0)
                {
                    throw new DomainRuleException(string.Join(" ", row.Problems));
                }

                if (!seen.Add((row.LotNumber.ToUpperInvariant(), row.MaterialCode.ToUpperInvariant())))
                {
                    throw new DomainRuleException("The same lot appears earlier in the file.");
                }

                if (Find(existing, row) is not null)
                {
                    state.Action = Unchanged;
                    if (row.Quantity is not null)
                    {
                        state.Notes.Add("Pulaski has this lot; its quantity stays as it is.");
                    }

                    continue;
                }

                await ReceiveAsync(receiver, command.SiteId, row, state, cancellationToken);
            }
            catch (Exception ex) when (ex is DomainRuleException or ValidationException or ConflictException or NotFoundException)
            {
                state.Fail(ex);
            }
        }

        await SetAttributesAsync(command.SiteId, attributes, states, cancellationToken);

        // All or nothing: anything wrong, or a dry run, and the transaction ends without a commit.
        var errors = states.Count(s => s.Action == Error);
        var applied = errors == 0 && !command.DryRun;
        if (applied)
        {
            await transaction.CommitAsync(cancellationToken);
        }

        return new ImportMaterialsResponse
        {
            Applied = applied,
            Created = states.Count(s => s.Action == Create),
            Updated = states.Count(s => s.Action == Update),
            Unchanged = states.Count(s => s.Action == Unchanged),
            Errors = errors,
            Rows = [.. states.Select(s => s.ToResult())],
        };
    }

    private async Task ReceiveAsync(ReceiveLotHandler receiver, Guid siteId, LotB2mmlRow row, RowState state, CancellationToken cancellationToken)
    {
        if (row.Quantity is not { } quantity || row.UomCode is null || row.LocationCode is null)
        {
            throw new DomainRuleException("Pulaski doesn't have this lot; to receive it, give its Quantity with a UnitOfMeasure and its StorageLocation.");
        }

        var materialId = await inventory.FindMaterialIdByCodeAsync(row.MaterialCode, cancellationToken)
            ?? throw new DomainRuleException($"There is no material {row.MaterialCode}.");
        var uomId = await inventory.FindUomIdByCodeAsync(row.UomCode, cancellationToken)
            ?? throw new DomainRuleException($"There is no unit {row.UomCode}.");
        var locationId = await inventory.FindLocationIdByCodeAsync(siteId, row.LocationCode, cancellationToken)
            ?? throw new DomainRuleException($"There is no location {row.LocationCode} at this site.");
        var received = await receiver.ReceiveAsync(
            new ReceiveLotCommand(materialId, locationId, row.LotNumber, quantity, uomId, row.SupplierLot, row.ExpiresAt, Reason), cancellationToken);
        state.Action = Create;
        state.Changes.Add($"received {quantity} {row.UomCode} at {row.LocationCode}");

        // Where lots mix, the receipt joins the location's balance (D-031) and has no lot of its own to record attributes on.
        if (!string.Equals(received.LotNumber, row.LotNumber, StringComparison.OrdinalIgnoreCase))
        {
            state.Notes.Add($"It joined the balance at {row.LocationCode}, where lots mix; its properties aren't recorded.");
            state.SkipAttributes = true;
        }
    }

    private async Task SetAttributesAsync(
        Guid siteId, Dictionary<string, LotAttributeDefinition> attributes, List<RowState> states, CancellationToken cancellationToken)
    {
        var pending = states.Where(s => s.Action != Error && !s.SkipAttributes && s.Row.Properties.Count > 0).ToList();
        if (pending.Count == 0)
        {
            return;
        }

        var found = await records.FindLotsByNumberAsync(siteId, [.. pending.Select(s => s.Row.LotNumber).Distinct(StringComparer.OrdinalIgnoreCase)], cancellationToken);
        var lots = pending.Select(s => (State: s, Lot: Find(found, s.Row))).ToList();
        var locked = (await inventory.LockLotsAsync([.. lots.Where(l => l.Lot is not null).Select(l => l.Lot!.LotId).Distinct()], cancellationToken))
            .ToDictionary(l => l.Lot.Id, l => l.Lot);
        var writer = new LotAttributeWriter(inventory, records, eventLog, events, quality, ids, request);
        foreach (var (state, lot) in lots)
        {
            if (lot is null)
            {
                continue;
            }

            try
            {
                var context = new LotRecordContext(siteId, lot.MaterialId, lot.MaterialClassId);
                var changed = false;
                foreach (var (id, text) in state.Row.Properties)
                {
                    if (!attributes.TryGetValue(id, out var attribute))
                    {
                        state.Notes.Add($"Property {id} isn't a lot attribute here; left out.");
                        continue;
                    }

                    var value = Parse(attribute, text);
                    var before = (await records.GetValueForUpdateAsync(lot.LotId, attribute.Id, cancellationToken))?.Value ?? LotAttributeValue.Empty;
                    if (await writer.SetAsync(locked[lot.LotId], context, attribute, before, value, Reason, cancellationToken) is not null)
                    {
                        state.Changes.Add($"{attribute.Code}: {before.Display ?? "—"} → {value.Display ?? "cleared"}");
                        changed = true;
                    }
                }

                if (changed && state.Action == Unchanged)
                {
                    state.Action = Update;
                }
            }
            catch (DomainRuleException ex)
            {
                state.Fail(ex);
            }
        }
    }

    // A lot of the site by number and material; lot numbers are unique per material (D-027).
    private static LotByNumber? Find(IReadOnlyList<LotByNumber> lots, LotB2mmlRow row) =>
        lots.FirstOrDefault(l => string.Equals(l.LotNumber, row.LotNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(l.MaterialCode, row.MaterialCode, StringComparison.OrdinalIgnoreCase));

    // An empty value leaves an attribute alone, as a blank cell does in the CSV import (#78).
    private static LotAttributeValue Parse(LotAttributeDefinition attribute, string text)
    {
        try
        {
            return text.Length == 0 ? LotAttributeValue.Empty : attribute.Parse(text);
        }
        catch (DomainRuleException ex)
        {
            throw new DomainRuleException($"{attribute.Code}: {ex.Message}");
        }
    }

    private sealed class RowState(LotB2mmlRow row)
    {
        public LotB2mmlRow Row { get; } = row;

        public string Action { get; set; } = Unchanged;

        public string? Message { get; private set; }

        public bool SkipAttributes { get; set; }

        public List<string> Changes { get; } = [];

        public List<string> Notes { get; } = [];

        public void Fail(Exception ex)
        {
            Action = Error;
            Message = ex is ValidationException v ? string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))) : ex.Message;
        }

        public ImportRowResultDto ToResult() => new()
        {
            Line = Row.Line,
            Code = Row.MaterialCode.Length == 0 ? Row.LotNumber : $"{Row.LotNumber} ({Row.MaterialCode})",
            Action = Action,
            Changes = Changes,
            Message = Message,
            Notes = Notes,
        };
    }
}
