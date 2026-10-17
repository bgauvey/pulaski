using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Materials;

/// <summary>
/// Loads many materials at once (D-034), from a CSV file or any other source that produces
/// <see cref="MaterialImportRow"/>s. All or nothing: every row is checked with the same rules as a
/// single change, and the rows are saved only when none has an error and it isn't a dry run.
/// </summary>
/// <param name="Rows">The rows, and any problem with their source as a whole.</param>
/// <param name="DryRun">True to check the rows and report what would happen, saving nothing.</param>
/// <param name="Source">What the rows came from, such as "CSV import"; recorded as each event's reason.</param>
public sealed record ImportMaterialsCommand(MaterialImportSource Rows, bool DryRun, string Source) : ICommand<ImportMaterialsResponse>;

internal sealed class ImportMaterialsHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<ImportMaterialsCommand, ImportMaterialsResponse>
{
    public const string Create = "create";
    public const string Update = "update";
    public const string Unchanged = "unchanged";
    public const string Error = "error";

    public async Task<ImportMaterialsResponse> HandleAsync(ImportMaterialsCommand command, CancellationToken cancellationToken)
    {
        if (command.Rows.Problems.Count > 0)
        {
            return new ImportMaterialsResponse { FileProblems = command.Rows.Problems, Errors = command.Rows.Problems.Count };
        }

        var results = new List<ImportRowResultDto>();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        var keys = new HashSet<(string, string)>();

        // One transaction: the rows see each other's changes, and an error anywhere leaves nothing saved.
        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        foreach (var row in command.Rows.Rows)
        {
            var code = row.Code.Trim();
            try
            {
                // Counted before any other check, so a later row with the same code is caught even when this one fails.
                if (!codes.Add(code))
                {
                    throw new DomainRuleException($"{code} appears earlier in the file.");
                }

                if (row.Problems.Count > 0)
                {
                    throw new DomainRuleException(string.Join(" ", row.Problems));
                }

                if (row.ExternalSystem is { } system && row.ExternalKey is { } key && !keys.Add((system.Trim(), key.Trim())))
                {
                    throw new DomainRuleException($"{system} key {key} appears earlier in the file.");
                }

                results.Add(await ImportRowAsync(row, code, command.Source, cancellationToken));
            }
            catch (Exception ex) when (ex is DomainRuleException or ValidationException)
            {
                results.Add(new ImportRowResultDto { Line = row.Line, Code = code, Action = Error, Message = Message(ex) });
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

    private async Task<ImportRowResultDto> ImportRowAsync(MaterialImportRow row, string code, string source, CancellationToken cancellationToken)
    {
        var name = row.Name.Trim();
        var description = row.Description is { } d ? MaterialRules.Text(d.Value) : null;
        new Validator()
            .RequireText(code, "code", MaterialRules.MaxCode)
            .RequireText(name, "name", MaterialRules.MaxName)
            .Require(MaterialTypes.All.Contains(row.MaterialType), "type", $"'{row.MaterialType}' isn't a material type; use {string.Join(", ", MaterialTypes.All)}.")
            .RequireText(row.BaseUomCode, "base_unit", 20)
            .Require(description is null || description.Length <= MaterialRules.MaxDescription, "description", $"At most {MaterialRules.MaxDescription} characters.")
            .Require(row.ShelfLifeDays?.Value is null or (> 0 and <= 36_500), "shelf_life_days", "Between 1 and 36500 days, or blank for none.")
            .Require((row.ExternalSystem is null) == (row.ExternalKey is null), "external_key", "Give both external_system and external_key, or neither.")
            .Require(row.ExternalSystem is null || row.ExternalSystem.Length <= 100, "external_system", "At most 100 characters.")
            .Require(row.ExternalKey is null || row.ExternalKey.Length <= 200, "external_key", "At most 200 characters.")
            .Require(row.ExternalVersion is null || row.ExternalVersion.Length <= 100, "external_version", "At most 100 characters.")
            .ThrowIfInvalid();

        var unit = await materials.FindUomAsync(row.BaseUomCode.Trim(), cancellationToken)
            ?? throw new DomainRuleException($"There is no unit '{row.BaseUomCode.Trim()}' in Pulaski; add it on the Units page or use another.");
        var materialClass = await ClassAsync(row, cancellationToken);
        var classId = materialClass?.Id;

        Versioned<Material>? existing;
        Material before;
        Material after;
        MaterialUse use;
        if (row.ExternalSystem is { } system && row.ExternalKey is { } key)
        {
            existing = await materials.FindByExternalKeyForUpdateAsync(system.Trim(), key.Trim(), cancellationToken);
            await EnsureCodeFreeAsync(code, existing?.Value.Id, cancellationToken);
            use = existing is null ? MaterialUse.None : await materials.GetUseAsync(existing.Value.Id, cancellationToken);
            before = existing?.Value
                ?? Material.CreateFromErp(ids.NewId(), system.Trim(), key.Trim(), row.ExternalVersion, code, name, unit.Id, row.MaterialType);
            var imported = before.ImportFromErp(row.ExternalVersion, code, name, unit.Id, row.MaterialType, use);
            after = imported.Edit(Change(row, imported, imported.Code, imported.Name, imported.MaterialType, imported.BaseUomId, description, classId), use);
        }
        else
        {
            existing = await materials.FindByCodeForUpdateAsync(code, cancellationToken);
            if (existing?.Value is { IsFromErp: true } erp)
            {
                throw new DomainRuleException(
                    $"{code} comes from {erp.ExternalSystem} (key {erp.ExternalKey}); give its external_system and external_key to import it, or change it there.");
            }

            use = existing is null ? MaterialUse.None : await materials.GetUseAsync(existing.Value.Id, cancellationToken);
            var change = Change(row, existing?.Value, code, name, row.MaterialType, unit.Id, description, classId);
            before = existing?.Value ?? Material.CreateLocal(ids.NewId(), change);
            after = before.Edit(change, use);
        }

        if (materialClass is { IsActive: false } && materialClass.Id != existing?.Value.MaterialClassId)
        {
            throw new DomainRuleException($"Class {materialClass.Code} is inactive; choose another.");
        }

        if (unit.Id != existing?.Value.BaseUomId && !unit.IsActive)
        {
            throw new DomainRuleException($"Unit {row.BaseUomCode.Trim()} is inactive.");
        }

        if (existing is not null && after == existing.Value)
        {
            return new ImportRowResultDto { Line = row.Line, Code = code, Action = Unchanged };
        }

        var type = after.IsFromErp ? EventTypes.MaterialImported : existing is null ? EventTypes.MaterialCreated : EventTypes.MaterialUpdated;
        var envelope = existing is null
            ? events.Create(type, "material", after.Id, new { After = after }, source)
            : events.Create(type, "material", after.Id, new { Before = existing.Value, After = after }, source);
        if (existing is null)
        {
            await materials.InsertAsync(after, envelope.OccurredAt, cancellationToken);
        }
        else
        {
            await materials.UpdateAsync(after, envelope.OccurredAt, cancellationToken);
        }

        await eventLog.AppendAsync(envelope, cancellationToken);
        return existing is null
            ? new ImportRowResultDto { Line = row.Line, Code = code, Action = Create }
            : new ImportRowResultDto { Line = row.Line, Code = code, Action = Update, Changes = Changes(existing.Value, after) };
    }

    /// <summary>The row's values over the material's own: a field the source left out keeps what it has (or the default, for a new one).</summary>
    private static MaterialChange Change(
        MaterialImportRow row, Material? current, string code, string name, string type, Guid unitId, string? description, Guid? classId) =>
        new(
            code,
            name,
            row.Description is null ? current?.Description : description,
            type,
            row.ClassCode is null ? current?.MaterialClassId : classId,
            unitId,
            row.IsLotTracked?.Value ?? current?.IsLotTracked ?? true,
            row.ShelfLifeDays is { } days ? days.Value : current?.ShelfLifeDays,
            row.IsActive?.Value ?? current?.IsActive ?? true);

    /// <summary>The class the row names; an inactive one is allowed only for a material already in it.</summary>
    private async Task<MaterialClass?> ClassAsync(MaterialImportRow row, CancellationToken cancellationToken) =>
        row.ClassCode?.Value is not { } classCode
            ? null
            : await materials.FindClassByCodeAsync(classCode.Trim().ToUpperInvariant(), cancellationToken)
                ?? throw new DomainRuleException($"There is no class '{classCode.Trim()}'; add it on the Classes tab first.");

    /// <summary>A code another material already has would break the unique key, and with it the whole import's transaction.</summary>
    private async Task EnsureCodeFreeAsync(string code, Guid? materialId, CancellationToken cancellationToken)
    {
        if (await materials.FindByCodeForUpdateAsync(code, cancellationToken) is { } owner && owner.Value.Id != materialId)
        {
            throw new DomainRuleException(
                $"Another material already has the code {code}{(owner.Value.IsFromErp ? $" ({owner.Value.ExternalSystem} key {owner.Value.ExternalKey})" : "")}.");
        }
    }

    private static List<string> Changes(Material before, Material after)
    {
        var changes = new List<string>();
        void Check(bool changed, string field)
        {
            if (changed)
            {
                changes.Add(field);
            }
        }

        Check(before.Code != after.Code, "code");
        Check(before.Name != after.Name, "name");
        Check(before.Description != after.Description, "description");
        Check(before.MaterialType != after.MaterialType, "type");
        Check(before.MaterialClassId != after.MaterialClassId, "class");
        Check(before.BaseUomId != after.BaseUomId, "base unit");
        Check(before.IsLotTracked != after.IsLotTracked, "lot tracking");
        Check(before.ShelfLifeDays != after.ShelfLifeDays, "shelf life");
        Check(before.IsActive != after.IsActive, "active");
        Check(before.ExternalVersion != after.ExternalVersion, "external version");
        return changes;
    }

    private static string Message(Exception ex) => ex switch
    {
        ValidationException v => string.Join(" ", v.Errors.SelectMany(e => e.Value.Select(m => $"{e.Key}: {m}"))),
        _ => ex.Message,
    };
}
