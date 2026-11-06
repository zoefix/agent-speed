using System.Text;
using System.Text.Json;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The app → relay lines of REMOTE 5.3.</summary>
public sealed class RemoteEncoderTests
{
    private static readonly RemotePriceTableJson Prices =
        RemotePriceTableJson.From("""{ "schema": 3, "models": [ {"id": "a b", "price": 0.20000000000000001} ] }"""u8)
        ?? throw new InvalidOperationException("price table refused");

    [Fact]
    public void PriceTablesAreCompactWithLiteralsKept()
    {
        Assert.Equal("""{"schema":3,"models":[{"id":"a b","price":0.20000000000000001}]}""", Prices.Text);
        Assert.Null(RemotePriceTableJson.From("[1]"u8));
        Assert.Null(RemotePriceTableJson.From("{"u8));
        Assert.Null(RemotePriceTableJson.From(""u8));
        Assert.Null(RemotePriceTableJson.From("{} {}"u8));
        Assert.Equal("""{"k":"q\" \\ x"}""", RemotePriceTableJson.From("""{"k":"q\" \\ x"}"""u8)?.Text);
        Assert.Equal("{\"k\":\"a\\u000a b\\t\",\"n\":1e+2}", RemotePriceTableJson.From("{\r\n\t\"k\" : \"a\\u000a b\\t\" ,\n \"n\": 1e+2 }"u8)?.Text);

        var bundled = BundledPrices();
        var table = RemotePriceTableJson.From(bundled);
        Assert.NotNull(table);
        Assert.DoesNotContain("\n", table.Text, StringComparison.Ordinal);
        Assert.True(JsonElement.DeepEquals(Parse(Encoding.UTF8.GetString(bundled)), Parse(table.Text)));
    }

