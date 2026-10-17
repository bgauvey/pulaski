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
                Key = "active", Name = "Active", Category = Status, Value = PropertyItem.FromBool(m.IsActive),
                Editor = PropertyEditor.Choice, Choices = PropertyChoice.Boolean,
                Description = "An inactive material can't be received, ordered or added to BOMs. What already uses it carries on.",
            },
        ];
    }

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
