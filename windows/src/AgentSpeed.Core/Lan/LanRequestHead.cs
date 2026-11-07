using System.Text;
using System.Text.RegularExpressions;

namespace AgentSpeed.Core.Lan;

/// <summary>The protocol a connection's first line picks (LAN 4.2).</summary>
public enum LanFirstLine
{
    /// <summary>No complete line yet.</summary>
    Incomplete,
    Http,
    Line,
    Close,
}

/// <summary>
/// A request head (LAN 4.2, 4.3): the first line and the header lines up to the empty line, within 8192 bytes. Header names
/// are case-insensitive and the first of a repeated header counts.
/// </summary>
public sealed partial record LanRequestHead(string Method, string Target, IReadOnlyDictionary<string, string> Headers, bool Malformed)
{
    public const int Limit = 8192;

    /// <summary>The path: the target up to <c>?</c>.</summary>
    public string Path => Target.Split('?', 2)[0];

    public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());

    /// <summary>Classifies the bytes received so far; <see cref="LanFirstLine.Incomplete"/> while no <c>\n</c> came within the limit.</summary>
    public static LanFirstLine Classify(ReadOnlySpan<byte> data, bool final = false)
    {
        var end = data.IndexOf((byte)'\n');
        if (end < 0)
        {
            return final || data.Length >= Limit ? LanFirstLine.Close : LanFirstLine.Incomplete;
        }

        if (end >= Limit)
        {
            return LanFirstLine.Close;
        }

        var line = data[..end];
        if (line.Length > 0 && line[^1] == '\r')
        {
            line = line[..^1];
        }

        string text;
        try
        {
            text = Strict.GetString(line);
        }
        catch (DecoderFallbackException)
        {
            return LanFirstLine.Close;
        }

        if (RequestLine().IsMatch(text))
        {
            return LanFirstLine.Http;
        }

        return text.StartsWith('{') ? LanFirstLine.Line : LanFirstLine.Close;
    }

    /// <summary>The length of the head including its empty line, or −1 while it is not complete within the limit.</summary>
    public static int HeadLength(ReadOnlySpan<byte> data) => Find(data, out var end, out var separator) ? end + separator : -1;

    /// <summary>The head at the start of <paramref name="data"/>, or null while it is incomplete or its first line is not a request line.</summary>
    public static LanRequestHead? Parse(ReadOnlySpan<byte> data)
    {
        if (!Find(data, out var end, out _))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(data[..end]);
        var lines = text.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).ToArray();
        var match = RequestLine().Match(lines[0]);
        if (!match.Success)
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                return new LanRequestHead(match.Groups[1].Value, match.Groups[2].Value, headers, Malformed: true);
            }

            headers.TryAdd(LanText.Trim(line[..colon]).ToLowerInvariant(), LanText.Trim(line[(colon + 1)..]));
        }

        return new LanRequestHead(match.Groups[1].Value, match.Groups[2].Value, headers, Malformed: false);
    }

    private static bool Find(ReadOnlySpan<byte> data, out int end, out int separator)
    {
        var crlf = data.IndexOf("\r\n\r\n"u8);
        var lf = data.IndexOf("\n\n"u8);
        (end, separator) = crlf >= 0 && (lf < 0 || lf >= crlf) ? (crlf, 4) : (lf, 2);
        return end >= 0 && end + separator <= Limit;
    }

    private static readonly UTF8Encoding Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex(@"^([A-Z]+) (\S+) HTTP/1\.[01]\z", RegexOptions.CultureInvariant)]
    private static partial Regex RequestLine();
}
