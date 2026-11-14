using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Quota;
using AgentSpeed.Core.Quota.Estimate;

namespace AgentSpeed.Core.Tests.Quota;

/// <summary>Claude Code's usage cache (QUOTAS 5.6): the <c>claudeCodeCache</c> cases of quota-cases.json and the reader's cadence.</summary>
public sealed class ClaudeCodeCacheTests : IDisposable
{
    private static readonly Lazy<JsonElement> Cases = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota-cases.json"))).RootElement.GetProperty("claudeCodeCache").Clone());

    private readonly TempDirectory _temp = new();
    private long _clock = 1_794_338_773_000;
    private DateTime _written = new(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);

    public void Dispose() => _temp.Dispose();

    private static JsonElement Root => Cases.Value;

    private static string Text(string name) =>
        Root.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name).GetProperty("text").GetString()!;

    private string ConfigFile => _temp.Combine(".claude.json");

    public static TheoryData<string> FileNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Value.GetProperty("files").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FileNames))]
    public void FilesReadAsTheReference(string name)
    {
        var c = Root.GetProperty("files").EnumerateArray().Single(f => f.GetProperty("name").GetString() == name);
        var parsed = ClaudeCodeUsageCache.TryRead(Encoding.UTF8.GetBytes(c.GetProperty("text").GetString()!), out var cache);
        var expected = c.GetProperty("expected");
        if (expected.ValueKind == JsonValueKind.String)
        {
            Assert.Equal("unparsed", expected.GetString());
            Assert.False(parsed, name);
            return;
        }

        Assert.True(parsed, name);
        AssertSnapshot(expected, cache);
    }

    [Fact]
    public void ReadsInARowKeepThePreviousValueWhenTheFileDoesNotParse()
    {
        var reads = Root.GetProperty("reads");
        var texts = reads.GetProperty("texts").EnumerateArray().Select(t => t.GetString()!).ToList();
        var expected = reads.GetProperty("expected").EnumerateArray().ToList();
        var reader = Reader();
        Assert.Null(reader.Snapshot);
        for (var i = 0; i < texts.Count; i++)
        {
            Write(texts[i]);
            _clock += QuotaSignIn.RereadMs + 1_000;
            reader.Poll();
            AssertSnapshot(expected[i], reader.Snapshot);
        }
    }

    [Fact]
    public void TheFileIsReadAtMostOnceAMinuteAndAgainWhenItChanges()
    {
        var reader = Reader();
        Assert.False(reader.Poll());
        Write(Text("observed"));
        Assert.True(reader.Poll());
        Assert.Equal(3, reader.Snapshot!.Windows.Count);

        // Unchanged: never read again.
        _clock += 10 * QuotaSignIn.RereadMs;
        Assert.False(reader.Poll());

        // Changed: read once the minute since the last read is over.
        Write(Text("otherAccount"));
        _clock += 1_000;
        Assert.True(reader.Poll());
        Assert.Null(reader.Snapshot);
        Write(Text("observed"));
        _clock += QuotaSignIn.RereadMs - 2_000;
        Assert.False(reader.Poll());
        Assert.Null(reader.Snapshot);
        _clock += 2_000;
        Assert.True(reader.Poll());
        Assert.NotNull(reader.Snapshot);

        // A changed file with the same size and time is not read again.
        var same = Same(Text("observed"));
        File.WriteAllText(ConfigFile, same);
        File.SetLastWriteTimeUtc(ConfigFile, _written);
        _clock += QuotaSignIn.RereadMs;
        Assert.False(reader.Poll());
        Assert.Equal(42, reader.Snapshot!.Windows[0].UsedPercent);

        File.Delete(ConfigFile);
        Assert.True(reader.Poll());
        Assert.Null(reader.Snapshot);
    }

    [Fact]
    public void AFileThatDoesNotParseIsReadAgainAtTheNextCheckEvenUnchanged()
    {
        var reader = Reader();
        var observed = Text("observed");
        Write(observed);
        Assert.True(reader.Poll());

        // Cut short, at the same size: the value stays.
        var cut = observed.TrimEnd()[..^1] + " " + new string(' ', observed.Length - observed.TrimEnd().Length);
        Assert.Equal(observed.Length, cut.Length);
        Write(cut);
        var stamp = _written;
        _clock += QuotaSignIn.RereadMs;
        Assert.False(reader.Poll());
        Assert.Equal(42, reader.Snapshot!.Windows[0].UsedPercent);

        // Rewritten with the same size and time: read again, since the last read did not parse.
        File.WriteAllText(ConfigFile, Same(observed));
        File.SetLastWriteTimeUtc(ConfigFile, stamp);
        _clock += QuotaSignIn.RereadMs;
        Assert.True(reader.Poll());
        Assert.Equal(43, reader.Snapshot!.Windows[0].UsedPercent);
    }

    [Fact]
    public void AFileOver64MiBHasNoCache()
    {
        var reader = Reader();
        Write(Text("observed"));
        Assert.True(reader.Poll());
        using (var stream = new FileStream(ConfigFile, FileMode.Open, FileAccess.Write))
        {
            stream.SetLength(QuotaSignIn.MaxFileBytes + 1);
        }

        File.SetLastWriteTimeUtc(ConfigFile, _written.AddMinutes(1));
        _clock += QuotaSignIn.RereadMs;
        Assert.True(reader.Poll());
        Assert.Null(reader.Snapshot);
    }

    [Fact]
    public void TheFileIsTheOneInClaudeCodesConfigFolder()
    {
        Write(Text("observed"));
        var reader = ClaudeCodeUsageCache.ForThisComputer(k => k == "CLAUDE_CONFIG_DIR" ? _temp.Path : null);
        Assert.True(reader.Poll());
        Assert.Equal(["5h", "7d", "Fable 7d"], reader.Snapshot!.Windows.Select(w => QuotaWindows.Label(w)));
    }

    public static TheoryData<string> JoinNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Cases.Value.GetProperty("joins").EnumerateArray())
        {
            data.Add(c.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JoinNames))]
    public void TheRoutesJoinWindowByWindow(string name)
    {
        var c = Root.GetProperty("joins").EnumerateArray().Single(j => j.GetProperty("name").GetString() == name);
        var bridge = QuotaFixtureTests.ReadSnapshot(c.GetProperty("bridge"));
        var cache = QuotaFixtureTests.ReadSnapshot(c.GetProperty("cache"));
        var joined = ClaudeCodeRoutes.Join(bridge, cache);
        AssertSnapshot(c.GetProperty("expected"), joined);
        if (bridge is null || cache is null)
        {
            Assert.Same(bridge ?? cache, joined);
        }
    }

    [Fact]
    public void TheEstimateGetsEachWindowAtItsOwnTimeOnce()
    {
        var feed = new QuotaWindowFeed();
        var step = 0;
        foreach (var c in Root.GetProperty("feed").EnumerateArray())
        {
            var given = feed.Next(QuotaFixtureTests.ReadSnapshot(c.GetProperty("snapshot"))!);
            var expected = c.GetProperty("expected").EnumerateArray().ToList();
            Assert.True(expected.Count == given.Count, $"step {step}: {expected.Count} groups expected, {given.Count} given");
            for (var i = 0; i < expected.Count; i++)
            {
                Assert.Equal(SourceKind.ClaudeCode, given[i].Source);
                Assert.Equal(expected[i].GetProperty("observedMs").GetInt64(), given[i].ObservedMs);
                Assert.Equal(QuotaFixtureTests.ReadWindows(expected[i].GetProperty("windows")), given[i].Windows);
            }

            step++;
        }

        Assert.Equal(6, step);
    }

    [Theory]
    [InlineData("ages")]
    [InlineData("cacheOnlyAges")]
    public void EachWindowHasItsOwnTime(string key)
    {
        var c = Root.GetProperty(key);
        var snapshot = QuotaFixtureTests.ReadSnapshot(c.GetProperty("snapshot"))!;
        var ordered = QuotaWindows.Ordered(snapshot.Windows);
        var expected = c.GetProperty("expected").EnumerateArray().ToList();
        Assert.Equal(expected.Select(e => e.GetProperty("label").GetString()), ordered.Select(w => QuotaWindows.Label(w)));
        Assert.Equal(expected.Select(e => e.GetProperty("observedMs").GetInt64()), ordered.Select(snapshot.TimeOf));
        if (c.TryGetProperty("shown", out var shownCases))
        {
            var shown = QuotaWindows.Shown(snapshot, c.GetProperty("nowMs").GetInt64());
            var cases = shownCases.EnumerateArray().ToList();
            Assert.Equal(cases.Count, shown.Count);
            for (var i = 0; i < shown.Count; i++)
            {
                Assert.Equal(cases[i].GetProperty("label").GetString(), shown[i].Label);
                Assert.Equal(cases[i].GetProperty("percent").GetInt32(), shown[i].Percent);
                Assert.Equal(cases[i].GetProperty("bar").GetDouble(), shown[i].Bar, 9);
                Assert.Equal((long?)cases[i].GetProperty("resetsAtMs").GetInt64(), shown[i].ResetsAtMs);
                Assert.Equal(cases[i].GetProperty("passed").GetBoolean(), shown[i].Passed);
            }
        }
    }

    private ClaudeCodeUsageCache Reader() => new(ConfigFile, () => _clock);

    /// <summary>Writes the file with a time one minute after the previous write.</summary>
    private void Write(string text)
    {
        File.WriteAllText(ConfigFile, text);
        _written = _written.AddMinutes(1);
        File.SetLastWriteTimeUtc(ConfigFile, _written);
    }

    /// <summary>The observed file with its 5-hour value at 43 instead of 42: the same size.</summary>
    private static string Same(string observed)
    {
        const string From = "\"utilization\": 42";
        var at = observed.IndexOf(From, StringComparison.Ordinal);
        Assert.True(at >= 0);
        return string.Concat(observed.AsSpan(0, at), "\"utilization\": 43", observed.AsSpan(at + From.Length));
    }

    private static void AssertSnapshot(JsonElement expected, QuotaSnapshot? actual)
    {
        if (expected.ValueKind != JsonValueKind.Null)
        {
            Assert.Equal(SourceKind.ClaudeCode, actual?.Source);
        }

        QuotaFixtureTests.AssertSnapshot(expected, actual);
    }
}
