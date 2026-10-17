using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Contracts.Common;
using Pulaski.Contracts.Materials;
using Pulaski.Domain;
using Pulaski.Domain.Materials;

namespace Pulaski.Application.Materials;

/// <summary>A new local material (D-033).</summary>
public sealed record CreateMaterialCommand(
    string Code, string Name, string? Description, string MaterialType, Guid? MaterialClassId, Guid BaseUomId, bool IsLotTracked, int? ShelfLifeDays)
    : ICommand<CreatedResponse>;

/// <summary>Changes a material; for one from the ERP, only the fields Pulaski owns may differ.</summary>
public sealed record UpdateMaterialCommand(
    Guid Id,
    long ExpectedVersion,
    string Code,
    string Name,
    string? Description,
    string MaterialType,
    Guid? MaterialClassId,
    Guid BaseUomId,
    bool IsLotTracked,
    int? ShelfLifeDays,
    bool IsActive) : ICommand<UpdateMaterialResponse>;

/// <summary>One item from an external item master: creates the material or updates the fields the ERP owns.</summary>
public sealed record ImportMaterialCommand(
    string ExternalSystem, string ExternalKey, string? ExternalVersion, string Code, string Name, string BaseUomCode, string MaterialType)
    : ICommand<ImportMaterialResponse>;

public sealed record CreateMaterialClassCommand(string Code, string Name) : ICommand<CreatedResponse>;

public sealed record UpdateMaterialClassCommand(Guid Id, long ExpectedVersion, string Name, bool IsActive) : ICommand<VersionResponse>;

internal static class MaterialRules
{
    public const int MaxCode = 50;
    public const int MaxName = 200;
    public const int MaxDescription = 2000;

    public static Validator RequireMaterial(this Validator validator, string code, string name, string? description, string type, Guid baseUomId, int? shelfLifeDays) =>
        validator
            .RequireText(code, "Code", MaxCode)
            .RequireText(name, "Name", MaxName)
            .Require(description is null || description.Length <= MaxDescription, "Description", $"At most {MaxDescription} characters.")
            .Require(MaterialTypes.All.Contains(type), "MaterialType", $"One of {string.Join(", ", MaterialTypes.All)}.")
            .Require(baseUomId != Guid.Empty, "BaseUomId", "Required.")
            .Require(shelfLifeDays is null or (> 0 and <= 36_500), "ShelfLifeDays", "Between 1 and 36500 days, or blank for none.");

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A class the material may be given: one that exists and, unless it already has it, is active.</summary>
    public static async Task EnsureClassAsync(IMaterialAdminRepository materials, Guid? classId, Guid? currentClassId, CancellationToken cancellationToken)
    {
        if (classId is not { } id || id == currentClassId)
        {
            return;
        }

        var materialClass = await materials.GetClassAsync(id, cancellationToken) ?? throw new DomainRuleException($"Material class {id} not found.");
        if (!materialClass.IsActive)
        {
            throw new DomainRuleException($"Class {materialClass.Code} is inactive; choose another.");
        }
    }

    public static async Task EnsureUnitAsync(IMaterialAdminRepository materials, Guid uomId, Guid? currentUomId, CancellationToken cancellationToken)
    {
        if (uomId != currentUomId && !await materials.IsUomActiveAsync(uomId, cancellationToken))
        {
            throw new DomainRuleException("Choose an active unit as the base unit.");
        }
    }
}

internal sealed class CreateMaterialHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateMaterialCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateMaterialCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim();
        var name = command.Name.Trim();
        var description = MaterialRules.Text(command.Description);
        new Validator().RequireMaterial(code, name, description, command.MaterialType, command.BaseUomId, command.ShelfLifeDays).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        await MaterialRules.EnsureUnitAsync(materials, command.BaseUomId, null, cancellationToken);
        await MaterialRules.EnsureClassAsync(materials, command.MaterialClassId, null, cancellationToken);

        var material = Material.CreateLocal(
            ids.NewId(),
            new MaterialChange(code, name, description, command.MaterialType, command.MaterialClassId, command.BaseUomId, command.IsLotTracked, command.ShelfLifeDays, IsActive: true));
        var envelope = events.Create(EventTypes.MaterialCreated, "material", material.Id, new { After = material });
        var version = await materials.InsertAsync(material, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(material.Id, version);
    }
}

internal sealed class UpdateMaterialHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateMaterialCommand, UpdateMaterialResponse>
{
    public async Task<UpdateMaterialResponse> HandleAsync(UpdateMaterialCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim();
        var name = command.Name.Trim();
        var description = MaterialRules.Text(command.Description);
        new Validator().RequireMaterial(code, name, description, command.MaterialType, command.BaseUomId, command.ShelfLifeDays).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await materials.GetForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Material {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"{current.Value.Code} was changed by someone else. Reload and try again.");
        }

        await MaterialRules.EnsureUnitAsync(materials, command.BaseUomId, current.Value.BaseUomId, cancellationToken);
        await MaterialRules.EnsureClassAsync(materials, command.MaterialClassId, current.Value.MaterialClassId, cancellationToken);
        var use = await materials.GetUseAsync(current.Value.Id, cancellationToken);
        var updated = current.Value.Edit(
            new MaterialChange(code, name, description, command.MaterialType, command.MaterialClassId, command.BaseUomId, command.IsLotTracked, command.ShelfLifeDays, command.IsActive),
            use);
        if (updated == current.Value)
        {
            return new UpdateMaterialResponse(current.Version, []);
        }