    [Fact]
    public void StartCarriesSourcesZoneAndPrices()
    {
        var zone = RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Asia/Taipei"));
        var start = Object(RelayLineEncoder.Start(1_780_000_000_000, [SourceKind.ClaudeCode, SourceKind.Codex, SourceKind.ClaudeCode], zone, Prices));
        Assert.Equal("start", start.GetProperty("t").GetString());
        Assert.Equal(1, start.GetProperty("protocol").GetInt64());
        Assert.Equal(1_780_000_000_000, start.GetProperty("todayStartMs").GetInt64());
        Assert.Equal(["codex", "claudeCode"], start.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal("Asia/Taipei", start.GetProperty("zone").GetProperty("id").GetString());
        var tzif = Convert.FromBase64String(start.GetProperty("zone").GetProperty("tzif").GetString()!);
        Assert.Equal("TZif"u8.ToArray(), tzif[..4]);
        Assert.Equal(zone.Tzif, tzif);
        Assert.True(JsonElement.DeepEquals(Parse(Prices.Text), start.GetProperty("prices")));
        Assert.Equal(["t", "protocol", "todayStartMs", "sources", "zone", "prices"], start.EnumerateObject().Select(m => m.Name));
    }

    [Fact]
    public void StartWithEverySourceUsesTheSpecKeys()
    {
        var start = Object(RelayLineEncoder.Start(0, Enum.GetValues<SourceKind>().Reverse(), RemoteZone.Current(TimeZoneInfo.Utc), Prices));
        Assert.Equal(Enum.GetValues<SourceKind>().Select(SourceNames.Of), start.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
        Assert.Empty(Object(RelayLineEncoder.Start(0, [], RemoteZone.Current(TimeZoneInfo.Utc), Prices)).GetProperty("sources").EnumerateArray());
    }

    [Fact]
    public void SmallLines()
    {
        Assert.Equal("{\"t\":\"ping\",\"id\":42}\n", Text(RelayLineEncoder.Ping(42)));
        Assert.Equal("{\"t\":\"today\",\"todayStartMs\":5}\n", Text(RelayLineEncoder.Today(5)));
        Assert.Equal(5, Object(RelayLineEncoder.Today(5)).GetProperty("todayStartMs").GetInt64());
        Assert.True(Object(RelayLineEncoder.Watch(true)).GetProperty("on").GetBoolean());
        Assert.False(Object(RelayLineEncoder.Watch(false)).GetProperty("on").GetBoolean());
        Assert.Equal("{\"t\":\"watch\",\"on\":true}\n", Text(RelayLineEncoder.Watch(true)));
        Assert.True(JsonElement.DeepEquals(Parse(Prices.Text), Object(RelayLineEncoder.Prices(Prices)).GetProperty("table")));
        Assert.Equal("{\"t\":\"prices\",\"table\":" + Prices.Text + "}\n", Text(RelayLineEncoder.Prices(Prices)));

        var berlin = RemoteZone.Current(TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin"));
        var zone = Object(RelayLineEncoder.Zone(berlin));
        Assert.Equal("zone", zone.GetProperty("t").GetString());
        Assert.Equal("Europe/Berlin", zone.GetProperty("id").GetString());
        Assert.Equal(berlin.Tzif, Convert.FromBase64String(zone.GetProperty("tzif").GetString()!));

        var history = RelayLineEncoder.History(new Dictionary<string, string>
        {
            ["2026-11-03/codex"] = "0123456789abcdef", ["2026-11-02/claudeCode"] = "fedcba9876543210",
        });
        Assert.Equal("""{"t":"history","digests":{"2026-11-02/claudeCode":"fedcba9876543210","2026-11-03/codex":"0123456789abcdef"}}""" + "\n",
            Text(history));
        Assert.Equal("{\"t\":\"history\",\"digests\":{}}\n", Text(RelayLineEncoder.History(new Dictionary<string, string>())));
    }

    [Fact]
    public void HistoryKeysAreInUtf8ByteOrder()
    {
        var history = RelayLineEncoder.History(new Dictionary<string, string> { ["\U0001F600"] = "b", ["～"] = "a", ["Z"] = "c", ["a"] = "d" });
        Assert.Equal(["Z", "a", "～", "\U0001F600"], Object(history).GetProperty("digests").EnumerateObject().Select(m => m.Name));
    }

    [Fact]
    public void StringsAreEscapedOnlyWhereJsonNeedsIt()
    {
        var history = Text(RelayLineEncoder.History(new Dictionary<string, string> { ["q\"\\\n/é"] = "x" }));
        Assert.Contains("\"q\\\"\\\\\\n/é\":\"x\"", history, StringComparison.Ordinal);
        Assert.Equal("q\"\\\n/é", Object(Encoding.UTF8.GetBytes(history)).GetProperty("digests").EnumerateObject().Single().Name);
    }

    [Fact]
    public void ZonesAreTzif()
    {
        var current = RemoteZone.Current();
        Assert.True(RemoteZone.IsTzif(current.Tzif));
        var fixedZone = RemoteZone.Current(TimeZoneInfo.CreateCustomTimeZone("Test Fixed +1", TimeSpan.FromHours(1), "Test Fixed +1", "Test Fixed +1"));
        Assert.Equal("Etc/GMT-1", fixedZone.Id);
        Assert.True(RemoteZone.IsTzif(fixedZone.Tzif));
        var odd = RemoteZone.Current(TimeZoneInfo.CreateCustomTimeZone("Test Fixed +5:30", new TimeSpan(5, 30, 0), "Test", "Test"));
        Assert.Equal("UTC", odd.Id);
        Assert.True(RemoteZone.IsTzif(odd.Tzif));
        Assert.Null(RemoteZone.Create("Bad Zone", current.Tzif));
        Assert.Null(RemoteZone.Create("UTC", "nope"u8.ToArray()));
    }

    [Fact]
    public void LinesBeyond4MiBAreRefused()
    {
        Assert.Equal(4 << 20, RelayLineEncoder.MaxLineBytes);
        Assert.Null(RemotePriceTableJson.From(Encoding.UTF8.GetBytes("{\"k\":\"" + new string('x', 4 << 20) + "\"}")));

        var nearly = RemotePriceTableJson.From(Encoding.UTF8.GetBytes("{\"k\":\"" + new string('x', (4 << 20) - 20) + "\"}"));
        Assert.NotNull(nearly);
        Assert.Null(RelayLineEncoder.Prices(nearly));
        Assert.Null(RelayLineEncoder.Start(0, [SourceKind.Codex], RemoteZone.Current(TimeZoneInfo.Utc), nearly));

        const int frame = 32;
        var exact = RemotePriceTableJson.From(Encoding.UTF8.GetBytes("{\"k\":\"" + new string('x', (4 << 20) - frame) + "\"}"));
        Assert.NotNull(exact);
        Assert.Equal(4 << 20, RelayLineEncoder.Prices(exact)?.Length);
        var over = RemotePriceTableJson.From(Encoding.UTF8.GetBytes("{\"k\":\"" + new string('x', (4 << 20) - frame + 1) + "\"}"));
        Assert.NotNull(over);
        Assert.Null(RelayLineEncoder.Prices(over));

        var digests = Enumerable.Range(0, 120_000).ToDictionary(i => $"2026-01-01/source{i:D6}", _ => "0123456789abcdef");
        Assert.Null(RelayLineEncoder.History(digests));
    }

    private static byte[] BundledPrices()
    {
        using var stream = typeof(PriceBook).Assembly.GetManifestResourceStream(PriceBook.BundledResourceName)
            ?? throw new InvalidOperationException("no bundled prices");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string Text(byte[]? line)
    {
        Assert.NotNull(line);
        return Encoding.UTF8.GetString(line);
    }

    /// <summary>One compact line: a JSON object and exactly one newline, at the end.</summary>
    private static JsonElement Object(byte[]? line)
    {
        Assert.NotNull(line);
        Assert.Equal((byte)'\n', line[^1]);
        Assert.DoesNotContain((byte)'\n', line[..^1]);
        var value = Parse(Encoding.UTF8.GetString(line, 0, line.Length - 1));
        Assert.Equal(JsonValueKind.Object, value.ValueKind);
        return value;
    }

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
