using System.Globalization;
using Pulaski.Contracts.Materials;
using Pulaski.Contracts.ReferenceData;
using Pulaski.Web.Components;
using Pulaski.Web.Services.Localization;

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

    private static readonly string Identity = Term.Mark("Identity");
    private static readonly string Classification = Term.Mark("Classification");
    private static readonly string Stock = Term.Mark("Stock");
    private static readonly string Status = Term.Mark("Status");
    private static readonly string Batches = Term.Mark("Batches");

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
                g.Key == NoClass ? Term.Mark("No class") : g.First().MaterialClassName ?? g.Key,
                [.. g.OrderBy(m => m.Code, StringComparer.Ordinal)]))
            .OrderBy(g => g.Key == NoClass)
            .ThenBy(g => g.Label, StringComparer.CurrentCultureIgnoreCase)];

    public static IReadOnlyList<PropertyItem> For(Text l,
        MaterialDto m, MaterialUsageDto usage, IReadOnlyList<MaterialClassDto> classes, IReadOnlyList<UomDto> uoms)
    {
        var erp = IsFromErp(m);
        var erpNote = erp ? l[" Comes from {0}; change it there.", m.ExternalSystem] : "";
        var codeFixed = erp || usage.InUse;
        var unitFixed = erp || usage.InUse;
        var trackingFixed = usage.LotsOnHand > 0;

        return
        [
            new()
            {
                Key = "code", Name = l["Code"], Category = l[Identity], Value = m.Code, MaxLength = 50,
                Editor = codeFixed ? PropertyEditor.ReadOnly : PropertyEditor.Text,
                Description = l["The item or part number."] + (erp ? erpNote : usage.InUse ? l[" Fixed: the material is in use."] : ""),
            },
            new()
            {
                Key = "name", Name = l["Name"], Category = l[Identity], Value = m.Name, MaxLength = 200,
                Editor = erp ? PropertyEditor.ReadOnly : PropertyEditor.Text,
                Description = l["What people call it."] + erpNote,
            },
            new()
            {
                Key = "description", Name = l["Description"], Category = l[Identity], Value = m.Description, MaxLength = 2000,
                Editor = PropertyEditor.Text, Description = l["Notes for the people who use it. Kept in Pulaski."],
            },
            new()
            {
                Key = "type", Name = l["Type"], Category = l[Classification], Value = m.MaterialType,
                Editor = erp ? PropertyEditor.ReadOnly : PropertyEditor.Choice,
                Choices = [.. Types.Select(t => new PropertyChoice(t, Display.Label(t)))],
                Description = l["A label from the item master, such as its MRP class; it drives no rules."] + erpNote,
            },
            new()
            {
                Key = "class", Name = l["Class"], Category = l[Classification], Value = m.MaterialClassId?.ToString() ?? "",
                Editor = PropertyEditor.Choice,
                Choices = [new("", l["No class"]), .. classes
                    .Where(c => c.IsActive || c.Id == m.MaterialClassId)
                    .Select(c => new PropertyChoice(c.Id.ToString(), $"{c.Code} · {c.Name}{(c.IsActive ? "" : " (inactive)")}"))],
                Description = l["Groups similar materials. Processes and changeover rules can name a class instead of each material."],
            },
            new()
            {
                Key = "origin", Name = l["Origin"], Category = l[Classification],
                Value = erp ? $"{m.ExternalSystem} · {m.ExternalKey}{(m.ExternalVersion is { } v ? $" · version {v}" : "")}" : l["Pulaski"],
                Description = erp ? l["Imported from an external item master, which owns the code, name, base unit and type."] : l["Created in Pulaski."],
            },
            new()
            {
                Key = "baseUom", Name = l["Base unit"], Category = l[Stock], Value = m.BaseUomId.ToString(),
                Editor = unitFixed ? PropertyEditor.ReadOnly : PropertyEditor.Choice,
                Choices = [.. uoms.Where(u => u.IsActive || u.Id == m.BaseUomId).Select(u => new PropertyChoice(u.Id.ToString(), $"{u.Code} · {u.Name}"))],
                Description = l["The unit stock is counted in."] + (erp ? erpNote : usage.InUse ? l[" Fixed: the material is in use, and its quantities are stored in it."] : ""),
            },
            new()
            {
                Key = "lotTracked", Name = l["Lot-tracked"], Category = l[Stock], Value = PropertyItem.FromBool(m.IsLotTracked),
                Editor = trackingFixed ? PropertyEditor.ReadOnly : PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = l["Each receipt is its own numbered lot; otherwise one balance per location."] +
                    (trackingFixed ? l[" Fixed while stock is on hand."] : ""),
            },
            new()
            {
                Key = "shelfLife", Name = l["Shelf life"], Category = l[Stock],
                Value = m.ShelfLifeDays?.ToString(CultureInfo.InvariantCulture), Editor = PropertyEditor.Number,
                Min = 1, Max = 36_500, Step = 1, Suffix = l["days"], Placeholder = l["None"],
                Description = l["A new lot without an expiry expires this long after it arrives. Lots already on hand keep theirs."],
            },
            new()
            {
                Key = GtinKey, Name = l["GTIN"], Category = l[Stock], Value = m.Gtin, Editor = PropertyEditor.Text, Placeholder = l["None"],
                Description = l["Its GS1 trade item number (8, 12, 13 or 14 digits). Lots of it get GS1 labels when the site prints them, and a scanned GS1 or supplier barcode with it finds this material."],
            },
            new()
            {
                Key = LeadTimeKey, Name = l["Lead time"], Category = l[Stock],
                Value = m.LeadTimeDays?.ToString(CultureInfo.InvariantCulture), Editor = PropertyEditor.Number,
                Min = 1, Max = 3650, Step = 1, Suffix = l["days"], Placeholder = l["None"],
                Description = l["How long supply of it takes to come. The shortage check flags shortages needed sooner than this from now: asked for today, supply wouldn't arrive in time. Nothing is ordered."],
            },
            new()
            {
                Key = "batchSize", Name = l["Batch size"], Category = l[Batches],
                Value = m.BatchSize?.ToString("0.######", CultureInfo.InvariantCulture), Editor = PropertyEditor.Number,
                Min = 0, Step = 0.001, Suffix = m.BaseUomCode, Placeholder = l["Made continuously"],
                Description = l["Made in batches of this size: orders and jobs are whole batches, and production is reported batch by batch. A batch cut short when the line stops is a partial batch. Leave blank for a material made continuously."],
            },
            .. m.BatchSize is null ? Array.Empty<PropertyItem>() : [
                new()
                {
                    Key = "batchRule", Name = l["Batch rule"], Category = l[Batches], Value = m.BatchRule, Editor = PropertyEditor.Choice,
                    Choices = [new("exact", l["Exact batches; partials need approval"]), new("near", l["Near batches; partials accepted"])],
                    Description = l["Exact: every batch is exactly the batch size, and a partial batch waits on hold until approved (finished goods can't be sold as partials). Near: a batch may differ by the tolerance, and a partial batch is accepted because it will be used."],
                },
                new()
                {
                    Key = "batchTolerance", Name = l["Batch tolerance"], Category = l[Batches],
                    Value = m.BatchTolerancePct.ToString("0.##", CultureInfo.InvariantCulture),
                    Editor = m.BatchRule == "exact" ? PropertyEditor.ReadOnly : PropertyEditor.Number, Min = 0, Max = 50, Step = 0.1, Suffix = "%",
                    Description = m.BatchRule == "exact"
                        ? l["Not used: exact batches are always the batch size."]
                        : l["How far a batch may be from the batch size: a {0} batch is accepted from {1} to {2} {3}.", Batching(m), Low(m), High(m), m.BaseUomCode],
                },
                new()
                {
                    Key = ErpBatchRuleKey, Name = l["ERP orders not whole batches"], Category = l[Batches], Value = m.ErpBatchRule ?? "", Editor = PropertyEditor.Choice,
                    Choices =
                    [
                        new("", l["The site's rule"]),
                        new("reject", l["Refuse the order"]),
                        new("round_up", l["Round up to whole batches"]),
                        new("round_down", l["Round down to whole batches"]),
                        new("hold", l["Import it held for a planner"]),
                    ],
                    Description = l["What happens when the ERP sends a work order for this material that isn't whole batches (D-066). The site's rule is set on Site settings; the ERP's own quantity is always kept on the order beside Pulaski's."],
                },
            ],
            new()
            {
                Key = "active", Name = l["Active"], Category = l[Status], Value = PropertyItem.FromBool(m.IsActive),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = l["An inactive material can't be received, ordered or added to BOMs. What already uses it carries on."],
            },
        ];
    }

    /// <summary>The material's lead time (#83), saved on its own.</summary>
    public const string LeadTimeKey = "leadTime";

    /// <summary>The material's GTIN (#75), saved on its own.</summary>
    public const string GtinKey = "gtin";

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
