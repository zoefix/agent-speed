using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Quota;

namespace AgentSpeed.Core.Tests.Quota;

/// <summary>Every case of spec/fixtures/quota-cases.json that the core implements (QUOTAS 10).</summary>
public sealed class QuotaFixtureTests
{
    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota-cases.json"))).RootElement.Clone());

    private static readonly Lazy<JsonElement> Strings = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(FixtureLocator.FixturesDirectory, "..", "strings.json"))).RootElement.Clone());

    private static JsonElement Root => Cases.Value;

    private static long Now => Root.GetProperty("now").GetInt64();

    public static TheoryData<string> CodexNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Value.GetProperty("codex").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(CodexNames))]
    public void CodexLinesGiveTheNewestSnapshot(string name)
    {
        var c = Root.GetProperty("codex").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var collector = new CodexQuotaCollector();
        var parser = new CodexParser("C:/codex/sessions/2026/11/10/rollout-a.jsonl", new Deduper()) { Quota = collector };
        var sink = new RecordingSink();
        // A paginated copy: lines below ordinal 100 are copied history (METRICS 3.5).
        Feed.Lines(parser, sink, """{"timestamp":"2025-01-01T00:00:00Z","type":"session_meta","payload":{"id":"t1","subagent_history_start_ordinal":100}}""");
        foreach (var item in c.GetProperty("lines").EnumerateArray())
        {
            var line = JsonNode.Parse(item.GetProperty("line").GetRawText())!.AsObject();
            if (item.TryGetProperty("copied", out var copied) && copied.GetBoolean())
            {
                line["ordinal"] = 5;
            }

            Feed.Lines(parser, sink, line.ToJsonString());
        }

        AssertSnapshot(c.GetProperty("expected"), collector.Snapshot);
    }

    [Fact]
    public void CacheEntriesParseAsTheReference()
    {
        foreach (var c in Root.GetProperty("entries").EnumerateArray())
        {
            var file = c.GetProperty("file").GetString()!;
            var entry = DesktopCacheEntry.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota/" + file)), 1794338773000);
            var expected = c.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.True(entry is null, file);
                continue;
            }

            Assert.True(entry is not null, file);
            Assert.Equal(expected.GetProperty("url").GetString(), entry!.Url);
            Assert.Equal(expected.GetProperty("observedMs").GetInt64(), entry.ObservedMs);
            AssertWindows(expected.GetProperty("windows"), DesktopUsageBody.Windows(entry.Body), file);
        }
    }

    [Fact]
    public void EveryEncodingIsCovered()
    {
        var parsed = Root.GetProperty("entries").EnumerateArray()
            .Where(c => c.GetProperty("expected").ValueKind != JsonValueKind.Null)
            .Select(c => c.GetProperty("file").GetString()!).ToList();
        Assert.Contains("zstd-ultra.bin", parsed);
        Assert.Contains("gzip-max.bin", parsed);
        Assert.Contains("br-ultra.bin", parsed);
        Assert.Contains("identity-max.bin", parsed);
    }

    [Fact]
    public void EntryFileNameIsTheKeysHash()
    {
        var c = Root.GetProperty("fileName");
        Assert.Equal(c.GetProperty("expected").GetString(), DesktopCacheEntry.FileName(c.GetProperty("key").GetString()!));
    }

    [Fact]
    public void TheNewestDesktopEntryWins()
    {
        var c = Root.GetProperty("desktopNewest");
        var mtime = c.GetProperty("mtimeMs").GetInt64();
        var entries = c.GetProperty("files").EnumerateArray().Select(f => f.GetString()!)
            .Select(f => (f, DesktopCacheEntry.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota/" + f)), mtime), mtime)).ToList();
        var chosen = DesktopQuotaReader.Choose(entries);
        var expected = c.GetProperty("expected");
        Assert.NotNull(chosen);
        Assert.Equal(expected.GetProperty("file").GetString(), chosen!.Value.File);
        AssertSnapshot(expected, chosen.Value.Snapshot);
    }

    [Fact]
    public void HelperMergesAndPrintsItsLine()
    {
        var state = CliQuotaState.Empty;
        foreach (var step in Root.GetProperty("helper").EnumerateArray())
        {
            var input = Encoding.UTF8.GetBytes(step.GetProperty("input").GetRawText());
            var now = step.GetProperty("nowMs").GetInt64();
            state = ClaudeCliQuota.Merge(state, input, now, out _);
            AssertState(step.GetProperty("expected"), state);
            Assert.Equal(step.GetProperty("line").GetString(), ClaudeCliQuota.Line(state, now));

            // The file round-trips.
            var reread = ClaudeCliQuota.Read(ClaudeCliQuota.Write(state));
            Assert.Equal(state, reread);
        }
    }

    [Fact]
    public void ExpiredWindowsLeaveTheLine()
    {
        var c = Root.GetProperty("helperExpired");
        var state = ClaudeCliQuota.Read(Encoding.UTF8.GetBytes(c.GetProperty("stored").GetRawText()));
        Assert.Equal(c.GetProperty("line").GetString(), ClaudeCliQuota.Line(state, c.GetProperty("nowMs").GetInt64()));
    }

    [Fact]
    public void QuotaFileBecomesTheCliSnapshot()
    {
        var c = Root.GetProperty("helperExpired");
        var state = ClaudeCliQuota.Read(Encoding.UTF8.GetBytes(c.GetProperty("stored").GetRawText()));
        var snapshot = ClaudeCliQuotaReader.ToSnapshot(state, 0);
        Assert.Equal(SourceKind.ClaudeCode, snapshot.Source);
        Assert.Equal(1794339013000, snapshot.ObservedMs);
        Assert.Equal([QuotaWindowKind.FiveHour, QuotaWindowKind.SevenDay], snapshot.Windows.Select(w => w.Kind));
        Assert.Equal(1794372000000, snapshot.Windows[0].ResetsAtMs);
        Assert.Equal(88, snapshot.Windows[1].UsedPercent);
    }

    [Fact]
    public void CommandsQuoteThePath()
    {
        var c = Root.GetProperty("commands");
        foreach (var pair in c.GetProperty("posix").EnumerateArray())
        {
            Assert.Equal(pair[1].GetString(), ClaudeCliSettings.PosixCommand(pair[0].GetString()!));
        }

        foreach (var pair in c.GetProperty("windows").EnumerateArray())
        {
            Assert.Equal(pair[1].GetString(), ClaudeCliSettings.WindowsCommand(pair[0].GetString()!));
        }
    }

    [Fact]
    public void BridgeEditsTheSettingsAsTheReference()
    {
        var command = Root.GetProperty("commands").GetProperty("posix")[0][1].GetString()!;
        foreach (var c in Root.GetProperty("bridge").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            var text = c.GetProperty("settings").ValueKind == JsonValueKind.Null ? null : c.GetProperty("settings").GetString();
            var sidecar = c.GetProperty("sidecar").ValueKind == JsonValueKind.Null ? null : JsonNode.Parse(c.GetProperty("sidecar").GetRawText())!.AsObject();
            JsonObject? previous = sidecar;
            if (c.GetProperty("on") is { ValueKind: JsonValueKind.Object } on)
            {
                var result = ClaudeCliSettings.On(text, command, sidecar);
                Assert.Equal(on.GetProperty("status").GetString(), result.Status == ClaudeCliBridgeStatus.Unreadable ? "unreadable" : "on");
                AssertText(on.GetProperty("settings"), result.Settings, name + " on");
                AssertJson(on.GetProperty("previous"), result.Previous, name + " previous");
                previous = result.Previous;
                text = result.Settings;
            }

            if (c.GetProperty("off") is { ValueKind: JsonValueKind.Object } off)
            {
                Assert.Equal("off", off.GetProperty("status").GetString());
                AssertText(off.GetProperty("settings"), ClaudeCliSettings.Off(text, previous), name + " off");
            }
        }
    }

    [Fact]
    public void BridgeStatusReadsTheSettings()
    {
        foreach (var c in Root.GetProperty("bridgeStatus").EnumerateArray())
        {
            var status = ClaudeCliSettings.Status(c.GetProperty("settings").GetString(), c.GetProperty("quotaFile").GetBoolean());
            var expected = c.GetProperty("expected").GetString();
            Assert.Equal(expected, status switch
            {
                ClaudeCliBridgeStatus.Waiting => "waiting",
                ClaudeCliBridgeStatus.Ok => "ok",
                ClaudeCliBridgeStatus.Replaced => "replaced",
                ClaudeCliBridgeStatus.Unreadable => "unreadable",
                _ => status.ToString(),
            });
        }
    }

    [Fact]
    public void ShownWindowsAreOrderedAndResetAware()
    {
        var c = Root.GetProperty("shown");
        var snapshot = new QuotaSnapshot(SourceKind.Codex, c.GetProperty("snapshot").GetProperty("observedMs").GetInt64(),
            ReadWindows(c.GetProperty("snapshot").GetProperty("windows")));
        var shown = QuotaWindows.Shown(snapshot, Now);
        var expected = c.GetProperty("expected").EnumerateArray().ToList();
        Assert.Equal(expected.Count, shown.Count);
        for (var i = 0; i < shown.Count; i++)
        {
            Assert.Equal(expected[i].GetProperty("label").GetString(), shown[i].Label);
            Assert.Equal(expected[i].GetProperty("percent").GetInt32(), shown[i].Percent);
            Assert.Equal(expected[i].GetProperty("bar").GetDouble(), shown[i].Bar, 9);
            Assert.Equal(NullableLong(expected[i].GetProperty("resetsAtMs")), shown[i].ResetsAtMs);
            Assert.Equal(expected[i].GetProperty("passed").GetBoolean(), shown[i].Passed);
        }
    }

    [Fact]
    public void PaceIsTheShareOfTheWindowPassed()
    {
        foreach (var c in Root.GetProperty("pace").EnumerateArray())
        {
            var pace = QuotaWindows.Pace(ReadWindow(c.GetProperty("window")), Now);
            var expected = c.GetProperty("expected");
            if (expected.ValueKind == JsonValueKind.Null)
            {
                Assert.Null(pace);
            }
            else
            {
                Assert.NotNull(pace);
                Assert.Equal(expected.GetDouble(), pace!.Value, 9);
            }
        }
    }

    [Fact]
    public void LabelsInEveryLanguage()
    {
        foreach (var pair in Root.GetProperty("labels").EnumerateArray())
        {
            var window = ReadWindow(pair[0]);
            foreach (var lang in pair[1].EnumerateObject())
            {
                var labels = new QuotaLabels(String("quota.window.month", lang.Name), String("quota.window.credits", lang.Name));
                Assert.Equal(lang.Value.GetString(), QuotaWindows.Label(window, labels));
            }
        }
    }

    [Fact]
    public void OrderGroupsNamedWindows()
    {
        var c = Root.GetProperty("order");
        var ordered = QuotaWindows.Ordered(ReadWindows(c.GetProperty("windows")));
        Assert.Equal(c.GetProperty("expected").EnumerateArray().Select(e => e.GetString()), ordered.Select(w => QuotaWindows.Label(w)));
    }

    [Fact]
    public void PercentsRoundHalfUpAndClamp()
    {
        foreach (var pair in Root.GetProperty("percents").EnumerateArray())
        {
            Assert.Equal(pair[1].GetInt32(), QuotaWindows.Percent(pair[0].GetDouble()));
        }
    }

    private static string String(string key, string lang) => Strings.Value.GetProperty(key).GetProperty(lang).GetString()!;

    private static long? NullableLong(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();

    private static QuotaWindowKind Kind(string text) => text switch
    {
        "fiveHour" => QuotaWindowKind.FiveHour,
        "sevenDay" => QuotaWindowKind.SevenDay,
        "modelWeek" => QuotaWindowKind.ModelWeek,
        "month" => QuotaWindowKind.Month,
        "credits" => QuotaWindowKind.Credits,
        _ => QuotaWindowKind.Other,
    };

    internal static QuotaWindow ReadWindow(JsonElement w) => new(
        Kind(w.GetProperty("kind").GetString()!),
        w.GetProperty("name").ValueKind == JsonValueKind.Null ? null : w.GetProperty("name").GetString(),
        w.GetProperty("minutes").ValueKind == JsonValueKind.Null ? null : w.GetProperty("minutes").GetInt32(),
        w.GetProperty("usedPercent").GetDouble(),
        NullableLong(w.GetProperty("resetsAtMs")));

    private static List<QuotaWindow> ReadWindows(JsonElement array) => [.. array.EnumerateArray().Select(ReadWindow)];

    private static void AssertSnapshot(JsonElement expected, QuotaSnapshot? actual)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.GetProperty("observedMs").GetInt64(), actual!.ObservedMs);
        AssertWindows(expected.GetProperty("windows"), actual.Windows, expected.GetProperty("source").GetString()!);
    }

    private static void AssertWindows(JsonElement expected, IReadOnlyList<QuotaWindow> actual, string what)
    {
        var windows = ReadWindows(expected);
        Assert.True(windows.Count == actual.Count, $"{what}: {windows.Count} windows expected, {actual.Count} found");
        for (var i = 0; i < windows.Count; i++)
        {
            Assert.Equal(windows[i], actual[i]);
        }
    }

    private static void AssertState(JsonElement expected, CliQuotaState actual)
    {
        Assert.Equal(NullableLong(expected.GetProperty("receivedMs")), actual.ReceivedMs);
        var windows = expected.GetProperty("windows");
        AssertWindowValue(windows, "five_hour", actual.FiveHour);
        AssertWindowValue(windows, "seven_day", actual.SevenDay);
    }

    private static void AssertWindowValue(JsonElement windows, string name, CliWindowValue? actual)
    {
        if (!windows.TryGetProperty(name, out var w))
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(w.GetProperty("used_percentage").GetDouble(), actual!.Value.UsedPercentage);
        Assert.Equal(w.GetProperty("resets_at").GetDouble(), actual.Value.ResetsAt);
    }

    private static void AssertText(JsonElement expected, string? actual, string what)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, what + ": the file must be left alone");
            return;
        }

        Assert.Equal(expected.GetString(), actual);
    }

    private static void AssertJson(JsonElement expected, JsonNode? actual, string what)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.True(actual is null, what);
            return;
        }

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expected.GetRawText()), actual), what + ": " + actual?.ToJsonString());
    }
}
