using System.Globalization;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Web.Components;

namespace Pulaski.Web.Services.Materials;

/// <summary>Materials of one class, for the materials list (D-033).</summary>
public sealed record MaterialGroup(string Key, string Label, IReadOnlyList<MaterialDto> Items);

/// <summary>
/// The property grid rows for a material, and the update an edit makes (D-033). Fields the ERP owns,
/// or that the material's use fixes, are read-only, and their description says why.
/// </summary>
public static class MaterialProperties
{
    public const string NoClass = "";

    private const string Identity = "Identity";
    private const string Classification = "Classification";
    private const string Stock = "Stock";
    private const string Status = "Status";
    private const string Batches = "Batches";

    /// <summary>The keys of the batch settings, saved with their own request (D-036).</summary>
    public static bool IsBatchingKey(string key) => key is "batchSize" or "batchTolerance" or "batchRule";

    public static IReadOnlyList<string> Types { get; } = ["raw", "intermediate", "finished", "packaging", "consumable", "rework"];

    public static bool IsFromErp(MaterialDto m) => m.Origin == "erp";

    /// <summary>Materials by class name with "No class" last, codes in order.</summary>
    public static IReadOnlyList<MaterialGroup> Group(IEnumerable<MaterialDto> materials) =>
        [.. materials
            .GroupBy(m => m.MaterialClassCode ?? NoClass)
            .Select(g => new MaterialGroup(
                g.Key,
                g.Key == NoClass ? "No class" : g.First().MaterialClassName ?? g.Key,
                [.. g.OrderBy(m => m.Code, StringComparer.Ordinal)]))
            .OrderBy(g => g.Key == NoClass)
            .ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)];

    public static IReadOnlyList<PropertyItem> For(
        MaterialDto m, MaterialUsageDto usage, IReadOnlyList<MaterialClassDto> classes, IReadOnlyList<UomDto> uoms)
    {
        var erp = IsFromErp(m);
        var erpNote = erp ? $" Comes from {m.ExternalSystem}; change it there." : "";
        var codeFixed = erp || usage.InUse;
        var unitFixed = erp || usage.InUse;
        var trackingFixed = usage.LotsOnHand > 0;

        return
        [
            new()
            {
                Key = "code", Name = "Code", Category = Identity, Value = m.Code, MaxLength = 50,
                Editor = codeFixed ? PropertyEditor.ReadOnly : PropertyEditor.Text,
                Description = "The item or part number." + (erp ? erpNote : usage.InUse ? " Fixed: the material is in use." : ""),
            },
            new()
            {
                Key = "name", Name = "Name", Category = Identity, Value = m.Name, MaxLength = 200,
                Editor = erp ? PropertyEditor.ReadOnly : PropertyEditor.Text,
                Description = "What people call it." + erpNote,
            },
            new()
            {
                Key = "description", Name = "Description", Category = Identity, Value = m.Description, MaxLength = 2000,
                Editor = PropertyEditor.Text, Description = "Notes for the people who use it. Kept in Pulaski.",
            },
            new()
            {
                Key = "type", Name = "Type", Category = Classification, Value = m.MaterialType,
                Editor = erp ? PropertyEditor.ReadOnly : PropertyEditor.Choice,
                Choices = [.. Types.Select(t => new PropertyChoice(t, Display.Label(t)))],
                Description = "A label from the item master, such as its MRP class; it drives no rules." + erpNote,
            },
            new()
            {
                Key = "class", Name = "Class", Category = Classification, Value = m.MaterialClassId?.ToString() ?? "",
                Editor = PropertyEditor.Choice,
                Choices = [new("", "No class"), .. classes
                    .Where(c => c.IsActive || c.Id == m.MaterialClassId)
                    .Select(c => new PropertyChoice(c.Id.ToString(), $"{c.Code} · {c.Name}{(c.IsActive ? "" : " (inactive)")}"))],
                Description = "Groups similar materials. Processes and changeover rules can name a class instead of each material.",
            },
            new()
            {
                Key = "origin", Name = "Origin", Category = Classification,
                Value = erp ? $"{m.ExternalSystem} · {m.ExternalKey}{(m.ExternalVersion is { } v ? $" · version {v}" : "")}" : "Pulaski",
                Description = erp ? "Imported from an external item master, which owns the code, name, base unit and type." : "Created in Pulaski.",
            },
            new()
            {
                Key = "baseUom", Name = "Base unit", Category = Stock, Value = m.BaseUomId.ToString(),
                Editor = unitFixed ? PropertyEditor.ReadOnly : PropertyEditor.Choice,
                Choices = [.. uoms.Where(u => u.IsActive || u.Id == m.BaseUomId).Select(u => new PropertyChoice(u.Id.ToString(), $"{u.Code} · {u.Name}"))],
                Description = "The unit stock is counted in." + (erp ? erpNote : usage.InUse ? " Fixed: the material is in use, and its quantities are stored in it." : ""),
            },
            new()
            {
                Key = "lotTracked", Name = "Lot-tracked", Category = Stock, Value = PropertyItem.FromBool(m.IsLotTracked),
                Editor = trackingFixed ? PropertyEditor.ReadOnly : PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = "Each receipt is its own numbered lot; otherwise one balance per location." +
                    (trackingFixed ? " Fixed while stock is on hand." : ""),
            },
            new()
            {
                Key = "shelfLife", Name = "Shelf life", Category = Stock,
                Value = m.ShelfLifeDays?.ToString(CultureInfo.InvariantCulture), Editor = PropertyEditor.Number,
                Min = 1, Max = 36_500, Step = 1, Suffix = "days", Placeholder = "None",
                Description = "A new lot without an expiry expires this long after it arrives. Lots already on hand keep theirs.",
            },
            new()
            {
                Key = "batchSize", Name = "Batch size", Category = Batches,
                Value = m.BatchSize?.ToString("0.######", CultureInfo.InvariantCulture), Editor = PropertyEditor.Number,
                Min = 0, Step = 0.001, Suffix = m.BaseUomCode, Placeholder = "Made continuously",
                Description = "Made in batches of this size: orders and jobs are whole batches, and production is reported batch by batch. " +
                    "A batch cut short when the line stops is a partial batch. Leave blank for a material made continuously.",
            },
            .. m.BatchSize is null ? Array.Empty<PropertyItem>() : [
                new()
                {
                    Key = "batchRule", Name = "Batch rule", Category = Batches, Value = m.BatchRule, Editor = PropertyEditor.Choice,
                    Choices = [new("exact", "Exact batches; partials need approval"), new("near", "Near batches; partials accepted")],
                    Description = "Exact: every batch is exactly the batch size, and a partial batch waits on hold until approved (finished goods can't be sold as partials). " +
                        "Near: a batch may differ by the tolerance, and a partial batch is accepted because it will be used.",
                },
                new()
                {
                    Key = "batchTolerance", Name = "Batch tolerance", Category = Batches,
                    Value = m.BatchTolerancePct.ToString("0.##", CultureInfo.InvariantCulture),
                    Editor = m.BatchRule == "exact" ? PropertyEditor.ReadOnly : PropertyEditor.Number, Min = 0, Max = 50, Step = 0.1, Suffix = "%",
                    Description = m.BatchRule == "exact"
                        ? "Not used: exact batches are always the batch size."
                        : $"How far a batch may be from the batch size: a {Batching(m)} batch is accepted from {Low(m)} to {High(m)} {m.BaseUomCode}.",
                },
                new()
                {
                    Key = ErpBatchRuleKey, Name = "ERP orders not whole batches", Category = Batches, Value = m.ErpBatchRule ?? "", Editor = PropertyEditor.Choice,
                    Choices =
                    [
                        new("", "The site's rule"),
                        new("reject", "Refuse the order"),
                        new("round_up", "Round up to whole batches"),
                        new("round_down", "Round down to whole batches"),
                        new("hold", "Import it held for a planner"),
                    ],
                    Description = "What happens when the ERP sends a work order for this material that isn't whole batches (D-066). " +
                        "The site's rule is set on Site settings; the ERP's own quantity is always kept on the order beside Pulaski's.",
                },
            ],
            new()
            {
                Key = "active", Name = "Active", Category = Status, Value = PropertyItem.FromBool(m.IsActive),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = "An inactive material can't be received, ordered or added to BOMs. What already uses it carries on.",
            },
        ];
    }

    /// <summary>The material's own rule for ERP orders that aren't whole batches (D-066), saved on its own.</summary>
    public const string ErpBatchRuleKey = "erpBatchRule";

    /// <summary>The batch settings to send for an edited batch property; null when nothing changes (D-036).</summary>
    public static SetBatchingRequest? ApplyBatching(MaterialDto m, PropertyChange change)
    {
        var text = string.IsNullOrWhiteSpace(change.Value) ? null : change.Value.Trim();
        var request = new SetBatchingRequest { Version = m.Version, BatchSize = m.BatchSize, TolerancePct = m.BatchTolerancePct, Rule = m.BatchRule };
        return change.Key switch
        {
            "batchSize" => request with
            {
                BatchSize = decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var size) && size > 0 ? size : null,
                Rule = text is null ? null : m.BatchRule,
            },
            "batchRule" when text is not null => request with { Rule = text },
            "batchTolerance" when decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct) => request with { TolerancePct = pct },
            _ => null,
        };
    }

    private static string Batching(MaterialDto m) => $"{m.BatchSize?.ToString("0.###", CultureInfo.InvariantCulture)} {m.BaseUomCode}";

    private static string Low(MaterialDto m) => ((m.BatchSize ?? 0) * (1 - m.BatchTolerancePct / 100m)).ToString("0.###", CultureInfo.InvariantCulture);

    private static string High(MaterialDto m) => ((m.BatchSize ?? 0) * (1 + m.BatchTolerancePct / 100m)).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>The update to send for one edited property; null when nothing changes or the key isn't editable.</summary>
    public static UpdateMaterialRequest? ApplyChange(MaterialDto m, PropertyChange change)
    {
        var request = new UpdateMaterialRequest
        {
            Version = m.Version,
            Code = m.Code,
            Name = m.Name,
            Description = m.Description,
            MaterialType = m.MaterialType,
            MaterialClassId = m.MaterialClassId,
            BaseUomId = m.BaseUomId,
            IsLotTracked = m.IsLotTracked,
            ShelfLifeDays = m.ShelfLifeDays,
            IsActive = m.IsActive,
        };
        var text = string.IsNullOrWhiteSpace(change.Value) ? null : change.Value.Trim();

        return change.Key switch
        {
            "code" when text is not null => request with { Code = text },
            "name" when text is not null => request with { Name = text },
            "description" => request with { Description = text },
            "type" when text is not null => request with { MaterialType = text },
            "class" => request with { MaterialClassId = Guid.TryParse(text, out var c) ? c : null },
            "baseUom" when Guid.TryParse(text, out var u) => request with { BaseUomId = u },
            "lotTracked" => request with { IsLotTracked = change.Value == "true" },
            "shelfLife" => request with { ShelfLifeDays = int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : null },
            "active" => request with { IsActive = change.Value == "true" },
            _ => null,
        };
    }
}
