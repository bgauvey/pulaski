using System.Buffers.Binary;
using System.Text;

namespace Pulaski.Connectors.Sparkplug;

/// <summary>Sparkplug B metric data types (Sparkplug 3.0 specification), as far as Pulaski reads them.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name", Justification = "The names the Sparkplug specification gives them.")]
public enum SparkplugDataType : uint
{
    Unknown = 0,
    Int8 = 1,
    Int16 = 2,
    Int32 = 3,
    Int64 = 4,
    UInt8 = 5,
    UInt16 = 6,
    UInt32 = 7,
    UInt64 = 8,
    Float = 9,
    Double = 10,
    Boolean = 11,
    String = 12,
    DateTime = 13,
    Text = 14,
    Uuid = 15,
}

/// <summary>One metric of a Sparkplug B payload. Datasets, templates and properties aren't read.</summary>
public sealed record SparkplugMetric(string? Name, ulong? Alias, ulong? Timestamp, SparkplugDataType DataType, bool IsNull, object? Value)
{
    /// <summary>A metric with its value, its data type taken from the value.</summary>
    public static SparkplugMetric Of(string name, object? value, ulong? alias = null, ulong? timestamp = null) =>
        new(name, alias, timestamp, value switch
        {
            bool => SparkplugDataType.Boolean,
            int => SparkplugDataType.Int32,
            long => SparkplugDataType.Int64,
            uint => SparkplugDataType.UInt32,
            ulong => SparkplugDataType.UInt64,
            float => SparkplugDataType.Float,
            double => SparkplugDataType.Double,
            string => SparkplugDataType.String,
            _ => SparkplugDataType.Unknown,
        }, value is null, value);
}

/// <summary>A Sparkplug B payload: a timestamp, a sequence number and metrics.</summary>
public sealed record SparkplugPayload(ulong? Timestamp, ulong? Sequence, IReadOnlyList<SparkplugMetric> Metrics)
{
    /// <summary>Reads the protobuf encoding of a payload. Fields Pulaski doesn't use are skipped.</summary>
    /// <exception cref="FormatException">The bytes aren't a valid payload.</exception>
    public static SparkplugPayload Decode(ReadOnlySpan<byte> bytes)
    {
        var reader = new ProtoReader(bytes);
        ulong? timestamp = null;
        ulong? sequence = null;
        var metrics = new List<SparkplugMetric>();
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0:
                    timestamp = reader.Varint();
                    break;
                case 2 when wire == 2:
                    metrics.Add(DecodeMetric(reader.Bytes()));
                    break;
                case 3 when wire == 0:
                    sequence = reader.Varint();
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new SparkplugPayload(timestamp, sequence, metrics);
    }

    /// <summary>The protobuf encoding of the payload; for commands to edge nodes, and for tests and simulators.</summary>
    public byte[] Encode()
    {
        var writer = new ProtoWriter();
        if (Timestamp is { } timestamp)
        {
            writer.Varint(1, timestamp);
        }

        foreach (var metric in Metrics)
        {
            writer.Bytes(2, EncodeMetric(metric));
        }

        if (Sequence is { } sequence)
        {
            writer.Varint(3, sequence);
        }

        return writer.ToArray();
    }

    private static SparkplugMetric DecodeMetric(ReadOnlySpan<byte> bytes)
    {
        var reader = new ProtoReader(bytes);
        string? name = null;
        ulong? alias = null;
        ulong? timestamp = null;
        var type = SparkplugDataType.Unknown;
        var isNull = false;
        object? raw = null;
        while (reader.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2:
                    name = Encoding.UTF8.GetString(reader.Bytes());
                    break;
                case 2 when wire == 0:
                    alias = reader.Varint();
                    break;
                case 3 when wire == 0:
                    timestamp = reader.Varint();
                    break;
                case 4 when wire == 0:
                    type = (SparkplugDataType)reader.Varint();
                    break;
                case 7 when wire == 0:
                    isNull = reader.Varint() != 0;
                    break;
                case 10 when wire == 0:
                    raw = (uint)reader.Varint();
                    break;
                case 11 when wire == 0:
                    raw = reader.Varint();
                    break;
                case 12 when wire == 5:
                    raw = BinaryPrimitives.ReadSingleLittleEndian(reader.Fixed(4));
                    break;
                case 13 when wire == 1:
                    raw = BinaryPrimitives.ReadDoubleLittleEndian(reader.Fixed(8));
                    break;
                case 14 when wire == 0:
                    raw = reader.Varint() != 0;
                    break;
                case 15 when wire == 2:
                    raw = Encoding.UTF8.GetString(reader.Bytes());
                    break;
                default:
                    reader.Skip(wire);
                    break;
            }
        }

