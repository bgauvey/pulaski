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

    [Fact]
    public void Material_classes_are_read_as_records_and_a_file_of_classes_alone_is_fine()
    {
        var source = MaterialB2mml.Parse("""
            <MaterialInformation>
              <MaterialClass><ID>RESIN</ID><Description>Resins</Description></MaterialClass>
              <MaterialClass><ID>CAPS</ID></MaterialClass>
            </MaterialInformation>
            """, externalSystem: null);

        Assert.Empty(source.Problems);
        Assert.Empty(source.Rows);
        Assert.Equal([("RESIN", "Resins"), ("CAPS", "CAPS")], source.Classes.Select(c => (c.Code, c.Name)));
    }

    [Fact]
    public void The_template_reads_cleanly_and_an_export_reads_back_as_it_was_written()
    {
        var template = MaterialB2mml.Parse(MaterialB2mml.Template, null);
        Assert.Empty(template.Problems);
        Assert.Equal(("RESIN", 1), (Assert.Single(template.Classes).Code, template.Rows.Count));

        var written = MaterialB2mml.Write(
            [new Contracts.ReferenceData.MaterialClassDto { Id = Guid.CreateVersion7(), Code = "RESIN", Name = "Resins & co" }],
            [new Contracts.ReferenceData.MaterialDto
            {
                Id = Guid.CreateVersion7(), Code = "PP-1", Name = "Polypropylene", MaterialType = "raw", BaseUomId = Guid.CreateVersion7(), BaseUomCode = "kg",
                MaterialClassCode = "RESIN", IsLotTracked = false, ShelfLifeDays = 180, IsActive = true, Description = "Natural",
            }]);
        var read = MaterialB2mml.Parse(written, null);

        Assert.Empty(read.Problems);
        Assert.Equal(("RESIN", "Resins & co"), (Assert.Single(read.Classes).Code, read.Classes[0].Name));
        var row = Assert.Single(read.Rows);
        Assert.Equal(("PP-1", "Polypropylene", "raw", "kg", "RESIN"), (row.Code, row.Name, row.MaterialType, row.BaseUomCode, row.ClassCode!.Value));
        Assert.Equal((false, 180, true, "Natural"), (row.IsLotTracked!.Value, row.ShelfLifeDays!.Value, row.IsActive!.Value, row.Description!.Value));
        Assert.Empty(row.Problems);
    }

    [Fact]
    public void Material_lots_are_read_with_their_quantity_location_expiry_supplier_lot_and_other_properties()
    {
        var source = Pulaski.Application.Inventory.LotB2mml.Parse("""
            <SyncMaterialInformation xmlns="http://www.mesa.org/xml/B2MML"><DataArea><MaterialInformation>
              <MaterialLot>
                <ID>L-1</ID><MaterialDefinitionID>PP-1</MaterialDefinitionID><StorageLocation>WH-1</StorageLocation>
                <Quantity><QuantityString>1,250.5</QuantityString><UnitOfMeasure>kg</UnitOfMeasure></Quantity>
                <MaterialLotProperty><ID>Vendor Lot</ID><Value><ValueString>S-9</ValueString></Value></MaterialLotProperty>
                <MaterialLotProperty><ID>ExpirationDate</ID><Value><ValueString>2027-01-31T12:00:00+01:00</ValueString></Value></MaterialLotProperty>
                <MaterialLotProperty><ID>MFI</ID><Value><ValueString>12.4</ValueString></Value></MaterialLotProperty>
              </MaterialLot>
              <MaterialLot><ID>L-2</ID><Quantity><QuantityString>lots</QuantityString></Quantity><ExpirationTime>soon</ExpirationTime></MaterialLot>
            </MaterialInformation></DataArea></SyncMaterialInformation>
            """);

        Assert.Empty(source.Problems);
        var first = source.Rows[0];
        Assert.Equal(("L-1", "PP-1", 1250.5m, "kg", "WH-1", "S-9"), (first.LotNumber, first.MaterialCode, first.Quantity!.Value, first.UomCode!, first.LocationCode!, first.SupplierLot!));
        Assert.Equal(new DateTimeOffset(2027, 1, 31, 11, 0, 0, TimeSpan.Zero), first.ExpiresAt);
        Assert.Equal([("MFI", "12.4")], first.Properties);
        Assert.Empty(first.Problems);

        Assert.Equal(3, source.Rows[1].Problems.Count);
        Assert.Contains(Pulaski.Application.Inventory.LotB2mml.Parse("<MaterialInformation/>").Problems, p => p.Contains("no MaterialLot", StringComparison.Ordinal));
        Assert.Empty(Pulaski.Application.Inventory.LotB2mml.Parse(Pulaski.Application.Inventory.LotB2mml.Template).Rows[0].Problems);
    }
}
