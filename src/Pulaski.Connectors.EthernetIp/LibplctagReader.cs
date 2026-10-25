using libplctag;

namespace Pulaski.Connectors.EthernetIp;

/// <summary>Reads CIP tags through libplctag (#24), keeping one tag handle per address; libplctag keeps the session to the PLC.</summary>
internal sealed class LibplctagReader(EthernetIpOptions options) : ICipTagReader
{
    private readonly Dictionary<string, Tag> _tags = [];

    public async Task<object> ReadAsync(EthernetIpAddress address, CancellationToken cancellationToken)
    {
        if (!_tags.TryGetValue(address.Address, out var tag))
        {
            tag = new Tag
            {
                Name = address.TagName,
                Gateway = options.Gateway,
                Path = options.Path,
                PlcType = PlcTypeOf(options.Plc),
                Protocol = libplctag.Protocol.ab_eip,
                ElementSize = address.ElementSize,
                ElementCount = 1,
                Timeout = options.Timeout,
            };
            _tags[address.Address] = tag;
        }

        if (!tag.IsInitialized)
        {
            await tag.InitializeAsync(cancellationToken);
        }

        await tag.ReadAsync(cancellationToken);
        return address.Type switch
        {
            "bool" => tag.GetBit(0),
            "sint" => tag.GetInt8(0),
            "usint" => tag.GetUInt8(0),
            "int" => tag.GetInt16(0),
            "uint" => tag.GetUInt16(0),
            "dint" => tag.GetInt32(0),
            "udint" => tag.GetUInt32(0),
            "lint" => tag.GetInt64(0),
            "ulint" => tag.GetUInt64(0),
            "real" => tag.GetFloat32(0),
            _ => (object)tag.GetFloat64(0),
        };
    }

    public ValueTask ForgetAsync(string address)
    {
        if (_tags.Remove(address, out var tag))
        {
            tag.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        foreach (var tag in _tags.Values)
        {
            tag.Dispose();
        }

        _tags.Clear();
        return ValueTask.CompletedTask;
    }

    private static PlcType PlcTypeOf(string plc) => plc switch
    {
        "micro800" => PlcType.Micro800,
        "omron-njnx" => PlcType.Omron,
        "plc5" => PlcType.Plc5,
        "slc500" => PlcType.Slc500,
        "logixpccc" => PlcType.LogixPccc,
        _ => PlcType.ControlLogix,   // CompactLogix speaks the same
    };
}
