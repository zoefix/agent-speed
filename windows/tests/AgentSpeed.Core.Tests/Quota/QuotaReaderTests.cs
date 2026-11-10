using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Quota;

namespace AgentSpeed.Core.Tests.Quota;

/// <summary>The readers' behavior beyond the fixtures: cache search and polling, the bridge on disk, the signed-in markers.</summary>
public sealed class QuotaReaderTests
{
    private const string UsageKey = "1/0/https://claude.ai/api/organizations/0fba5d0f-43ab-4376-8ca3-f41e01ec4623/usage";

    private static byte[] Entry(string name) => File.ReadAllBytes(FixtureLocator.PathOf("quota/" + name + ".bin"));

    [Fact]
    public void SearchFindsTheNewestUsageEntryAndPollsOnlyChangedOnes()
    {
        using var temp = new TempDirectory();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var clock = now;
        var other = temp.Combine("1111111111111111_0");
        File.WriteAllBytes(other, Entry("other-url"));
        var usage = temp.Combine(DesktopCacheEntry.FileName(UsageKey));
        File.WriteAllBytes(usage, Entry("zstd-ultra"));
        File.SetLastWriteTimeUtc(usage, DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllBytes(temp.Combine("not-an-entry_0"), Entry("identity-max"));

        var reader = new DesktopQuotaReader(() => [temp.Path], () => clock);
        Assert.True(reader.Poll(desktopRuns: false));
        Assert.Equal(1, reader.CandidateCount);
        Assert.Equal(2, reader.LastSearchReads);
        Assert.Equal(usage, reader.SnapshotFile);
        Assert.Equal(["7d", "Fable 7d"], reader.Snapshot!.Windows.Select(w => QuotaWindows.Label(w)));

        // Within a minute nothing is read; after it, a changed candidate is.
        File.WriteAllBytes(usage, Entry("identity-max"));
        clock += 30_000;
        Assert.False(reader.Poll(desktopRuns: false));
        clock += 31_000;
        Assert.True(reader.Poll(desktopRuns: false));
        Assert.Equal(["5h", "7d", "Opus 7d"], reader.Snapshot!.Windows.Select(w => QuotaWindows.Label(w)));

        // A vanished candidate is dropped.
        File.Delete(usage);
        clock += 61_000;
        Assert.True(reader.Poll(desktopRuns: false));
        Assert.Null(reader.Snapshot);
        Assert.Equal(0, reader.CandidateCount);
    }

    [Fact]
    public void DesktopStartingSearchesAgain()
    {
        using var temp = new TempDirectory();
        var clock = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var reader = new DesktopQuotaReader(() => [temp.Path], () => clock);
        Assert.False(reader.Poll(desktopRuns: false));
        File.WriteAllBytes(temp.Combine(DesktopCacheEntry.FileName(UsageKey)), Entry("gzip-max"));
        clock += 61_000;
        Assert.False(reader.Poll(desktopRuns: false));
        reader.DesktopStarted();
        Assert.True(reader.Poll(desktopRuns: true));
        Assert.Equal(1794339805000, reader.Snapshot!.ObservedMs);
    }

    [Fact]
    public void OldEntriesAreNotSearched()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine(DesktopCacheEntry.FileName(UsageKey));
        File.WriteAllBytes(path, Entry("identity-max"));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-8));
        var reader = new DesktopQuotaReader(() => [temp.Path]);
        reader.Poll(desktopRuns: true);
        Assert.Equal(0, reader.CandidateCount);
        Assert.Equal(0, reader.LastSearchReads);
    }

    [Theory]
    [InlineData("0123456789abcdef_0", true)]
    [InlineData("0123456789ABCDEF_0", true)]
    [InlineData("0123456789abcdef_1", false)]
    [InlineData("0123456789abcdeg_0", false)]
    [InlineData("index", false)]
    public void EntryNames(string name, bool expected) => Assert.Equal(expected, DesktopQuotaReader.IsEntryName(name));

    [Fact]
    public void RateLimitsThatAreNotAnObjectAreIgnored()
    {
        var line = CodexLineReader.Read(Encoding.UTF8.GetBytes(
            """{"timestamp":"2026-11-10T19:00:00Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":[1,2]}}"""));
        Assert.Null(line.RateLimits);
        var withObject = CodexLineReader.Read(Encoding.UTF8.GetBytes(
            """{"timestamp":"2026-11-10T19:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":true,"window_minutes":300}}}}"""));
        Assert.NotNull(withObject.RateLimits);
        Assert.Empty(withObject.RateLimits!.Windows(0));
    }

    [Fact]
    public void BridgeOnAndOffOnDisk()
    {
        using var temp = new TempDirectory();
        var settings = temp.WriteFile(".claude/settings.json", "{\n  \"model\": \"opus\",\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"~/line.sh\",\n    \"padding\": 2\n  },\n  \"env\": {}\n}\n");
        var paths = new ClaudeCliPaths(settings, temp.Combine("data"));
        var helper = new byte[] { 1, 2, 3, 4 };
        var bridge = new ClaudeCliBridge(paths, () => new MemoryStream(helper));

        Assert.Equal(ClaudeCliBridgeStatus.Waiting, bridge.TurnOn());
        Assert.Equal(helper, File.ReadAllBytes(paths.HelperCopy));
        var on = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Equal(["model", "statusLine", "env"], on.Select(p => p.Key));
        Assert.Equal(2, on["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Contains("agent-speed-statusline", on["statusLine"]!["command"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.True(File.Exists(paths.Sidecar));
        Assert.True(File.Exists(paths.Backup));
        Assert.Equal("~/line.sh", ClaudeCliQuota.PreviousCommand(File.ReadAllBytes(paths.Sidecar)));

        // Turning it on again keeps the first sidecar.
        Assert.Equal(ClaudeCliBridgeStatus.Waiting, bridge.TurnOn());
        Assert.Equal("~/line.sh", ClaudeCliQuota.PreviousCommand(File.ReadAllBytes(paths.Sidecar)));

        File.WriteAllText(paths.QuotaFile, "{\"version\":1,\"receivedMs\":1,\"windows\":{}}");
        Assert.Equal(ClaudeCliBridgeStatus.Ok, bridge.Status());

        bridge.TurnOff();
        var off = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Equal(["model", "statusLine", "env"], off.Select(p => p.Key));
        Assert.Equal("~/line.sh", off["statusLine"]!["command"]!.GetValue<string>());
        Assert.False(File.Exists(paths.Sidecar));
        Assert.False(File.Exists(paths.Backup));
        Assert.False(File.Exists(paths.QuotaFile));
        Assert.EndsWith("}\n", File.ReadAllText(settings), StringComparison.Ordinal);
        Assert.DoesNotContain("\r", File.ReadAllText(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void BridgeLeavesAReplacedOrBrokenFileAlone()
    {
        using var temp = new TempDirectory();
        var settings = temp.WriteFile("settings.json", "{ not json");
        var paths = new ClaudeCliPaths(settings, temp.Combine("data"));
        var bridge = new ClaudeCliBridge(paths, () => new MemoryStream([1]));
        Assert.Equal(ClaudeCliBridgeStatus.Unreadable, bridge.TurnOn());
        Assert.Equal("{ not json", File.ReadAllText(settings));
        Assert.False(File.Exists(paths.Sidecar));

        File.WriteAllText(settings, "{}");
        Assert.Equal(ClaudeCliBridgeStatus.Waiting, bridge.TurnOn());
        var replaced = "{\n  \"statusLine\": {\n    \"type\": \"command\",\n    \"command\": \"npx ccstatusline\"\n  }\n}\n";
        File.WriteAllText(settings, replaced);
        Assert.Equal(ClaudeCliBridgeStatus.Replaced, bridge.Status());
        bridge.TurnOff();
        Assert.Equal(replaced, File.ReadAllText(settings));
        Assert.False(File.Exists(paths.Sidecar));
    }

    [Fact]
    public void BridgeWithoutAHelperFails()
    {
        using var temp = new TempDirectory();
        var settings = temp.WriteFile("settings.json", "{}");
        var bridge = new ClaudeCliBridge(new ClaudeCliPaths(settings, temp.Combine("data")), () => null);
        Assert.Equal(ClaudeCliBridgeStatus.Failed, bridge.TurnOn());
        Assert.Equal("{}", File.ReadAllText(settings));
    }

    [Fact]
    public void BridgeCreatesAMissingSettingsFile()
    {
        using var temp = new TempDirectory();
        var paths = new ClaudeCliPaths(temp.Combine(".claude", "settings.json"), temp.Combine("data"));
        var bridge = new ClaudeCliBridge(paths, () => new MemoryStream([7]));
        Assert.Equal(ClaudeCliBridgeStatus.Waiting, bridge.TurnOn());
        Assert.False(File.Exists(paths.Backup));
        bridge.TurnOff();
        Assert.Equal("{}\n", File.ReadAllText(paths.SettingsFile));
    }

    [Fact]
    public void QuotaFileReaderFollowsTheFile()
    {
        using var temp = new TempDirectory();
        var file = temp.Combine("claude-cli.json");
        var reader = new ClaudeCliQuotaReader(file);
        Assert.False(reader.Poll());
        var state = new CliQuotaState(1794338685000, new CliWindowValue(6, 1794354000), null);
        File.WriteAllBytes(file, ClaudeCliQuota.Write(state));
        Assert.True(reader.Poll());
        Assert.Equal(1794338685000, reader.Snapshot!.ObservedMs);
        Assert.Single(reader.Snapshot.Windows);
        Assert.False(reader.Poll());
        File.Delete(file);
        Assert.True(reader.Poll());
        Assert.Null(reader.Snapshot);
    }

    [Theory]
    [InlineData("""{"auth_mode":"chatgpt","OPENAI_API_KEY":null}""", true)]
    [InlineData("""{"OPENAI_API_KEY":"sk-x","tokens":{"id_token":"x"}}""", true)]
    [InlineData("""{"OPENAI_API_KEY":"sk-x","auth_mode":"apikey"}""", false)]
    [InlineData("""{"tokens":null}""", false)]
    [InlineData("[1]", false)]
    [InlineData("{", false)]
    public void CodexSignIn(string json, bool expected) => Assert.Equal(expected, QuotaSignIn.IsCodexSignedIn(Encoding.UTF8.GetBytes(json)));

    [Theory]
    [InlineData("""{"projects":{"a":{"oauthAccount":{}}},"oauthAccount":{"emailAddress":"x"}}""", true)]
    [InlineData("""{"projects":{"a":{"oauthAccount":{}}}}""", false)]
    [InlineData("""{"oauthAccount":null}""", false)]
    public void ClaudeSignIn(string json, bool expected) => Assert.Equal(expected, QuotaSignIn.IsClaudeSignedIn(Encoding.UTF8.GetBytes(json)));

    [Fact]
    public void SignInFollowsTheFile()
    {
        using var temp = new TempDirectory();
        var clock = 0L;
        var codex = temp.Combine("auth.json");
        var signIn = new QuotaSignIn(codex, temp.Combine(".claude.json"), () => clock);
        Assert.False(signIn.Codex);
        File.WriteAllText(codex, """{"auth_mode":"chatgpt"}""");
        Assert.True(signIn.Codex);
        Assert.False(signIn.ClaudeCode);
        File.Delete(codex);
        Assert.False(signIn.Codex);
    }
}
