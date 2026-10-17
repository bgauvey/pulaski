using System.Globalization;
using Pulaski.Application.Abstractions;
using Pulaski.Application.Errors;
using Pulaski.Application.Units;
using Pulaski.Contracts.Inventory;
using Pulaski.Domain;
using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Inventory;

/// <summary>Checks and lookups shared by the inventory commands (D-027). Everything runs inside the command's transaction.</summary>
internal static class InventoryRules
{
    public const int MaxLotNumber = 100;
    public const int MaxComment = 1000;
    public const int MaxReference = 100;

    private static readonly HashSet<string> ClosedJobStatuses = ["completed", "closed", "cancelled"];

    /// <summary>The lot, locked, at the version the caller read (when one is given).</summary>
    public static async Task<Lot> LotAsync(IInventoryRepository repository, Guid id, long? expectedVersion, CancellationToken cancellationToken)
    {
        var current = await repository.GetLotForUpdateAsync(id, cancellationToken)
            ?? throw new NotFoundException($"Lot {id} not found.");
        if (expectedVersion is { } expected && current.Version != expected)
        {
            throw new ConflictException($"{current.Value.Name} was changed by someone else. Reload and try again.");
        }

        return current.Value;
    }

    public static async Task<InventoryMaterial> MaterialAsync(IInventoryRepository repository, Guid id, CancellationToken cancellationToken) =>
        await repository.GetMaterialAsync(id, cancellationToken) ?? throw new DomainRuleException($"Material {id} not found.");

    /// <summary>An active location that holds inventory, in <paramref name="siteId"/> when one is given.</summary>
    public static async Task<InventoryLocation> LocationAsync(
        IInventoryRepository repository, Guid id, Guid? siteId, CancellationToken cancellationToken)
    {
        var location = await repository.GetLocationAsync(id, cancellationToken)
            ?? throw new DomainRuleException($"Location {id} not found.");
        if (!location.HoldsInventory)
        {
            throw new DomainRuleException($"{location.Code} doesn't hold inventory; mark it on the Equipment page, or choose another location.");
        }

        if (!location.IsActive)
        {
            throw new DomainRuleException($"{location.Code} is inactive.");
        }

        if (siteId is { } site && location.SiteId != site)
        {
            throw new DomainRuleException($"{location.Code} is in another site; a lot stays in its site.");
        }

        return location;
    }

    /// <summary>
    /// The quantity in the material's base unit (D-026), rounded to six places.
    /// </summary>
    /// <exception cref="DomainRuleException">The unit doesn't convert for the material, or the result rounds to zero.</exception>
    public static async Task<decimal> ToBaseAsync(
        IUnitRepository units, InventoryMaterial material, decimal quantity, Guid uomId, CancellationToken cancellationToken)
    {
        var all = await units.GetUnitsAsync(cancellationToken);
        var conversions = await units.GetConversionsAsync([material.Id], cancellationToken);
        var converted = UnitConversions.For(material.Id, all, conversions).Convert(quantity, uomId, material.BaseUomId);
        if (converted <= 0)
        {
            throw new DomainRuleException($"{Qty(quantity)} is too small to record in {material.BaseUomCode}.");
        }

        return converted;
    }

    /// <summary>
    /// The reason, checked: of <paramref name="kind"/>, active, selectable, enterprise-wide or for
    /// the site, and with a comment when it asks for one.
    /// </summary>
    public static async Task<InventoryReason> ReasonAsync(
        IInventoryRepository repository, Guid id, string kind, Guid siteId, string? comment, CancellationToken cancellationToken)
    {
        var reason = await repository.GetReasonAsync(id, cancellationToken);
        if (reason is null || reason.Kind != kind || (reason.SiteId is { } site && site != siteId))
        {
            throw new DomainRuleException(kind == ReasonKinds.Hold
                ? "Choose a hold reason."
                : "Choose an adjustment reason.");
        }

        if (!reason.IsActive || !reason.IsSelectable)
        {
            throw new DomainRuleException($"Reason {reason.Name} can't be chosen.");
        }

        if (reason.RequiresComment && comment is null)
        {
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["Comment"] = [$"Reason {reason.Name} needs a comment."],
            });
        }

        return reason;
    }

    /// <summary>The job, locked: an open production job in <paramref name="siteId"/> when one is given.</summary>
    public static async Task<InventoryJob> JobAsync(IInventoryRepository repository, Guid id, Guid? siteId, CancellationToken cancellationToken)
    {
        var job = await repository.GetJobForUpdateAsync(id, cancellationToken)
            ?? throw new DomainRuleException($"Job {id} not found.");
        if (job.JobType != "production" || job.MaterialId is null)
        {
            throw new DomainRuleException($"{job.JobNumber} is a {job.JobType.Replace('_', ' ')} job; consumption and production are declared against production jobs.");
        }

        if (ClosedJobStatuses.Contains(job.Status))
        {
            throw new DomainRuleException($"{job.JobNumber} is {job.Status}; nothing more can be declared against it.");
        }

        if (siteId is { } site && job.SiteId != site)
        {
            throw new DomainRuleException($"{job.JobNumber} is in another site.");
        }

        return job;
    }

    /// <summary>A trimmed comment, or null when blank.</summary>
    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>A lot number given by a person, when one is given.</summary>
    public static Validator RequireLotNumber(this Validator validator, string? number, string field) =>
        validator.Require(number is null || number.Length <= MaxLotNumber, field, $"At most {MaxLotNumber} characters.");

    public static Validator RequireComment(this Validator validator, string? comment) =>
        validator.Require(comment is null || comment.Trim().Length <= MaxComment, "Comment", $"At most {MaxComment} characters.");

    public static LotTransactionResponse Response(Lot lot, long version, IReadOnlyList<InventoryWarningDto>? warnings = null) => new()
    {
        LotId = lot.Id,
        LotNumber = lot.LotNumber,
        Version = version,
        Quantity = lot.Quantity,
        Status = lot.Status,
        Warnings = warnings ?? [],
    };

    /// <summary>Edges from every parent to every child, leaving out a lot linked to itself.</summary>
    public static List<GenealogyEdge> Edges(IIdGenerator ids, IEnumerable<Guid> parents, IEnumerable<Guid> children, Guid jobId) =>
        [.. from parent in parents
            from child in children
            where parent != child
            select new GenealogyEdge(ids.NewId(), parent, child, jobId)];

    public static string Qty(decimal value) => value.ToString("0.######", CultureInfo.InvariantCulture);
}
