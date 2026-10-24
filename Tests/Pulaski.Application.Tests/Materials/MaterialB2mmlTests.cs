using Pulaski.Application.Materials;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Reading ISA-95 material information in B2MML XML into import rows (D-053).</summary>
public class MaterialB2mmlTests
{
    private const string Resin = """
        <?xml version="1.0" encoding="UTF-8"?>
        <SyncMaterialInformation xmlns="http://www.mesa.org/xml/B2MML">
          <DataArea>
            <Sync />
            <MaterialInformation>
              <ID>ITEMS-2026-10</ID>
              <MaterialDefinition>
                <ID>PP-1</ID>
                <Description>Polypropylene</Description>
                <Description>Second language name</Description>
                <MaterialClassID>RESIN</MaterialClassID>
                <MaterialDefinitionProperty>
                  <ID>MaterialType</ID>
                  <Value><ValueString>Raw</ValueString><DataType>string</DataType></Value>
                </MaterialDefinitionProperty>
                <MaterialDefinitionProperty>
                  <ID>BaseUoM</ID>
                  <Value><ValueString /><UnitOfMeasure>kg</UnitOfMeasure></Value>
                </MaterialDefinitionProperty>
                <MaterialDefinitionProperty>
                  <ID>Shelf Life Days</ID>
                  <Value><ValueString>180</ValueString><DataType>integer</DataType></Value>
                </MaterialDefinitionProperty>
                <MaterialDefinitionProperty>
                  <ID>IsLotTracked</ID>
                  <Value><ValueString>false</ValueString><DataType>boolean</DataType></Value>
                </MaterialDefinitionProperty>
                <MaterialDefinitionProperty>
                  <ID>Density</ID>
                  <Value><ValueString>0.905</ValueString><UnitOfMeasure>g/cm3</UnitOfMeasure></Value>
                </MaterialDefinitionProperty>
              </MaterialDefinition>
            </MaterialInformation>
          </DataArea>
        </SyncMaterialInformation>
        """;

    [Fact]
    public void A_definition_becomes_a_row_from_its_id_description_class_and_named_properties()
    {
        var source = MaterialB2mml.Parse(Resin, externalSystem: null);

        Assert.Empty(source.Problems);
        var row = Assert.Single(source.Rows);
        Assert.Equal((7, "PP-1", "Polypropylene", "raw", "kg"), (row.Line, row.Code, row.Name, row.MaterialType, row.BaseUomCode));
        Assert.Equal(("RESIN", 180, false), (row.ClassCode!.Value, row.ShelfLifeDays!.Value, row.IsLotTracked!.Value));
        Assert.Empty(row.Problems);

        // Not in the file, so each material keeps what it has; local because no system was named.
        Assert.Null(row.Description);
        Assert.Null(row.IsActive);
        Assert.Null(row.ExternalSystem);
        Assert.Null(row.ExternalKey);
    }

    [Fact]
    public void Named_with_an_external_system_every_row_is_its_item_keyed_by_id()
    {
        var row = Assert.Single(MaterialB2mml.Parse(Resin, " ERP1 ").Rows);

        Assert.Equal(("ERP1", "PP-1"), (row.ExternalSystem, row.ExternalKey));
    }

    [Fact]
    public void Any_namespace_or_none_and_a_bare_definition_work()
    {
        var bare = """
            <MaterialDefinition>
              <ID>CAP-1</ID><Description>End cap</Description>
              <MaterialDefinitionProperty><ID>type</ID><Value><ValueString>packaging</ValueString></Value></MaterialDefinitionProperty>
              <MaterialDefinitionProperty><ID>base_unit</ID><Value><ValueString>ea</ValueString></Value></MaterialDefinitionProperty>
              <MaterialDefinitionProperty><ID>Description</ID><Value><ValueString>Grey</ValueString></Value></MaterialDefinitionProperty>
              <MaterialDefinitionProperty><ID>ShelfLifeDays</ID></MaterialDefinitionProperty>
            </MaterialDefinition>
            """;
        var row = Assert.Single(MaterialB2mml.Parse(bare, null).Rows);
        Assert.Equal(("CAP-1", "End cap", "packaging", "ea", "Grey"), (row.Code, row.Name, row.MaterialType, row.BaseUomCode, row.Description!.Value));

        // A property with no value clears the field, as a blank CSV cell does.
        Assert.Null(row.ShelfLifeDays!.Value);

        var old = Assert.Single(MaterialB2mml.Parse(bare.Replace("<MaterialDefinition>", "<MaterialDefinition xmlns=\"http://www.mesa.org/xml/B2MML-V0600\">", StringComparison.Ordinal), null).Rows);
        Assert.Equal("CAP-1", old.Code);
    }

    [Fact]
    public void Problems_with_a_definition_are_kept_on_its_row()
    {
        var xml = """
            <MaterialInformation>
              <MaterialDefinition>
                <ID>A</ID>
                <MaterialClassID>X</MaterialClassID>
                <MaterialDefinitionProperty><ID>Class</ID><Value><ValueString>Y</ValueString></Value></MaterialDefinitionProperty>
                <MaterialDefinitionProperty><ID>LotTracked</ID><Value><ValueString>maybe</ValueString></Value></MaterialDefinitionProperty>
              </MaterialDefinition>
              <MaterialDefinition><ID>B</ID><Description>B</Description></MaterialDefinition>
            </MaterialInformation>
            """;
        var source = MaterialB2mml.Parse(xml, null);

        Assert.Equal(["A", "B"], source.Rows.Select(r => r.Code));
        Assert.Equal(3, source.Rows[0].Problems.Count);
        Assert.Contains(source.Rows[0].Problems, p => p.Contains("Description is missing", StringComparison.Ordinal));
        Assert.Contains(source.Rows[0].Problems, p => p.Contains("given twice", StringComparison.Ordinal));
        Assert.Contains(source.Rows[0].Problems, p => p.Contains("isn't yes or no", StringComparison.Ordinal));
        Assert.Empty(source.Rows[1].Problems);
    }

    [Fact]
    public void Problems_with_the_file_are_reported_not_thrown_and_dtds_are_refused()
    {
        Assert.Equal(["The file is empty."], MaterialB2mml.Parse("  ", null).Problems);
        Assert.StartsWith("The file isn't well-formed XML", Assert.Single(MaterialB2mml.Parse("<MaterialInformation>", null).Problems), StringComparison.Ordinal);
        Assert.Contains("no MaterialDefinition", Assert.Single(MaterialB2mml.Parse("<MaterialInformation />", null).Problems), StringComparison.Ordinal);

        var entity = """
            <?xml version="1.0"?>
            <!DOCTYPE m [<!ENTITY x SYSTEM "file:///etc/passwd">]>
            <MaterialDefinition><ID>&x;</ID></MaterialDefinition>
            """;
        var refused = MaterialB2mml.Parse(entity, null);
        Assert.Empty(refused.Rows);
        Assert.StartsWith("The file isn't well-formed XML", Assert.Single(refused.Problems), StringComparison.Ordinal);

        var big = "<MaterialInformation>" + string.Concat(Enumerable.Repeat("<MaterialDefinition><ID>M</ID></MaterialDefinition>", MaterialImportFields.MaxRows + 1)) + "</MaterialInformation>";
        Assert.Contains("at most", Assert.Single(MaterialB2mml.Parse(big, null).Problems), StringComparison.Ordinal);
    }
}
