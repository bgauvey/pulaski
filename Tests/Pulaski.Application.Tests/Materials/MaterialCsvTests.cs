using Pulaski.Application.Materials;
using Pulaski.Contracts.Materials;

namespace Pulaski.Application.Tests.Materials;

/// <summary>Reading a materials CSV file into import rows (D-034).</summary>
public class MaterialCsvTests
{
    [Fact]
    public void Columns_are_named_in_any_order_case_and_spelling()
    {
        var source = MaterialCsv.Parse("﻿Base Unit,Item Code,NAME,material-type\r\nkg,PP-1,Polypropylene,Raw\r\n");

        Assert.Empty(source.Problems);
        var row = Assert.Single(source.Rows);
        Assert.Equal((2, "PP-1", "Polypropylene", "raw", "kg"), (row.Line, row.Code, row.Name, row.MaterialType, row.BaseUomCode));
        Assert.Null(row.ClassCode);
        Assert.Null(row.IsLotTracked);
    }

    [Fact]
    public void Quoted_fields_hold_separators_quotes_and_line_breaks_and_semicolons_work_too()
    {
        var comma = MaterialCsv.Parse("code,name,type,base_unit,description\nA,\"Profile, white\",finished,m,\"Say \"\"hi\"\"\nthen go\"\nB,End cap,packaging,ea,\n");
        Assert.Equal(["A", "B"], comma.Rows.Select(r => r.Code));
        Assert.Equal(("Profile, white", "Say \"hi\"\nthen go"), (comma.Rows[0].Name, comma.Rows[0].Description!.Value));
        Assert.Equal(4, comma.Rows[1].Line);

        var semicolon = MaterialCsv.Parse("code;name;type;base_unit;shelf_life_days\nA;Resin, natural;raw;kg;180");
        Assert.Equal(("Resin, natural", 180), (semicolon.Rows[0].Name, semicolon.Rows[0].ShelfLifeDays!.Value));
    }

    [Fact]
    public void A_blank_cell_clears_a_field_and_a_missing_column_leaves_it_alone()
    {
        var source = MaterialCsv.Parse("code,name,type,base_unit,class,shelf_life_days,lot_tracked,active\nA,A,raw,kg,,,no,\n");

        var row = Assert.Single(source.Rows);
        Assert.Null(row.ClassCode!.Value);
        Assert.Null(row.ShelfLifeDays!.Value);
        Assert.False(row.IsLotTracked!.Value);
        Assert.Null(row.IsActive);
        Assert.Null(row.Description);
    }

    [Fact]
    public void Problems_with_the_file_or_a_row_are_reported_not_thrown()
    {
        Assert.Contains("missing", Assert.Single(MaterialCsv.Parse("code,name,type\nA,A,raw").Problems), StringComparison.Ordinal);
        Assert.Contains("Unknown column 'clas'", MaterialCsv.Parse("code,name,type,base_unit,clas\n").Problems[0], StringComparison.Ordinal);
        Assert.Contains("twice", MaterialCsv.Parse("code,name,type,base_unit,unit\n").Problems[0], StringComparison.Ordinal);
        Assert.Equal(["The file is empty."], MaterialCsv.Parse("").Problems);
        Assert.Equal(["The file has a header but no rows."], MaterialCsv.Parse("code,name,type,base_unit\n,,,\n").Problems);

        var row = Assert.Single(MaterialCsv.Parse("code,name,type,base_unit,lot_tracked,shelf_life_days\nA,A,raw,kg,maybe,2.5").Rows);
        Assert.Equal(2, row.Problems.Count);
    }

    [Fact]
    public void Too_many_rows_are_refused_and_the_template_lists_every_column()
    {
        var big = "code,name,type,base_unit\n" + string.Concat(Enumerable.Range(0, MaterialCsv.MaxRows + 1).Select(i => $"M{i},M,raw,kg\n"));

        Assert.Contains("at most", Assert.Single(MaterialCsv.Parse(big).Problems), StringComparison.Ordinal);
        Assert.Equal(MaterialCsvFormat.Header, MaterialCsv.Template);
    }
}
