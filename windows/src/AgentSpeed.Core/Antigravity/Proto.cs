using System.Text;

namespace AgentSpeed.Core.Antigravity;

/// <summary>Bounded protobuf wire reader. Unknown fields are skipped; conversation text is never decoded.</summary>
internal readonly ref struct Proto(ReadOnlySpan<byte> data)
{
    public ReadOnlySpan<byte> Data { get; } = data;

    public ReadOnlySpan<byte> Bytes(int field)
    {
        var remaining = Data;
        while (Next(ref remaining, out var number, out var wire, out _, out var bytes))
        {
            if (number == field && wire == 2) return Data.Slice(Data.Length - remaining.Length - bytes.Length, bytes.Length);
        }
        return [];
    }

    public long Number(int field)
    {
        var remaining = Data;
        while (Next(ref remaining, out var number, out var wire, out var value, out _))
        {
            if (number == field && wire == 0) return value <= long.MaxValue ? (long)value : 0;
        }
        return 0;
    }

    public bool Has(int field)
    {
        var remaining = Data;
        while (Next(ref remaining, out var number, out _, out _, out _))
        {
            if (number == field) return true;
        }
        return false;
    }

    public string? Text(int field)
    {
        var bytes = Bytes(field);
        return bytes.IsEmpty || bytes.Length > 512 ? null : Encoding.UTF8.GetString(bytes);
    }

    public long? Milliseconds()
    {
        if (Data.IsEmpty) return null;
        var seconds = Number(1);
        var nanos = Number(2);
        if (seconds > 253_402_300_799 || nanos >= 1_000_000_000) throw new FormatException("Invalid protobuf time.");
        return seconds * 1000 + nanos / 1_000_000;
    }

    public List<long> Numbers(int field)
    {
        var result = new List<long>();
        var remaining = Data;
        while (Next(ref remaining, out var number, out var wire, out var value, out var bytes))
        {
            if (number != field) continue;
            if (wire == 0 && value <= long.MaxValue) result.Add((long)value);
            if (wire == 2)
            {
                while (!bytes.IsEmpty)
                {
                    value = Varint(ref bytes);
                    if (value <= long.MaxValue) result.Add((long)value);
                }
            }
        }
        return result;
    }

    private static bool Next(ref ReadOnlySpan<byte> data, out int field, out int wire, out ulong value, out ReadOnlySpan<byte> bytes)
    {
        field = wire = 0;
        value = 0;
        bytes = [];
        if (data.IsEmpty) return false;
        var tag = Varint(ref data);
        field = (int)(tag >> 3);
        wire = (int)(tag & 7);
        if (field <= 0 || tag >> 3 > 536_870_911) throw new FormatException("Invalid protobuf tag.");
        if (wire == 0) value = Varint(ref data);
        else
        {
            var length = wire switch
            {
                1 => 8UL,
                2 => Varint(ref data),
                5 => 4UL,
                _ => throw new FormatException("Unsupported protobuf wire type."),
            };
            if (length > (ulong)data.Length) throw new FormatException("Truncated protobuf field.");
            bytes = data[..(int)length];
            data = data[(int)length..];
        }
        return true;
    }

    private static ulong Varint(ref ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (var shift = 0; shift < 70; shift += 7)
        {
            if (data.IsEmpty) throw new FormatException("Truncated protobuf varint.");
            var b = data[0];
            data = data[1..];
            if (shift == 63 && b > 1) throw new FormatException("Overflowing protobuf varint.");
            value |= (ulong)(b & 127) << shift;
            if (b < 128) return value;
        }
        throw new FormatException("Overflowing protobuf varint.");
    }
}
