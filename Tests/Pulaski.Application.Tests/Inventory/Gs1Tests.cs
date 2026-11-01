using Pulaski.Domain.Inventory;

namespace Pulaski.Application.Tests.Inventory;

/// <summary>GS1 identifiers and element strings (#75), against examples worked from the GS1 General Specifications.</summary>
public class Gs1Tests
{
    private static readonly DateOnly Today = new(2026, 11, 1);

    [Fact]
    public void Check_digits_gtins_and_ssccs_follow_the_mod_10_rule()
    {
        Assert.Equal(3, Gs1.CheckDigit("0950110153000"));
        Assert.Equal("09501101530003", Gs1.NormalizeGtin("9501101530003"));
        Assert.Equal("09501101530003", Gs1.NormalizeGtin(" 09501101530003 "));
        Assert.Null(Gs1.NormalizeGtin("9501101530004"));
        Assert.Null(Gs1.NormalizeGtin("95011015300"));

        var sscc = Gs1.Sscc("0614141", 1, 123456789);
        Assert.Equal(18, sscc.Length);
        Assert.Equal("106141411234567897", sscc);
        Assert.Throws<Pulaski.Domain.DomainRuleException>(() => Gs1.Sscc("0614141", 1, Gs1.MaxSerial("0614141") + 1));
    }

    [Fact]
    public void Element_strings_read_bracketed_or_as_scanners_send_them_with_separators_after_variable_fields()
    {
        IReadOnlyList<Gs1Element> elements = [new("01", "09501101530003"), new("10", "AB-12"), new("17", "271231"), new("3102", "001250")];
        var data = Gs1.Data(elements, Gs1.GroupSeparator.ToString());
        Assert.Equal("0109501101530003" + "10AB-12\u001D" + "17271231" + "3102001250", data);
        Assert.Equal("(01)09501101530003(10)AB-12(17)271231(3102)001250", Gs1.HumanReadable(elements));

        Assert.Equal(elements, Gs1.Parse("]d2" + data));
        Assert.Equal(elements, Gs1.Parse(Gs1.HumanReadable(elements)));
        Assert.True(Gs1.LooksLikeGs1("]C1" + data));
        Assert.True(Gs1.LooksLikeGs1("(00)106141411234567897"));
        Assert.False(Gs1.LooksLikeGs1("LOT-1234"));
        Assert.Null(Gs1.Parse("(01)09501101530004"));   // wrong check digit

        var read = Gs1.Read(elements, Today);
        Assert.Equal(("09501101530003", "AB-12", new DateOnly(2027, 12, 31), 12.50m, "kg"), (read.Gtin, read.Lot, read.Expiry, read.Quantity, read.QuantityUnit));
        Assert.Equal(new DateOnly(2027, 2, 28), Gs1.Read([new("17", "270200")], Today).Expiry);
    }

    [Fact]
    public void A_net_weight_takes_the_decimals_it_needs_in_six_digits()
    {
        Assert.Equal(new Gs1Element("3101", "000125"), Gs1.NetWeight(12.5m, pounds: false));
        Assert.Equal(new Gs1Element("3103", "012345"), Gs1.NetWeight(12.345m, pounds: false));
        Assert.Equal(new Gs1Element("3200", "000500"), Gs1.NetWeight(500m, pounds: true));
        Assert.Null(Gs1.NetWeight(1_000_000m, pounds: false));
    }

    [Fact]
    public void A_supplier_format_names_its_parts_and_the_whole_scan_must_match()
    {
        const string Pattern = @"(?<material>[A-Z0-9-]+)\|(?<lot>[A-Z0-9]+)\|(?<qty>\d+(\.\d+)?)\|(?<expiry>\d{6})";

        Assert.Empty(SupplierBarcodes.Problems(Pattern, "yyMMdd"));
        Assert.Contains(SupplierBarcodes.Problems(@"(?<colour>\w+)", null), p => p.Contains("isn't a part", StringComparison.Ordinal));
        Assert.Contains(SupplierBarcodes.Problems(@"(?<qty>\d+)", null), p => p.Contains("finds a lot or a material", StringComparison.Ordinal));
        Assert.Contains(SupplierBarcodes.Problems("(unclosed", null), p => p.Contains("regular expression", StringComparison.Ordinal));

        var read = SupplierBarcodes.TryRead("RESIN-7|B4471|25.5|271130", Pattern, "yyMMdd", "ACME")!;
        Assert.Equal(("ACME", "RESIN-7", "B4471", 25.5m, new DateOnly(2027, 11, 30)), (read.Source, read.MaterialCode, read.LotNumber, read.Quantity, read.Expiry));
        Assert.Null(SupplierBarcodes.TryRead("RESIN-7|B4471|25.5|271130|extra", Pattern, "yyMMdd", "ACME"));
    }
}