        var envelope = events.Create(EventTypes.MaterialUpdated, "material", updated.Id, new { Before = current.Value, After = updated });
        var version = await materials.UpdateAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);

        // Switching a material off stops new use; whatever already uses it carries on, so say what that is.
        var stillUsedBy = current.Value.IsActive && !updated.IsActive && use.InUse
            ? await materials.DescribeUseAsync(updated.Id, cancellationToken)
            : [];
        await transaction.CommitAsync(cancellationToken);

        return new UpdateMaterialResponse(version, stillUsedBy);
    }
}

internal sealed class ImportMaterialHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<ImportMaterialCommand, ImportMaterialResponse>
{
    public async Task<ImportMaterialResponse> HandleAsync(ImportMaterialCommand command, CancellationToken cancellationToken)
    {
        var system = command.ExternalSystem.Trim();
        var key = command.ExternalKey.Trim();
        var code = command.Code.Trim();
        var name = command.Name.Trim();
        var version = MaterialRules.Text(command.ExternalVersion);
        new Validator()
            .RequireText(system, nameof(command.ExternalSystem), 100)
            .RequireText(key, nameof(command.ExternalKey), 200)
            .Require(version is null || version.Length <= 100, nameof(command.ExternalVersion), "At most 100 characters.")
            .RequireText(code, nameof(command.Code), MaterialRules.MaxCode)
            .RequireText(name, nameof(command.Name), MaterialRules.MaxName)
            .RequireText(command.BaseUomCode, nameof(command.BaseUomCode), 20)
            .Require(MaterialTypes.All.Contains(command.MaterialType), nameof(command.MaterialType), $"One of {string.Join(", ", MaterialTypes.All)}.")
            .ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var unit = await materials.FindUomAsync(command.BaseUomCode.Trim(), cancellationToken)
            ?? throw new DomainRuleException($"There is no unit '{command.BaseUomCode.Trim()}' in Pulaski; add it on the Units page or map the ERP's unit to one.");
        var existing = await materials.FindByExternalKeyForUpdateAsync(system, key, cancellationToken);

        if (existing is null)
        {
            if (!unit.IsActive)
            {
                throw new DomainRuleException($"Unit {command.BaseUomCode.Trim()} is inactive.");
            }

            var created = Material.CreateFromErp(ids.NewId(), system, key, version, code, name, unit.Id, command.MaterialType);
            var made = events.Create(EventTypes.MaterialImported, "material", created.Id, new { After = created });
            var createdVersion = await materials.InsertAsync(created, made.OccurredAt, cancellationToken);
            await eventLog.AppendAsync(made, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new ImportMaterialResponse(created.Id, createdVersion, Created: true, Changed: true);
        }

        var current = existing.Value;
        var use = await materials.GetUseAsync(current.Id, cancellationToken);
        var imported = current.ImportFromErp(version, code, name, unit.Id, command.MaterialType, use);
        if (imported == current)
        {
            return new ImportMaterialResponse(current.Id, existing.Version, Created: false, Changed: false);
        }

        var envelope = events.Create(EventTypes.MaterialImported, "material", current.Id, new { Before = current, After = imported });
        var newVersion = await materials.UpdateAsync(imported, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ImportMaterialResponse(current.Id, newVersion, Created: false, Changed: true);
    }
}

internal sealed class CreateMaterialClassHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IEventLog eventLog,
    IEventFactory events,
    IIdGenerator ids) : ICommandHandler<CreateMaterialClassCommand, CreatedResponse>
{
    public async Task<CreatedResponse> HandleAsync(CreateMaterialClassCommand command, CancellationToken cancellationToken)
    {
        var code = command.Code.Trim().ToUpperInvariant();
        var name = command.Name.Trim();
        new Validator().RequireText(code, nameof(command.Code), 30).RequireText(name, nameof(command.Name), 100).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var created = new MaterialClass(ids.NewId(), code, name, IsActive: true);
        var envelope = events.Create(EventTypes.MaterialClassCreated, "material_class", created.Id, new { After = created });
        var version = await materials.InsertClassAsync(created, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new CreatedResponse(created.Id, version);
    }
}

internal sealed class UpdateMaterialClassHandler(
    IUnitOfWork unitOfWork,
    IMaterialAdminRepository materials,
    IEventLog eventLog,
    IEventFactory events) : ICommandHandler<UpdateMaterialClassCommand, VersionResponse>
{
    public async Task<VersionResponse> HandleAsync(UpdateMaterialClassCommand command, CancellationToken cancellationToken)
    {
        var name = command.Name.Trim();
        new Validator().RequireText(name, nameof(command.Name), 100).ThrowIfInvalid();

        await using var transaction = await unitOfWork.BeginAsync(cancellationToken);
        var current = await materials.GetClassForUpdateAsync(command.Id, cancellationToken)
            ?? throw new NotFoundException($"Material class {command.Id} not found.");
        if (current.Version != command.ExpectedVersion)
        {
            throw new ConflictException($"Class {current.Value.Code} was changed by someone else. Reload and try again.");
        }

        var updated = current.Value with { Name = name, IsActive = command.IsActive };
        var envelope = events.Create(EventTypes.MaterialClassUpdated, "material_class", updated.Id, new { Before = current.Value, After = updated });
        var version = await materials.UpdateClassAsync(updated, envelope.OccurredAt, cancellationToken);
        await eventLog.AppendAsync(envelope, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new VersionResponse(version);
    }
}
