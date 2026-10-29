using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Trae;

/// <summary>Bounded projection of the observed Alauda format. Unknown log content is skipped, never retained.</summary>
internal static class TraeAlauda
{
    internal const int MaxCompressed = 32 * 1024 * 1024;
    internal const int MaxDecoded = 128 * 1024 * 1024;
    internal sealed record Record(ulong? Sid, ulong? Parent, ulong? Span, string? Name, long? Start, long? End,
        long? Time, string? Target, string? Message, string? Trace, bool Unsafe, Dictionary<string, string> Tags);

    public static List<Record> Decode(byte[] bytes) => Decode(bytes, out _);

    public static List<Record> Decode(byte[] bytes, out int decodedBytes)
    {
        if (bytes.Length < 172 || bytes.Length > MaxCompressed || !bytes.AsSpan(0, 4).SequenceEqual("AalG"u8)
            || !bytes.AsSpan(8, 4).SequenceEqual("Atab"u8)) throw new InvalidDataException("Unknown Trae log container.");
        var offset = BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(16, 8));
        if (offset != 152 || bytes.AsSpan(24, 128).IndexOfAnyExcept((byte)0) >= 0
            || !bytes.AsSpan(152, 4).SequenceEqual("AALG"u8)) throw new InvalidDataException("Unknown Trae log segment.");
        var versionLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(168, 4));
        if (versionLength > 1024 || bytes.Length < 172 + versionLength) throw new InvalidDataException("Invalid Trae log version.");
        using var input = new MemoryStream(bytes, (int)(172 + versionLength), bytes.Length - (int)(172 + versionLength), false);
        using var inflater = new DeflateStream(input, CompressionMode.Decompress);
        using var decoded = new MemoryStream();
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var count = inflater.Read(buffer);
            if (count == 0) break;
            if (decoded.Length + count > MaxDecoded) throw new InvalidDataException("Trae log exceeds decoded size bound.");
            decoded.Write(buffer, 0, count);
        }
        decodedBytes = (int)decoded.Length;
        var reader = new Reader(decoded.ToArray());
        var records = new List<Record>();
        while (!reader.End)
        {
            try
            {
                if (records.Count >= 500_000) throw new InvalidDataException("Too many Trae log records.");
                if (reader.Record() is { } record) records.Add(record);
            }
            catch (EndOfStreamException) { break; } // Active streams can have an incomplete last CBOR item.
        }
        return records;
    }

    private sealed class Reader(byte[] data)
    {
        private static readonly UTF8Encoding Utf8 = new(false, true);
        private int _position;
        public bool End => _position == data.Length;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count < 0 || count > data.Length - _position) throw new EndOfStreamException();
            var result = data.AsSpan(_position, count);
            _position += count;
            return result;
        }
        private (int Major, ulong Value) Header()
        {
            var b = Take(1)[0];
            var value = (b & 31) switch
            {
                < 24 => (ulong)(b & 31),
                24 => Take(1)[0],
                25 => BinaryPrimitives.ReadUInt16BigEndian(Take(2)),
                26 => BinaryPrimitives.ReadUInt32BigEndian(Take(4)),
                27 => BinaryPrimitives.ReadUInt64BigEndian(Take(8)),
                _ => throw new InvalidDataException("Indefinite CBOR is unsupported."),
            };
            return (b >> 5, value);
        }
        private int Length(ulong value) => value <= MaxDecoded ? (int)value : throw new InvalidDataException("CBOR length exceeds bound.");
        private void Skip(int depth = 0)
        {
            if (depth > 32) throw new InvalidDataException("CBOR nesting exceeds bound.");
            var (major, value) = Header();
            if (major is 2 or 3) Take(Length(value));
            else if (major is 4 or 5)
            {
                var count = Length(value);
                if (count > 500_000) throw new InvalidDataException("CBOR collection exceeds bound.");
                for (var i = 0; i < count; i++) { Skip(depth + 1); if (major == 5) Skip(depth + 1); }
            }
            else if (major == 6) Skip(depth + 1);
        }
        private string? Text(int maximum)
        {
            var before = _position;
            var (major, value) = Header();
            if (major is not 2 and not 3) { _position = before; Skip(); return null; }
            var bytes = Take(Length(value));
            return bytes.Length <= maximum ? Utf8.GetString(bytes) : null;
        }
        private ulong? Number()
        {
            var before = _position;
            var (major, value) = Header();
            if (major == 0) return value;
            _position = before; Skip(); return null;
        }
        private long? Time()
        {
            var before = _position;
            var (major, length) = Header();
            if (major != 4 || length != 9) { _position = before; Skip(); return null; }
            Span<long> parts = stackalloc long[9];
            for (var i = 0; i < parts.Length; i++)
            {
                var (kind, value) = Header();
                if (kind is not 0 and not 1 || value > int.MaxValue) throw new InvalidDataException("Invalid Trae timestamp.");
                parts[i] = kind == 0 ? (long)value : -1 - (long)value;
            }
            if (parts[0] is < 1970 or > 9999 || parts[1] < 1 || parts[1] > (DateTime.IsLeapYear((int)parts[0]) ? 366 : 365)
                || parts[2] is < 0 or > 23 || parts[3] is < 0 or > 59 || parts[4] is < 0 or > 59
                || parts[5] is < 0 or > 999_999_999 || Math.Abs(parts[6]) > 23 || Math.Abs(parts[7]) > 59 || Math.Abs(parts[8]) > 59)
                throw new InvalidDataException("Invalid Trae timestamp.");
            var utc = new DateTimeOffset((int)parts[0], 1, 1, (int)parts[2], (int)parts[3], (int)parts[4], TimeSpan.Zero)
                .AddDays(parts[1] - 1).ToUnixTimeMilliseconds();
            return utc - (parts[6] * 3600 + parts[7] * 60 + parts[8]) * 1000 + parts[5] / 1_000_000;
        }
        private void Map(Action<string?> read)
        {
            var before = _position;
            var (major, count) = Header();
            if (major != 5) { _position = before; Skip(); return; }
            if (count > 256) throw new InvalidDataException("Trae record map exceeds bound.");
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (ulong i = 0; i < count; i++)
            {
                var key = Text(128);
                if (key is not null && !keys.Add(key)) throw new InvalidDataException("Duplicate CBOR map key.");
                read(key);
            }
        }
        private Dictionary<string, string> Tags()
        {
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            var before = _position;
            var (major, count) = Header();
            if (major != 4) { _position = before; Skip(); return result; }
            if (count > 256) throw new InvalidDataException("Too many Trae span tags.");
            for (ulong i = 0; i < count; i++)
            {
                var (kind, length) = Header();
                if (kind != 4 || length != 2) throw new InvalidDataException("Invalid Trae span tag.");
                var key = Text(128);
                if (key is "turn_id" or "session_id" or "history_v2_id" or "event_name")
                {
                    if (Text(4096) is { } value && !result.TryAdd(key, value)) throw new InvalidDataException("Duplicate Trae span tag.");
                }
                else Skip();
            }
            return result;
        }
        public Record? Record()
        {
            ulong? sid = null, parent = null, span = null;
            long? start = null, end = null, time = null;
            string? name = null, target = null, message = null, trace = null, level = null;
            Dictionary<string, string> tags = [];
            Map(key =>
            {
                switch (key)
                {
                    case "sid": sid = Number(); break;
                    case "psid": parent = Number(); break;
                    case "span": span = Number(); break;
                    case "name": name = Text(256); break;
                    case "level": level = Text(16); break;
                    case "start": start = Time(); break;
                    case "end": end = Time(); break;
                    case "time": time = Time(); break;
                    case "tags": tags = Tags(); break;
                    case "marker": Map(field => { if (field == "target") target = Text(256); else Skip(); }); break;
                    case "fields": Map(field => { if (field == "message") message = Text(32768); else if (field == "trace_id") trace = Text(4096); else Skip(); }); break;
                    default: Skip(); break;
                }
            });
            // Retain only transport/timing messages. All other user content has already been skipped or discarded.
            var timing = message?.StartsWith("TimingCost: TimingCostOriginEvent { data: ", StringComparison.Ordinal) == true;
            var unsafeEvent = level == "ERROR" || (!timing && message is not null
                && Regex.IsMatch(message, @"\bretry\b|\bretried\b|\breconnect", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
            if (message is not null && message is not "DONE" and not "EOF"
                && !message.StartsWith("[aha_net] send: calling Fetch id=", StringComparison.Ordinal)
                && !timing) message = null;
            return sid is null && message is null && !unsafeEvent ? null : new Record(sid, parent, span, name, start, end, time, target, message, trace, unsafeEvent, tags);
        }
    }
}
