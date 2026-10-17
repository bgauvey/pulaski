namespace Pulaski.Domain.EquipmentModel;

/// <summary>One node of the ISA-95 equipment hierarchy (work center, unit, machine, storage...).</summary>
public sealed record Equipment(
    Guid Id,
    Guid? ParentId,
    Guid? SiteId,
    string Code,
    string Name,
    string? Description,
    string Level,
    string EquipmentRole,
    bool TracksState,
    bool IsSchedulable,
    bool HoldsInventory,
    string? TimeZone,
    bool IsActive,
    bool CanRunJob = false,
    bool ComminglesLots = false,
    bool SingleMaterial = false)
{
    /// <summary>
    /// A planning target that runs nothing itself, such as a department: jobs planned to it are
    /// run by its descendants that <see cref="CanRunJob"/> (D-022).
    /// </summary>
    public bool IsSchedulingGroup => IsSchedulable && !CanRunJob;

    /// <summary>
    /// Creates equipment under <paramref name="parent"/> (null only for the enterprise) and works out
    /// its owning site: a site owns itself, the enterprise has none, everything else inherits.
    /// </summary>
    /// <exception cref="DomainRuleException">The level/role pair or the placement is not allowed.</exception>
    public static Equipment Create(
        Guid id,
        Equipment? parent,
        string code,
        string name,
        string? description,
        string level,
        string equipmentRole,
        bool tracksState,
        bool isSchedulable,
        bool holdsInventory,
        string? timeZone,
        bool canRunJob = false,
        bool comminglesLots = false,
        bool singleMaterial = false)
    {
        if (!EquipmentTypes.IsValidLevelRole(level, equipmentRole))
        {
            throw new DomainRuleException($"'{equipmentRole}' is not a valid role for level '{level}'.");
        }

        if (parent is null && EquipmentTypes.RequiresParent(level))
        {
            throw new DomainRuleException($"A {level} needs a parent.");
        }

        if (parent is not null && !EquipmentTypes.CanBeChildOf(level, parent.Level))
        {
            throw new DomainRuleException($"Level {level} cannot be placed under level {parent.Level}.");
        }

        EnsureStorageFlags(holdsInventory, comminglesLots, singleMaterial);

        var siteId = level switch
        {
            EquipmentTypes.Enterprise => (Guid?)null,
            EquipmentTypes.Site => id,
            _ => parent!.SiteId,
        };

        return new Equipment(
            id, parent?.Id, siteId, code, name, description, level, equipmentRole,
            tracksState, isSchedulable, holdsInventory, timeZone, IsActive: true, canRunJob, comminglesLots, singleMaterial);
    }

    /// <summary>Lots can only mix (D-031), and a location only take one material (D-032), where lots are kept.</summary>
    /// <exception cref="DomainRuleException">A storage rule without holding inventory.</exception>
    public static void EnsureStorageFlags(bool holdsInventory, bool comminglesLots, bool singleMaterial)
    {
        if ((comminglesLots || singleMaterial) && !holdsInventory)
        {
            throw new DomainRuleException(
                $"{(comminglesLots ? "Lots can only be indistinguishable" : "One material at a time only applies")} where inventory is held; tick Holds inventory too.");
        }
    }
}
