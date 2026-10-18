using Pulaski.Application.MachineData;

namespace Pulaski.Application.Tests.MachineData;

/// <summary>Reading a machine's definitions from CSV (D-041).</summary>
public class MachineProfileCsvTests
{
    [Fact]
    public void Columns_are_named_in_any_order_case_and_spelling()
    {
        var source = MachineProfileCsv.Parse("﻿Type,ID,Message,PackML,Group,Reason Code\r\nstate,3,Running,Execute,,run\r\nStop_Reason,101,\"Infeed jam, left\",,Mechanical,JAM\r\n");

        Assert.Empty(source.Problems);
        Assert.Equal(2, source.Rows.Count);
        var state = source.Rows[0];
        Assert.Equal((2, "state", 3, "Running", (int?)6, "RUN"), (state.Line, state.Kind, state.RawValue, state.Name, state.PackMlState, state.Reason));
        var stop = source.Rows[1];
        Assert.Equal(("stop_reason", 101, "Infeed jam, left", "Mechanical", "JAM", (int?)null), (stop.Kind, stop.RawValue, stop.Name, stop.Category, stop.Reason, stop.PackMlState));
        Assert.Empty(stop.Problems);
    }

    [Fact]
    public void Kind_value_and_name_are_required_and_unknown_columns_are_named()
    {
        var source = MachineProfileCsv.Parse("kind,value,colour\nstate,1,red\n");

        Assert.Empty(source.Rows);
        Assert.Contains(source.Problems, p => p.Contains("'colour'", StringComparison.Ordinal));
        Assert.Contains(source.Problems, p => p.Contains("'name'", StringComparison.Ordinal));
    }

    [Fact]
    public void A_row_says_what_it_could_not_read()
    {
        var source = MachineProfileCsv.Parse("kind,value,name,packml_state\nstate,x,Odd,\nfault,7,Seven,\nstate,9,Nine,Running\nalarm,12,Low air,\nstop_reason,5,Jam,Mechanical\n");

        Assert.Contains(source.Rows[0].Problems, p => p.StartsWith("value:", StringComparison.Ordinal));
        Assert.Contains(source.Rows[0].Problems, p => p.Contains("needs the PackML state", StringComparison.Ordinal));
        Assert.Contains(source.Rows[1].Problems, p => p.StartsWith("kind:", StringComparison.Ordinal));
        Assert.Contains(source.Rows[2].Problems, p => p.Contains("'Running' isn't a PackML state", StringComparison.Ordinal));
        Assert.Empty(source.Rows[3].Problems);
        Assert.Contains(source.Rows[4].Problems, p => p.Contains("only a state row has one", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_file_or_a_header_alone_has_nothing_to_import()
    {
        Assert.Equal(["The file is empty."], MachineProfileCsv.Parse("").Problems);
        Assert.Equal(["The file has a header but no rows."], MachineProfileCsv.Parse("kind,value,name\n\n").Problems);
    }
}
