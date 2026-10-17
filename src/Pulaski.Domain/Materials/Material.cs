namespace Pulaski.Domain.Materials;

/// <summary>Material types (D-033): a label from the item master, driving no rules. Values match <c>core.material.material_type</c>.</summary>
public static class MaterialTypes
{
    public static IReadOnlyList<string> All { get; } = ["raw", "intermediate", "finished", "packaging", "consumable", "rework"];
}

/// <summary>Where a material is mastered. Values match <c>core.material.origin</c>.</summary>
public static class MaterialOrigin
{
    public const string Local = "local";
    public const string Erp = "erp";
}

/// <summary>What refers to a material, for the rules that keep those references valid (D-033).</summary>
/// <param name="InUse">Any lot, BOM, BOM item, order, job, planned component, capability, run standard, process scope or changeover rule refers to it.</param>
/// <param name="HasStockOnHand">Some lot or balance of it has stock on hand.</param>
public sealed record MaterialUse(bool InUse, bool HasStockOnHand)
{
    public static MaterialUse None { get; } = new(false, false);
}

/// <summary>Every field a person can set on a material; for one from the ERP, its own fields must arrive unchanged.</summary>
public sealed record MaterialChange(
    string Code,
    string Name,
    string? Description,
    string MaterialType,
    Guid? MaterialClassId,
    Guid BaseUomId,
    bool IsLotTracked,
    int? ShelfLifeDays,
    bool IsActive);

/// <summary>
/// An item or part (D-033). For a material from the ERP, its code, name, base unit and type change
/// only through <see cref="ImportFromErp"/>; Pulaski owns everything else. The base unit can't change
/// once anything uses the material, nor a local code; lot tracking changes only with nothing on hand.
/// </summary>
public sealed record Material(
    Guid Id,
    string Code,
    string Name,
    string? Description,
    string MaterialType,
    Guid? MaterialClassId,
    Guid BaseUomId,
    bool IsLotTracked,
    int? ShelfLifeDays,
    string Origin,
    string? ExternalSystem,
    string? ExternalKey,
    string? ExternalVersion,
    bool IsActive)
{
    public bool IsFromErp => Origin == MaterialOrigin.Erp;

    public static Material CreateLocal(Guid id, MaterialChange change)
    {
        EnsureType(change.MaterialType);
        return new Material(
            id, change.Code, change.Name, change.Description, change.MaterialType, change.MaterialClassId, change.BaseUomId,
            change.IsLotTracked, change.ShelfLifeDays, MaterialOrigin.Local, null, null, null, IsActive: true);
    }

    /// <summary>A new material from an external system's item master; Pulaski's own fields start at their defaults.</summary>
    public static Material CreateFromErp(
        Guid id, string externalSystem, string externalKey, string? externalVersion, string code, string name, Guid baseUomId, string materialType)
    {
        EnsureType(materialType);
        return new Material(
            id, code, name, null, materialType, null, baseUomId, IsLotTracked: true, ShelfLifeDays: null,
            MaterialOrigin.Erp, externalSystem, externalKey, externalVersion, IsActive: true);
    }

    /// <summary>A person's change, checked against who owns each field and what uses the material.</summary>
    /// <exception cref="DomainRuleException">A field the ERP owns, or one the material's use fixes, would change.</exception>
    public Material Edit(MaterialChange change, MaterialUse use)
    {
        if (IsFromErp && (change.Code != Code || change.Name != Name || change.BaseUomId != BaseUomId || change.MaterialType != MaterialType))
        {
            throw new DomainRuleException($"{Code} comes from {ExternalSystem}: its code, name, base unit and type change there, not here.");
        }

        EnsureType(change.MaterialType);
        if (change.Code != Code && use.InUse)
        {
            throw new DomainRuleException($"{Code} is in use, so its code is fixed.");
        }

        EnsureBaseUnit(change.BaseUomId, use);
        if (change.IsLotTracked != IsLotTracked && use.HasStockOnHand)
        {
            throw new DomainRuleException(
                $"{Code} has stock on hand; use, move out or write it off before it is {(change.IsLotTracked ? "tracked by lot" : "kept as balances")}.");
        }

        return this with
        {
            Code = change.Code,
            Name = change.Name,
            Description = change.Description,
            MaterialType = change.MaterialType,
            MaterialClassId = change.MaterialClassId,
            BaseUomId = change.BaseUomId,
            IsLotTracked = change.IsLotTracked,
            ShelfLifeDays = change.ShelfLifeDays,
            IsActive = change.IsActive,
        };
    }

    /// <summary>
    /// The ERP's own fields as its item master now has them. The ERP may rename its items, but not
    /// change the base unit of one that is in use.
    /// </summary>
    /// <exception cref="DomainRuleException">The material isn't from the ERP, or its base unit would change while in use.</exception>
    public Material ImportFromErp(string? externalVersion, string code, string name, Guid baseUomId, string materialType, MaterialUse use)
    {
        if (!IsFromErp)
        {
            throw new DomainRuleException($"{Code} is maintained in Pulaski, not imported.");
        }

        EnsureType(materialType);
        EnsureBaseUnit(baseUomId, use);
        return this with { ExternalVersion = externalVersion, Code = code, Name = name, BaseUomId = baseUomId, MaterialType = materialType };
    }

    private void EnsureBaseUnit(Guid baseUomId, MaterialUse use)
    {
        if (baseUomId != BaseUomId && use.InUse)
        {
            throw new DomainRuleException($"{Code} is in use, and its quantities are stored in its base unit; the base unit can't change.");
        }
    }

    private static void EnsureType(string materialType)
    {
        if (!MaterialTypes.All.Contains(materialType))
        {
            throw new DomainRuleException($"'{materialType}' isn't a material type; use {string.Join(", ", MaterialTypes.All)}.");
        }
    }
}

/// <summary>A flat group of materials (D-033). The code is fixed; a class is switched off, never deleted.</summary>
public sealed record MaterialClass(Guid Id, string Code, string Name, bool IsActive);
