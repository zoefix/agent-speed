using System.Globalization;
using System.Text.Json;

namespace AgentSpeed.Core.Quota.Vendors;

/// <summary>The value rules shared by the phase 2 readers (QUOTAS 9), as <c>spec/tools/quota_vendors.py</c> applies them.</summary>
internal static class VendorValue
{
    public const int DayMinutes = 1440;
    public const int WeekMinutes = 10080;
    public const int MonthMinutes = 43200;

    /// <summary>The member of an object that is present and not null.</summary>
    public static JsonElement? Get(JsonElement? value, string name) =>
        value is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var member) && member.ValueKind != JsonValueKind.Null
            ? member
            : null;

    /// <summary>The value when it is an object.</summary>
    public static JsonElement? Object(JsonElement? value) => value is { ValueKind: JsonValueKind.Object } obj ? obj : null;

    public static JsonElement? Object(JsonElement? value, string name) => Object(Get(value, name));

    /// <summary>The items of an array, or none.</summary>
    public static IEnumerable<JsonElement> Items(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Array } array ? array.EnumerateArray() : [];

    public static string? String(JsonElement? value) => value is { ValueKind: JsonValueKind.String } text ? text.GetString() : null;

    /// <summary>A non-empty string.</summary>
    public static string? Name(JsonElement? value) => String(value) is { Length: > 0 } text ? text : null;

    public static bool IsTrue(JsonElement? value) => value is { ValueKind: JsonValueKind.True };

    /// <summary>A finite number, never a boolean or a string.</summary>
    public static double? StrictNumber(JsonElement? value) =>
        value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out var d) && double.IsFinite(d) ? d : null;

    /// <summary>A finite number, or a string that reads as one (<c>"150"</c>, <c>" 2.5 "</c>).</summary>
    public static double? Number(JsonElement? value)
    {
        if (StrictNumber(value) is { } number)
        {
            return number;
        }

        return String(value)?.Trim() is { Length: > 0 } text
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed
            : null;
    }

    /// <summary>ISO 8601 text, epoch seconds, or epoch milliseconds (above 1e11), as epoch milliseconds.</summary>
    public static long? TimeMs(JsonElement? value)
    {
        if (StrictNumber(value) is { } number)
        {
            return EpochMs(number);
        }

        if (String(value)?.Trim() is not { Length: > 0 } text)
        {
            return null;
        }

        if (text.All(char.IsAsciiDigit))
        {
            return double.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var digits) ? EpochMs(digits) : null;
        }

        return IsoMs(text);
    }

    public static long EpochMs(double value) => Truncated(value > 1e11 ? value : value * 1000);

    /// <summary>
    /// Python's <c>fromisoformat</c> as the reference uses it: <c>YYYY-MM-DD</c>, optionally <c>T</c> or a space and
    /// <c>HH:MM[:SS[.fraction]]</c>, then <c>Z</c> or an offset (none is UTC).
    /// </summary>
    public static long? IsoMs(string text)
    {
        var s = text.AsSpan();
        if (s.Length < 10 || !Digits(s, 0, 4, out var year) || s[4] != '-' || !Digits(s, 5, 2, out var month) || s[7] != '-'
            || !Digits(s, 8, 2, out var day))
        {
            return null;
        }

        int hour = 0, minute = 0, second = 0;
        long fractionTicks = 0;
        var offsetMinutes = 0;
        var i = 10;
        if (i < s.Length)
        {
            if ((s[i] != 'T' && s[i] != 't' && s[i] != ' ') || !Digits(s, i + 1, 2, out hour) || i + 3 >= s.Length || s[i + 3] != ':'
                || !Digits(s, i + 4, 2, out minute))
            {
                return null;
            }

            i += 6;
            if (i < s.Length && s[i] == ':')
            {
                if (!Digits(s, i + 1, 2, out second))
                {
                    return null;
                }

                i += 3;
                if (i < s.Length && (s[i] == '.' || s[i] == ','))
                {
                    var start = ++i;
                    long scale = TimeSpan.TicksPerSecond;
                    while (i < s.Length && char.IsAsciiDigit(s[i]))
                    {
                        scale /= 10;
                        fractionTicks += (s[i] - '0') * scale;
                        i++;
                    }

                    if (i == start)
                    {
                        return null;
                    }
                }
            }

            if (i < s.Length)
            {
                if (s[i] is 'Z' or 'z' && i == s.Length - 1)
                {
                    i++;
                }
                else if (s[i] is '+' or '-' && Offset(s[(i + 1)..], out var minutes))
                {
                    offsetMinutes = s[i] == '-' ? -minutes : minutes;
                    i = s.Length;
                }
                else
                {
                    return null;
                }
            }
        }

        if (i != s.Length || month is < 1 or > 12 || hour > 23 || minute > 59 || second > 59 || year < 1
            || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        var local = new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified).AddTicks(fractionTicks);
        var utcTicks = local.Ticks - offsetMinutes * TimeSpan.TicksPerMinute - DateTime.UnixEpoch.Ticks;
        return utcTicks / TimeSpan.TicksPerMillisecond;
    }

    /// <summary>Python's <c>int()</c> of a finite double, saturated.</summary>
    public static long Truncated(double value) =>
        double.IsNaN(value) ? 0 : value >= long.MaxValue ? long.MaxValue : value <= long.MinValue ? long.MinValue : (long)value;

    /// <summary><c>str(value or "")</c> for the texts a reader matches words in.</summary>
    public static string Text(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => text.GetString() ?? string.Empty,
        { ValueKind: JsonValueKind.Number } number => number.GetRawText(),
        { ValueKind: JsonValueKind.True } => "True",
        _ => string.Empty,
    };

    /// <summary>290–310 minutes is the 5-hour window, 10000–10160 the weekly one, anything else its own length.</summary>
    public static QuotaWindowKind KindFor(int minutes) => minutes switch
    {
        >= 290 and <= 310 => QuotaWindowKind.FiveHour,
        >= 10000 and <= 10160 => QuotaWindowKind.SevenDay,
        _ => QuotaWindowKind.Other,
    };

    /// <summary>The first member that is present and not null.</summary>
    public static JsonElement? Pick(JsonElement? value, params ReadOnlySpan<string> names)
    {
        foreach (var name in names)
        {
            if (Get(value, name) is { } member)
            {
                return member;
            }
        }

        return null;
    }

    /// <summary>A finite double as a window length, saturated to <see cref="int"/>.</summary>
    public static int Minutes(double value) => (int)Math.Clamp(Truncated(value), int.MinValue, int.MaxValue);

    /// <summary>Parses a JSON body; null when it is not a JSON object.</summary>
    public static JsonDocument? ParseObject(ReadOnlyMemory<byte> body)
    {
        try
        {
            var document = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 64 });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool Digits(ReadOnlySpan<char> s, int start, int count, out int value)
    {
        value = 0;
        if (start < 0 || start + count > s.Length)
        {
            return false;
        }

        for (var i = start; i < start + count; i++)
        {
            if (!char.IsAsciiDigit(s[i]))
            {
                return false;
            }

            value = value * 10 + (s[i] - '0');
        }

        return true;
    }

    /// <summary><c>HH</c>, <c>HHMM</c> or <c>HH:MM</c> (an extra <c>:00</c> seconds part allowed).</summary>
    private static bool Offset(ReadOnlySpan<char> s, out int minutes)
    {
        minutes = 0;
        var mins = 0;
        var valid = Digits(s, 0, 2, out var hours) && s.Length switch
        {
            2 => true,
            4 => Digits(s, 2, 2, out mins),
            5 => s[2] == ':' && Digits(s, 3, 2, out mins),
            8 => s[2] == ':' && Digits(s, 3, 2, out mins) && s[5..].SequenceEqual(":00"),
            _ => false,
        };
        if (!valid || hours > 23 || mins > 59)
        {
            return false;
        }

        minutes = hours * 60 + mins;
        return true;
    }
}
