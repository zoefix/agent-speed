using System.Text;
using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Reporting;
using AgentSpeed.Core.Unpriced;

namespace AgentSpeed.Core.Remote;

/// <summary>
/// The price table as a <c>start.prices</c> / <c>prices.table</c> value: a JSON object validated by a strict parser and
/// written compactly (insignificant whitespace removed, every literal kept as written), so it fits one line.
/// </summary>
public sealed record RemotePriceTableJson
{
    private RemotePriceTableJson(string text)
    {
        Text = text;
    }

    public string Text { get; }

    /// <summary>Null when <paramref name="data"/> is not a JSON object or too large for a line.</summary>
    public static RemotePriceTableJson? From(ReadOnlySpan<byte> data)
    {
        if (data.Length > RelayLineEncoder.MaxLineBytes)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object ? new RemotePriceTableJson(Compact(data)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Compact(ReadOnlySpan<byte> data)
    {
        var output = new List<byte>(data.Length);
        var inString = false;
        var escaped = false;
        foreach (var b in data)
        {
            if (inString)
            {
                output.Add(b);
                if (escaped)
                {
                    escaped = false;
                }
                else if (b == '\\')
                {
                    escaped = true;
                }
                else if (b == '"')
                {
                    inString = false;
                }
            }
            else if (b == '"')
            {
                inString = true;
                output.Add(b);
            }
            else if (b is not (0x20 or 0x09 or 0x0A or 0x0D))
            {
                output.Add(b);
            }
        }

        return Encoding.UTF8.GetString([.. output]);
    }
}

/// <summary>The app → relay lines of REMOTE 5.3, each a compact JSON object ending in a newline (at most 4 MiB).</summary>
internal static class RelayLineEncoder
{
    public const int MaxLineBytes = 4 << 20;

    /// <param name="quota">REMOTE 11.1: what the relay reads for quotas; null leaves the member out.</param>
    public static byte[]? Start(long todayStartMs, IEnumerable<SourceKind> sources, RemoteZone zone, RemotePriceTableJson prices, RemoteQuotaConfig? quota = null)
    {
        var keys = string.Join(',', sources.Distinct().Order().Select(s => ReportJson.Quoted(SourceNames.Of(s))));
        var quotaMember = quota is null ? string.Empty : ",\"quota\":" + QuotaObject(quota);
        return Line($"{{\"t\":\"start\",\"protocol\":{RemoteProtocol.Version},\"todayStartMs\":{todayStartMs},"
            + $"\"sources\":[{keys}],\"zone\":{ZoneObject(zone)},\"prices\":{prices.Text}{quotaMember}}}");
    }

    /// <summary>REMOTE 11.1: sent whenever the quota config changes.</summary>
    public static byte[]? Quota(RemoteQuotaConfig config) => Line($"{{\"t\":\"quota\",\"config\":{QuotaObject(config)}}}");

    public static byte[]? Ping(long id) => Line($"{{\"t\":\"ping\",\"id\":{id}}}");

    public static byte[]? Today(long todayStartMs) => Line($"{{\"t\":\"today\",\"todayStartMs\":{todayStartMs}}}");

    public static byte[]? Prices(RemotePriceTableJson table) => Line($"{{\"t\":\"prices\",\"table\":{table.Text}}}");

    public static byte[]? Zone(RemoteZone zone) =>
        Line($"{{\"t\":\"zone\",\"id\":{ReportJson.Quoted(zone.Id)},\"tzif\":{ReportJson.Quoted(Convert.ToBase64String(zone.Tzif))}}}");

    /// <summary><paramref name="digests"/>: <c>"&lt;day&gt;/&lt;source&gt;"</c> → digest, in key order so a request is reproducible.</summary>
    public static byte[]? History(IReadOnlyDictionary<string, string> digests)
    {
        var members = string.Join(',', digests.OrderBy(d => d.Key, Utf8Order.Instance)
            .Select(d => ReportJson.Quoted(d.Key) + ":" + ReportJson.Quoted(d.Value)));
        return Line($"{{\"t\":\"history\",\"digests\":{{{members}}}}}");
    }

    public static byte[]? Watch(bool on) => Line($"{{\"t\":\"watch\",\"on\":{(on ? "true" : "false")}}}");

    /// <summary><c>{sources, network, grokLive, claudeBridge}</c>, the sources in source order.</summary>
    private static string QuotaObject(RemoteQuotaConfig config)
    {
        var keys = string.Join(',', config.Sources.Select(s => ReportJson.Quoted(SourceNames.Of(s))));
        return $"{{\"sources\":[{keys}],\"network\":{Literal(config.Network)},\"grokLive\":{Literal(config.GrokLive)},"
            + $"\"claudeBridge\":{Literal(config.ClaudeBridge)}}}";
    }

    private static string Literal(bool value) => value ? "true" : "false";

    private static string ZoneObject(RemoteZone zone) =>
        $"{{\"id\":{ReportJson.Quoted(zone.Id)},\"tzif\":{ReportJson.Quoted(Convert.ToBase64String(zone.Tzif))}}}";

    private static byte[]? Line(string text)
    {
        var data = Encoding.UTF8.GetBytes(text + "\n");
        return data.Length <= MaxLineBytes ? data : null;
    }
}