        return new SparkplugMetric(name, alias, timestamp, type, isNull || raw is null, isNull ? null : Typed(type, raw));
    }

    // Signed integers travel as two's complement in the unsigned fields.
    private static object? Typed(SparkplugDataType type, object? raw) => (type, raw) switch
    {
        (SparkplugDataType.Int8, uint v) => (int)(sbyte)v,
        (SparkplugDataType.Int16, uint v) => (int)(short)v,
        (SparkplugDataType.Int32, uint v) => (int)v,
        (SparkplugDataType.UInt8 or SparkplugDataType.UInt16 or SparkplugDataType.UInt32, uint v) => (long)v,
        (SparkplugDataType.Int64, ulong v) => (long)v,
        (SparkplugDataType.UInt64 or SparkplugDataType.DateTime, ulong v) => v,
        _ => raw,
    };

    private static byte[] EncodeMetric(SparkplugMetric metric)
    {
        var writer = new ProtoWriter();
        if (metric.Name is { } name)
        {
            writer.Bytes(1, Encoding.UTF8.GetBytes(name));
        }

        if (metric.Alias is { } alias)
        {
            writer.Varint(2, alias);
        }

        if (metric.Timestamp is { } timestamp)
        {
            writer.Varint(3, timestamp);
        }

        writer.Varint(4, (ulong)metric.DataType);
        switch (metric.Value)
        {
            case null:
                writer.Varint(7, 1);
                break;
            case bool b:
                writer.Varint(14, b ? 1UL : 0UL);
                break;
            case int i:
                writer.Varint(10, (uint)i);
                break;
            case uint u:
                writer.Varint(10, u);
                break;
            case long l:
                writer.Varint(11, (ulong)l);
                break;
            case ulong ul:
                writer.Varint(11, ul);
                break;
            case float f:
                writer.Fixed32(12, BitConverter.SingleToUInt32Bits(f));
                break;
            case double d:
                writer.Fixed64(13, BitConverter.DoubleToUInt64Bits(d));
                break;
            case string s:
                writer.Bytes(15, Encoding.UTF8.GetBytes(s));
                break;
            default:
                throw new ArgumentException($"A {metric.Value.GetType().Name} can't be sent as a Sparkplug metric.", nameof(metric));
        }

        return writer.ToArray();
    }

    /// <summary>A minimal protobuf reader: varints, fixed widths and length-delimited fields.</summary>
    private ref struct ProtoReader(ReadOnlySpan<byte> bytes)
    {
        private readonly ReadOnlySpan<byte> _bytes = bytes;
        private int _position;

        public bool Next(out int field, out int wire)
        {
            if (_position >= _bytes.Length)
            {
                (field, wire) = (0, 0);
                return false;
            }

            var key = Varint();
            (field, wire) = ((int)(key >> 3), (int)(key & 7));
            return true;
        }

        public ulong Varint()
        {
            ulong value = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                if (_position >= _bytes.Length)
                {
                    throw new FormatException("The payload ends inside a number.");
                }

                var b = _bytes[_position++];
                value |= (ulong)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                {
                    return value;
                }
            }

            throw new FormatException("A number in the payload is too long.");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var length = Varint();
            return Fixed(length > int.MaxValue ? throw new FormatException("A field is too long.") : (int)length);
        }

        public ReadOnlySpan<byte> Fixed(int length)
        {
            if (length < 0 || _position + length > _bytes.Length)
            {
                throw new FormatException("The payload ends inside a field.");
            }

            var span = _bytes.Slice(_position, length);
            _position += length;
            return span;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0:
                    Varint();
                    break;
                case 1:
                    Fixed(8);
                    break;
                case 2:
                    Bytes();
                    break;
                case 5:
                    Fixed(4);
                    break;
                default:
                    throw new FormatException($"Unknown protobuf wire type {wire}.");
            }
        }
    }

    private sealed class ProtoWriter
    {
        private readonly List<byte> _bytes = [];

        public void Varint(int field, ulong value)
        {
            Key(field, 0);
            Raw(value);
        }

        public void Bytes(int field, byte[] value)
        {
            Key(field, 2);
            Raw((ulong)value.Length);
            _bytes.AddRange(value);
        }

        public void Fixed32(int field, uint value)
        {
            Key(field, 5);
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, value);
            _bytes.AddRange(b);
        }

        public void Fixed64(int field, ulong value)
        {
            Key(field, 1);
            Span<byte> b = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(b, value);
            _bytes.AddRange(b);
        }

        public byte[] ToArray() => [.. _bytes];

        private void Key(int field, int wire) => Raw(((ulong)field << 3) | (uint)wire);

        private void Raw(ulong value)
        {
            while (value >= 0x80)
            {
                _bytes.Add((byte)(value | 0x80));
                value >>= 7;
            }

            _bytes.Add((byte)value);
        }
    }
}
