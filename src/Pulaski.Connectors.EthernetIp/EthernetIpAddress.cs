using System.Globalization;

namespace Pulaski.Connectors.EthernetIp;

/// <summary>
/// An EtherNet/IP (CIP) tag to read (#24): its name as the controller knows it (<c>Program:Main.State</c>, <c>Counts[3]</c>, a bit as
/// <c>Status.5</c>) and the type to read it as, after <c>|</c>; DINT when left out. One element at a time.
/// </summary>
public sealed record EthernetIpAddress(string Address, string TagName, string Type)
{
    public static IReadOnlyList<string> Types { get; } = ["bool", "sint", "int", "dint", "lint", "usint", "uint", "udint", "ulint", "real", "lreal"];

    public static EthernetIpAddress Parse(string address)
    {
        var bar = address.LastIndexOf('|');
        var name = bar < 0 ? address : address[..bar];
        var type = bar < 0 ? "dint" : address[(bar + 1)..].ToLowerInvariant();
        if (name.Length == 0 || !Types.Contains(type))
        {
            throw new FormatException($"'{address}' isn't an EtherNet/IP address: a tag name, optionally with |{string.Join("|", Types)}.");
        }

        return new EthernetIpAddress(address, name, type);
    }

    /// <summary>How many bytes one element of the type takes.</summary>
    public int ElementSize => Type switch
    {
        "bool" or "sint" or "usint" => 1,
        "int" or "uint" => 2,
        "dint" or "udint" or "real" => 4,
        _ => 8,
    };

    /// <summary>The type's name as discovery reports it, such as <c>DINT</c>.</summary>
    public string DataType => Type.ToUpper(CultureInfo.InvariantCulture);
}
