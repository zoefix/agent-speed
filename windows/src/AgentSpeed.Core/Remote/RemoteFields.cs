using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Remote;

public enum RemoteLineErrorKind
{
    NotJson,
    NotObject,
    Oversized,
    Missing,
    WrongType,
    TooLong,
    BadCharacter,
    OutOfRange,
    BadTime,
    TooMany,
}

/// <summary>Why a relay line is invalid (REMOTE 5.6). Only the field name is kept, never the value.</summary>
public sealed class RemoteLineException(RemoteLineErrorKind kind, string field = "") : Exception($"{kind} {field}")
{
    public RemoteLineErrorKind Kind { get; } = kind;

    public string Field { get; } = field;
}

/// <summary>The character rules of REMOTE 5.6.</summary>
internal enum RemoteText
{
    /// <summary>Ids, keys, projects, paths and tool names: no U+0000–U+001F or U+007F.</summary>
    Identifier,

    /// <summary>Models, providers and other displayed text: also no C1 controls, line and paragraph separators or bidi controls.</summary>
    Model,
}

/// <summary>
/// Typed, validated access to one JSON object of a relay line. Every accessor throws <see cref="RemoteLineException"/> for a
/// missing required member, a wrong type or a violated bound. Absent and <c>null</c> both mean null for optional members.
/// </summary>
internal readonly struct RemoteFields
{
    /// <summary>±2^53: larger integers make a line invalid (REMOTE 5.6).</summary>
    public const long IntegerLimit = 1L << 53;

    /// <summary>2000-01-01T00:00:00Z … 2100-12-31T23:59:59.999Z.</summary>
    public const long EarliestMs = 946_684_800_000;
    public const long LatestMs = 4_133_980_799_999;

    private readonly JsonElement _object;
    private readonly string _path;

    public RemoteFields(JsonElement element, string path = "")
    {
        _object = element;
        _path = path;
    }

    /// <summary>A member object (or array item) at <paramref name="path"/>.</summary>
    public static RemoteFields Of(JsonElement? value, string path) => value is not { } element
        ? throw new RemoteLineException(RemoteLineErrorKind.Missing, path)
        : element.ValueKind == JsonValueKind.Object
            ? new RemoteFields(element, path + ".")
            : throw new RemoteLineException(RemoteLineErrorKind.WrongType, path);

    public static bool AllowsText(string text, RemoteText rule)
    {
        if (rule == RemoteText.Identifier)
        {
            foreach (var c in text)
            {
                if (c < 0x20 || c == 0x7F)
                {
                    return false;
                }
            }

            return true;
        }

        foreach (var c in text)
        {
            if (c < 0x20 || c is >= (char)0x7F and <= (char)0x9F || c is (char)0x2028 or (char)0x2029 or (char)0x061C or (char)0x200E or (char)0x200F
                || c is >= (char)0x202A and <= (char)0x202E || c is >= (char)0x2066 and <= (char)0x2069)
            {
                return false;
            }
        }

        return true;
    }

    private string Name(string key) => _path + key;

    private JsonElement Required(string key) =>
        Find(_object, key) ?? throw new RemoteLineException(RemoteLineErrorKind.Missing, Name(key));

    private JsonElement? Optional(string key) => Find(_object, key) is { ValueKind: not JsonValueKind.Null } value ? value : null;

    /// <summary>The last member named <paramref name="key"/>, as the line's raw text is taken (<see cref="RelayLineDecoder.RawMember"/>).</summary>
    public static JsonElement? Find(JsonElement element, string key)
    {
        JsonElement? found = null;
        foreach (var member in element.EnumerateObject())
        {
            if (member.NameEquals(key))
            {
                found = member.Value;
            }
        }

        return found;
    }

    // Strings

    public string String(string key, int maxBytes, RemoteText rule = RemoteText.Identifier) => Checked(Required(key), Name(key), maxBytes, rule);

    public string? OptionalString(string key, int maxBytes, RemoteText rule = RemoteText.Identifier) =>
        Optional(key) is { } value ? Checked(value, Name(key), maxBytes, rule) : null;

    /// <summary>A model or provider: <c>null</c>, absent or empty is null.</summary>
    public string? ModelString(string key, int maxBytes) =>
        OptionalString(key, maxBytes, RemoteText.Model) is { Length: > 0 } text ? text : null;

    public static string Checked(JsonElement value, string name, int maxBytes, RemoteText rule)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new RemoteLineException(RemoteLineErrorKind.WrongType, name);
        }

        var text = value.GetString() ?? string.Empty;
        if (Encoding.UTF8.GetByteCount(text) > maxBytes)
        {
            throw new RemoteLineException(RemoteLineErrorKind.TooLong, name);
        }

        return AllowsText(text, rule) ? text : throw new RemoteLineException(RemoteLineErrorKind.BadCharacter, name);
    }

    public SourceKind Source(string key)
    {
        var value = Required(key);
        if (value.ValueKind != JsonValueKind.String)
        {
            throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key));
        }

        return History.SourceNames.Parse(value.GetString()) ?? throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key));
    }

    /// <summary>A calendar day <c>YYYY-MM-DD</c> from 2000-01-01 to 2100-12-31 (REMOTE 8.3 <i>Bounds</i>).</summary>
    public string Day(string key)
    {
        var text = String(key, 10);
        return IsDay(text) ? text : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key));
    }

    public static bool IsDay(string text)
    {
        if (text.Length != 10)
        {
            return false;
        }

        for (var i = 0; i < 10; i++)
        {
            if (i is 4 or 7 ? text[i] != '-' : !char.IsAsciiDigit(text[i]))
            {
                return false;
            }
        }

        var year = int.Parse(text.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture);
        var month = int.Parse(text.AsSpan(5, 2), System.Globalization.CultureInfo.InvariantCulture);
        var day = int.Parse(text.AsSpan(8, 2), System.Globalization.CultureInfo.InvariantCulture);
        return year is >= 2000 and <= 2100 && month is >= 1 and <= 12 && day >= 1 && day <= DateTime.DaysInMonth(year, month);
    }

    /// <summary><paramref name="count"/> lowercase hex digits.</summary>
    public string Hex(string key, int count)
    {
        var text = String(key, count);
        return IsLowerHex(text, count) ? text : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key));
    }

    public static bool IsLowerHex(string text, int count) => text.Length == count && text.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    // Scalars

    public bool Bool(string key) => Required(key) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key)),
    };

    public bool? OptionalBool(string key) => Optional(key) switch
    {
        null => null,
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        _ => throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key)),
    };

    public long Integer(string key) => Integer(Required(key), Name(key));

    public long? OptionalInteger(string key) => Optional(key) is { } value ? Integer(value, Name(key)) : null;

    /// <summary>An integer literal (no fraction or exponent) within ±2^53.</summary>
    public static long Integer(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || value.GetRawText().AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
        {
            throw new RemoteLineException(RemoteLineErrorKind.WrongType, name);
        }

        return value.TryGetInt64(out var number) && number is >= -IntegerLimit and <= IntegerLimit
            ? number
            : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, name);
    }

    public int Count(string key, long limit = IntegerLimit)
    {
        var value = Integer(key);
        return value >= 0 && value <= limit && value <= int.MaxValue
            ? (int)value
            : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key));
    }

    /// <summary>A non-negative integer within 2^53.</summary>
    public long Total(string key)
    {
        var value = Integer(key);
        return value >= 0 ? value : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key));
    }

    public long? OptionalTotal(string key) => OptionalInteger(key) switch
    {
        null => null,
        >= 0 and var value => value,
        _ => throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key)),
    };

    /// <summary>A Unix-millisecond time within the years 2000–2100.</summary>
    public long Time(string key) => Time(Integer(key), Name(key));

    public long? OptionalTime(string key) => OptionalInteger(key) is { } value ? Time(value, Name(key)) : null;

    public static long Time(long value, string name) =>
        IsTime(value) ? value : throw new RemoteLineException(RemoteLineErrorKind.BadTime, name);

    public static bool IsTime(long value) => value is >= EarliestMs and <= LatestMs;

    /// <summary>A token value (METRICS 3.2): any number, truncated and clamped to 0 … 10^12.</summary>
    public long Tokens(string key)
    {
        var value = Required(key);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key));
        }

        if (value.TryGetInt64(out var integer))
        {
            return TokenCounts.Clamp(integer);
        }

        if (value.TryGetDouble(out var real) && double.IsFinite(real))
        {
            return real <= 0 ? 0 : real >= TokenCounts.MaxValue ? TokenCounts.MaxValue : (long)real;
        }

        return value.GetRawText().StartsWith('-') ? 0 : TokenCounts.MaxValue;
    }

    /// <summary>A finite number.</summary>
    public double Number(string key)
    {
        var value = Required(key);
        if (value.ValueKind != JsonValueKind.Number)
        {
            throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key));
        }

        return value.TryGetDouble(out var number) && double.IsFinite(number)
            ? number
            : throw new RemoteLineException(RemoteLineErrorKind.OutOfRange, Name(key));
    }

    /// <summary>A fraction 0–1 (progress); finite values outside are clamped.</summary>
    public double Fraction(string key) => Math.Clamp(Number(key), 0, 1);

    // Containers

    public RemoteFields Object(string key) => Of(Required(key), Name(key));

    /// <summary>The exact JSON text of a member as it appears on the line.</summary>
    public string RawText(string key) => Required(key).GetRawText();

    public JsonElement ObjectMembers(string key)
    {
        var value = Required(key);
        return value.ValueKind == JsonValueKind.Object ? value : throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key));
    }

    public JsonElement Array(string key, int limit)
    {
        var value = Required(key);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new RemoteLineException(RemoteLineErrorKind.WrongType, Name(key));
        }

        return value.GetArrayLength() <= limit ? value : throw new RemoteLineException(RemoteLineErrorKind.TooMany, Name(key));
    }
}
