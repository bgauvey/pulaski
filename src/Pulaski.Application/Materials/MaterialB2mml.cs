using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Pulaski.Contracts.ReferenceData;

namespace Pulaski.Application.Materials;

/// <summary>
/// Reads ISA-95 material master data in MESA's B2MML XML (D-053) into import rows: every <c>MaterialDefinition</c>, whether the
/// root is <c>MaterialInformation</c>, a verb wrapper such as <c>SyncMaterialInformation</c>, or the definition itself. Elements
/// are matched by local name, so any B2MML namespace (or none) works. <c>ID</c> is the code, the first <c>Description</c> the name
/// and the first <c>MaterialClassID</c> the class; the other fields come from <c>MaterialDefinitionProperty</c> elements whose
/// <c>ID</c> names an import field (<c>MaterialType</c>, <c>BaseUnit</c>, <c>LotTracked</c>, <c>ShelfLifeDays</c>…), each read from
/// <c>Value/ValueString</c>. Other properties are the source's own and are skipped. Given an external system, every row is that
/// system's item keyed by its <c>ID</c>; without one, rows are Pulaski materials matched on their code.
/// </summary>
public static class MaterialB2mml
{
    // No DTDs, so no entity expansion and nothing fetched from elsewhere.
    private static readonly XmlReaderSettings Settings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
    };

    // Import fields by their names compared as property IDs are (see Normalize), aliases included.
    private static readonly Dictionary<string, string> Fields = MaterialImportFields.Names
        .SelectMany(n => n.Value.Append(n.Key).Select(name => (Key: Normalize(name), Field: n.Key)))
        .ToDictionary(p => p.Key, p => p.Field, StringComparer.Ordinal);

    // From the definition's own elements or the import, never from a property.
    private static readonly string[] NotFromProperties = ["code", "name", "external_system", "external_key"];

    /// <summary>A document with one material and every property it reads, to start from (D-055).</summary>
    public const string Template = """
        <?xml version="1.0" encoding="UTF-8"?>
        <MaterialInformation xmlns="http://www.mesa.org/xml/B2MML">
          <MaterialClass>
            <ID>RESIN</ID>
            <Description>Resins</Description>
          </MaterialClass>
          <MaterialDefinition>
            <ID>PP-1</ID>
            <Description>Polypropylene, natural</Description>
            <MaterialClassID>RESIN</MaterialClassID>
            <MaterialDefinitionProperty><ID>MaterialType</ID><Value><ValueString>raw</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>BaseUnit</ID><Value><ValueString>kg</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>LotTracked</ID><Value><ValueString>yes</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>ShelfLifeDays</ID><Value><ValueString>365</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>Active</ID><Value><ValueString>yes</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>Description</ID><Value><ValueString>Notes about the material</ValueString></Value></MaterialDefinitionProperty>
            <MaterialDefinitionProperty><ID>ClassName</ID><Value><ValueString>Resins</ValueString></Value></MaterialDefinitionProperty>
          </MaterialDefinition>
        </MaterialInformation>

        """;

    public static MaterialImportSource Parse(string text, string? externalSystem)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new MaterialImportSource([], ["The file is empty."]);
        }

        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), Settings);
            document = XDocument.Load(reader, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            return new MaterialImportSource([], [$"The file isn't well-formed XML: {ex.Message}"]);
        }

        var definitions = document.Descendants().Where(e => e.Name.LocalName == "MaterialDefinition").ToList();
        var hasClasses = document.Descendants().Any(e => e.Name.LocalName == "MaterialClass");
        if (definitions.Count == 0 && !hasClasses)
        {
            return new MaterialImportSource([], ["The file has no MaterialDefinition elements; B2MML material information has one per material."]);
        }

        if (definitions.Count > MaterialImportFields.MaxRows)
        {
            return new MaterialImportSource([], [$"The file has {definitions.Count} materials; import at most {MaterialImportFields.MaxRows} at a time."]);
        }

        // MaterialClass elements are classes in their own right (#72): ID the code, the first Description the name.
        var classes = document.Descendants().Where(e => e.Name.LocalName == "MaterialClass")
            .Select(c => new MaterialClassImportRow(
                ((IXmlLineInfo)c).HasLineInfo() ? ((IXmlLineInfo)c).LineNumber : 0,
                Children(c, "ID").FirstOrDefault()?.Value.Trim() ?? "",
                Children(c, "Description").FirstOrDefault()?.Value.Trim() ?? Children(c, "ID").FirstOrDefault()?.Value.Trim() ?? ""))
            .ToList();
        var system = string.IsNullOrWhiteSpace(externalSystem) ? null : externalSystem.Trim();
        return new MaterialImportSource([.. definitions.Select(d => ToRow(d, system))], []) { Classes = classes };
    }

    private static MaterialImportRow ToRow(XElement definition, string? system)
    {
        var problems = new List<string>();
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? First(XElement parent, string name) => Children(parent, name).FirstOrDefault()?.Value.Trim();

        var id = First(definition, "ID") ?? "";
        fields["code"] = id;
        if (First(definition, "Description") is { } name)
        {
            fields["name"] = name;
        }
        else
        {
            problems.Add("Description is missing; it is the material's name.");
        }

        if (First(definition, "MaterialClassID") is { } classCode)
        {
            fields["class"] = classCode;
        }

        if (system is not null)
        {
            fields["external_system"] = system;
            fields["external_key"] = id;
        }

        foreach (var property in Children(definition, "MaterialDefinitionProperty"))
        {
            var propertyId = First(property, "ID") ?? "";
            if (!Fields.TryGetValue(Normalize(propertyId), out var field) || NotFromProperties.Contains(field))
            {
                continue;
            }

            var value = Children(property, "Value").FirstOrDefault();
            var text = value is null ? "" : First(value, "ValueString") ?? "";
            if (field == "base_unit" && text.Length == 0 && value is not null)
            {
                text = First(value, "UnitOfMeasure") ?? "";
            }

            // The class may come from MaterialClassID or a property, not both.
            if (!fields.TryAdd(field, text))
            {
                problems.Add($"{field} is given twice (property '{propertyId}').");
            }
        }

        var line = ((IXmlLineInfo)definition).HasLineInfo() ? ((IXmlLineInfo)definition).LineNumber : 0;
        return MaterialImportFields.ToRow(line, f => fields.GetValueOrDefault(f), problems);
    }

    /// <summary>
    /// Classes and materials as B2MML material information (#72), in the form <see cref="Parse"/> reads back: a <c>MaterialClass</c> per
    /// class, then a <c>MaterialDefinition</c> per material with every import field as a property.
    /// </summary>
    public static string Write(IEnumerable<MaterialClassDto> classes, IEnumerable<MaterialDto> materials)
    {
        XNamespace ns = "http://www.mesa.org/xml/B2MML";
        static XElement Property(XNamespace ns, string id, string? value) =>
            new(ns + "MaterialDefinitionProperty", new XElement(ns + "ID", id), new XElement(ns + "Value", new XElement(ns + "ValueString", value ?? "")));
        static string YesNo(bool value) => value ? "yes" : "no";

        var root = new XElement(ns + "MaterialInformation",
            classes.Select(c => new XElement(ns + "MaterialClass", new XElement(ns + "ID", c.Code), new XElement(ns + "Description", c.Name))),
            materials.Select(m => new XElement(ns + "MaterialDefinition",
                new XElement(ns + "ID", m.Code),
                new XElement(ns + "Description", m.Name),
                m.MaterialClassCode is { } classCode ? new XElement(ns + "MaterialClassID", classCode) : null,
                Property(ns, "MaterialType", m.MaterialType),
                Property(ns, "BaseUnit", m.BaseUomCode),
                Property(ns, "LotTracked", YesNo(m.IsLotTracked)),
                Property(ns, "ShelfLifeDays", m.ShelfLifeDays?.ToString(CultureInfo.InvariantCulture)),
                Property(ns, "Active", YesNo(m.IsActive)),
                Property(ns, "Description", m.Description))));
        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).Declaration + Environment.NewLine + root + Environment.NewLine;
    }

    private static IEnumerable<XElement> Children(XElement parent, string localName) =>
        parent.Elements().Where(e => e.Name.LocalName == localName);

    // Property IDs are usually PascalCase: "BaseUoM", "Shelf life days" and "lot_tracked" all compare as "baseuom", "shelflifedays", "lottracked".
    private static string Normalize(string id) =>
        string.Concat(id.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
