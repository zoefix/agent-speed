using System.Buffers.Binary;
using System.Text;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The app's time zone as <c>start.zone</c> and <c>zone</c> send it (REMOTE 5.3, 8.1): an IANA id and TZif data. Windows has
/// no TZif files, so the data is generated from the zone's own rules (<see cref="TimeZoneInfo"/>): every offset change
/// from 1970 through 2100, the years the relay labels days in, so the host labels days exactly as the app does.
/// </summary>
public sealed record RemoteZone
{
    private const int FirstYear = 1970;
    private const int LastYear = 2100;

    private static readonly Lock CacheLock = new();
    private static (TimeZoneInfo Zone, RemoteZone Value)? _cache;

    private RemoteZone(string id, byte[] tzif)
    {
        Id = id;
        Tzif = tzif;
    }

    public string Id { get; }

    public byte[] Tzif { get; }

    public static RemoteZone? Create(string id, byte[] tzif)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(tzif);
        return IsTzif(tzif) && IsZoneId(id) ? new RemoteZone(id, tzif) : null;
    }

    /// <summary>The zone of <paramref name="zone"/> (the local zone when null), never null: an unnamed zone is sent as <c>UTC</c>'s id with its own data.</summary>
    public static RemoteZone Current(TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        lock (CacheLock)
        {
            if (_cache is { } cached && cached.Zone.Id == zone.Id && cached.Zone.HasSameRules(zone))
            {
                return cached.Value;
            }
        }

        var data = Generate(zone);
        var value = Create(IanaId(zone), data) ?? new RemoteZone("UTC", data);
        lock (CacheLock)
        {
            _cache = (zone, value);
        }

        return value;
    }

    /// <summary>The IANA id of <paramref name="zone"/>: its own id when it is one, else the Windows id converted, else a fixed-offset or UTC id.</summary>
    public static string IanaId(TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        if (zone.HasIanaId && IsZoneId(zone.Id))
        {
            return zone.Id;
        }

        if (TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) && IsZoneId(iana))
        {
            return iana;
        }

        if (zone.GetAdjustmentRules().Length == 0 && zone.BaseUtcOffset.Ticks % TimeSpan.TicksPerHour == 0)
        {
            var hours = -(int)(zone.BaseUtcOffset.Ticks / TimeSpan.TicksPerHour);
            return hours == 0 ? "Etc/UTC" : $"Etc/GMT{(hours > 0 ? "+" : string.Empty)}{hours}";
        }

        return "UTC";
    }

    internal static bool IsTzif(ReadOnlySpan<byte> data) => data.Length >= 44 && data[..4].SequenceEqual("TZif"u8);

    private static bool IsZoneId(string id) =>
        id.Length is > 0 and <= 64 && id.All(c => RemoteHostValidation.IsNameChar(c) || c is '/' or '+') && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>A version 2 TZif file (RFC 8536): a minimal version 1 block, then every transition with 64-bit times and an empty footer.</summary>
    internal static byte[] Generate(TimeZoneInfo zone)
    {
        var (transitions, types) = Transitions(zone);
        using var stream = new MemoryStream();
        WriteHeader(stream, '2', timeCount: 0, typeCount: 1, charCount: 4);
        WriteType(stream, types[0]);
        stream.Write("UTC\0"u8);

        var names = new StringBuilder();
        var nameIndex = new List<int>();
        foreach (var type in types)
        {
            nameIndex.Add(names.Length);
            names.Append(Abbreviation(type.Offset)).Append('\0');
        }

        WriteHeader(stream, '2', transitions.Count, types.Count, names.Length);
        Span<byte> buffer = stackalloc byte[8];
        foreach (var (at, _) in transitions)
        {
            BinaryPrimitives.WriteInt64BigEndian(buffer, at);
            stream.Write(buffer);
        }

        foreach (var (_, type) in transitions)
        {
            stream.WriteByte((byte)type);
        }

        for (var i = 0; i < types.Count; i++)
        {
            WriteType(stream, types[i], nameIndex[i]);
        }

        stream.Write(Encoding.ASCII.GetBytes(names.ToString()));
        stream.Write("\n\n"u8);
        return stream.ToArray();
    }

    /// <summary>Every change of the UTC offset (or of daylight time) between 1970 and the end of 2100, found day by day and refined to the second.</summary>
    private static (List<(long At, int Type)> Transitions, List<(int Offset, bool Dst)> Types) Transitions(TimeZoneInfo zone)
    {
        var start = new DateTimeOffset(FirstYear, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var end = new DateTimeOffset(LastYear + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var types = new List<(int Offset, bool Dst)>();
        var transitions = new List<(long At, int Type)>();
        var previous = TypeAt(zone, start);
        types.Add(previous);
        for (var t = start + 86_400; t <= end; t += 86_400)
        {
            var current = TypeAt(zone, t);
            if (current == previous)
            {
                continue;
            }

            long low = t - 86_400, high = t;
            while (high - low > 1)
            {
                var middle = low + ((high - low) / 2);
                if (TypeAt(zone, middle) == previous)
                {
                    low = middle;
                }
                else
                {
                    high = middle;
                }
            }

            var changed = TypeAt(zone, high);
            var index = types.IndexOf(changed);
            if (index < 0)
            {
                index = types.Count;
                types.Add(changed);
            }

            transitions.Add((high, index));
            previous = changed;
            if (changed != current)
            {
                t = high;
            }
        }

        return (transitions, types);
    }

    private static (int Offset, bool Dst) TypeAt(TimeZoneInfo zone, long unixSeconds)
    {
        var instant = DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        return ((int)zone.GetUtcOffset(instant).TotalSeconds, zone.IsDaylightSavingTime(instant));
    }

    private static string Abbreviation(int offsetSeconds)
    {
        var sign = offsetSeconds < 0 ? '-' : '+';
        var magnitude = Math.Abs(offsetSeconds);
        var hours = magnitude / 3600;
        var minutes = magnitude % 3600 / 60;
        return minutes == 0 ? $"{sign}{hours:00}" : $"{sign}{hours:00}{minutes:00}";
    }

    private static void WriteHeader(Stream stream, char version, int timeCount, int typeCount, int charCount)
    {
        stream.Write("TZif"u8);
        stream.WriteByte((byte)version);
        stream.Write(new byte[15]);
        Span<byte> counts = stackalloc byte[24];
        int[] values = [0, 0, 0, timeCount, typeCount, charCount];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteInt32BigEndian(counts[(i * 4)..], values[i]);
        }

        stream.Write(counts);
    }

    private static void WriteType(Stream stream, (int Offset, bool Dst) type, int nameIndex = 0)
    {
        Span<byte> buffer = stackalloc byte[6];
        BinaryPrimitives.WriteInt32BigEndian(buffer, type.Offset);
        buffer[4] = type.Dst ? (byte)1 : (byte)0;
        buffer[5] = (byte)nameIndex;
        stream.Write(buffer);
    }
}
