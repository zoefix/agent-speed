using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Hermes;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The <c>hermes</c> block of <c>parser-expected.json</c> (METRICS 4.15), checked the way <c>reference.py</c> checks it:
/// discovery, the log sets of <c>order</c>, traces, wall times, homes, a rotation with real renames, config.yaml texts
/// and a scripted Codex runtime switch.
/// </summary>
public sealed class HermesFixtureTests
{
    private static JsonElement Expected => HermesFixture.Expected;

    private static TimeZoneInfo Zone => HermesFixture.Zone;

    public static TheoryData<string> SetNames()
    {
        var data = new TheoryData<string>();
        foreach (var set in Expected.GetProperty("sets").EnumerateObject())
        {
            data.Add(set.Name);
        }

        return data;
    }

    public static TheoryData<int> Indexes(string property)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Expected.GetProperty(property).GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    public static TheoryData<int> Traces() => Indexes("traces");

    public static TheoryData<int> WallTimes() => Indexes("wallTimes");

    public static TheoryData<int> Homes() => Indexes("homes");

    public static TheoryData<int> RuntimeConfigs() => Indexes("runtimeConfigs");

    public static TheoryData<int> AgentConfigs() => Indexes("agentConfigs");

    [Fact]
    public void DiscoveryFindsTheLogsOfTheHomeAndEveryProfile()
    {
        var found = HermesPaths.LogFolders(HermesFixture.Home)
            .SelectMany(HermesPaths.LogFiles)
            .Select(Relative)
            .Order(StringComparer.Ordinal)
            .ToList();
        Assert.Equal(Expected.GetProperty("discover").EnumerateArray().Select(e => e.GetString()!).ToList(), found);
    }

    [Theory]
    [MemberData(nameof(SetNames))]
    public void LogSetMatchesExpectation(string relativeFolder)
    {
        var (sets, sink) = FeedOrder();
        var expected = Expected.GetProperty("sets").GetProperty(relativeFolder);
        var set = sets[FixtureLocator.PathOf(relativeFolder)];

        Assert.Equal(expected.GetProperty("malformedLines").GetInt64(), set.MalformedLines);
        var agents = expected.GetProperty("agents");
        Assert.Equal(agents.EnumerateObject().Select(a => a.Name).ToList(), set.Agents.Select(a => a.SessionId).ToList());
        foreach (var item in agents.EnumerateObject())
        {
            var agent = set.Agent(item.Name)!;
            var want = item.Value;
            var label = relativeFolder + "#" + item.Name;
            Assert.Equal(set.Folder + "#" + item.Name, agent.AgentId);
            Assert.Equal("hermes", want.GetProperty("source").GetString());
            Assert.Equal(JsonValueKind.Null, want.GetProperty("project").ValueKind);
            Assert.Equal(JsonValueKind.Null, want.GetProperty("parentId").ValueKind);
            Assert.True(want.GetProperty("model").GetString() == agent.Model, $"{label} model: got {agent.Model}");
            Assert.True(want.GetProperty("isSubagent").GetBoolean() == agent.IsSubagent, $"{label} isSubagent");
            Assert.True(want.GetProperty("phase").GetString() == ParserFixtureTests.PhaseName(agent.Phase), $"{label} phase: got {agent.Phase}");
            Assert.True(ParserFixtureTests.NullableLong(want.GetProperty("inFlightStartMs")) == agent.InFlightStartMs,
                $"{label} inFlightStartMs: got {agent.InFlightStartMs}");
            Assert.True(ParserFixtureTests.NullableLong(want.GetProperty("lastLineMs")) == agent.LastLineMs,
                $"{label} lastLineMs: got {agent.LastLineMs}");
            var responses = sink.Latest.Where(r => r.AgentId == agent.AgentId).ToList();
            var expectedResponses = want.GetProperty("responses").EnumerateArray().ToList();
            Assert.Equal(expectedResponses.Select(r => r.GetProperty("key").GetString()!).ToList(), responses.Select(r => r.Key).ToList());
            for (var i = 0; i < expectedResponses.Count; i++)
            {
                AssertResponse(expectedResponses[i], responses[i], label);
            }

            ParserFixtureTests.AssertModes(want, label, ExecutionModes.Empty, responses, agent.SessionModes, agent.ConfiguredModes);
        }
    }

    [Fact]
    public void EveryResponseIsOneFinalCompletion()
    {
        var (_, sink) = FeedOrder();
        Assert.NotEmpty(sink.Emissions);
        Assert.All(sink.Emissions, e => Assert.True(e.IsCompletion && e.Response.Final && e.Response.Source == SourceKind.Hermes));
        Assert.Equal(sink.Emissions.Count, sink.Latest.Count);
    }

    [Theory]
    [MemberData(nameof(Traces))]
    public void TraceMatchesExpectation(int index)
    {
        var trace = Expected.GetProperty("traces")[index];
        var file = trace.GetProperty("file").GetString()!;
        var (sets, _) = FeedOrder((file, trace.GetProperty("afterLine").GetInt32()));
        var set = sets[Path.GetDirectoryName(FixtureLocator.PathOf(file))!];
        var agent = set.Agent(trace.GetProperty("session").GetString()!);
        var label = $"{file} line {trace.GetProperty("afterLine").GetInt32()}";
        Assert.True(trace.GetProperty("phase").GetString() == (agent is null ? null : ParserFixtureTests.PhaseName(agent.Phase)), $"{label} phase");
        Assert.True(ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")) == agent?.InFlightStartMs,
            $"{label} inFlightStartMs: got {agent?.InFlightStartMs}");
    }

    [Theory]
    [MemberData(nameof(WallTimes))]
    public void WallTimeMatchesExpectation(int index)
    {
        var item = Expected.GetProperty("wallTimes")[index];
        var line = item.GetProperty("line").GetString()!;
        var zone = HermesClock.FindZone(item.GetProperty("timeZone").GetString()!);
        var got = HermesLogSet.IsRecordLine(line) ? HermesLogSet.TimestampOf(line, zone) : null;
        Assert.Equal(ParserFixtureTests.NullableLong(item.GetProperty("ms")), got);
    }

    [Fact]
    public void WindowsZoneIdsConvertLikeTheirIanaZones()
    {
        // Windows names the fixture zone "Pacific Standard Time"; both ids must give the same instants.
        var iana = HermesClock.FindZone("America/Los_Angeles");
        var windows = TimeZoneInfo.TryConvertIanaIdToWindowsId("America/Los_Angeles", out var id) ? HermesClock.FindZone(id) : iana;
        foreach (var item in Expected.GetProperty("wallTimes").EnumerateArray()
            .Where(w => w.GetProperty("timeZone").GetString() == "America/Los_Angeles"))
        {
            var line = item.GetProperty("line").GetString()!;
            Assert.Equal(HermesLogSet.TimestampOf(line, iana), HermesLogSet.TimestampOf(line, windows));
        }
    }

    [Theory]
    [MemberData(nameof(Homes))]
    public void HomeMatchesExpectation(int index)
    {
        var item = Expected.GetProperty("homes")[index];
        var environment = item.GetProperty("env").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
        var home = HermesPaths.Home(name => environment.GetValueOrDefault(name), item.GetProperty("platform").GetString() == "win32",
            item.GetProperty("home").GetString()!);
        Assert.Equal(item.GetProperty("expected").GetString(), home);
    }

    [Theory]
    [MemberData(nameof(RuntimeConfigs))]
    public void ConfigTextMatchesExpectation(int index)
    {
        var item = Expected.GetProperty("runtimeConfigs")[index];
        Assert.True(item.GetProperty("codexRuntime").GetBoolean() == HermesConfig.SelectsCodexRuntime(item.GetProperty("text").GetString()!),
            item.GetProperty("name").GetString());
    }

    [Theory]
    [MemberData(nameof(AgentConfigs))]
    public void AgentScalarsMatchExpectation(int index)
    {
        var item = Expected.GetProperty("agentConfigs")[index];
        var want = item.EnumerateObject().Where(p => p.Name is not ("name" or "text"))
            .ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.Null ? null : p.Value.GetString(), StringComparer.Ordinal);
        Assert.Equal(want, HermesConfig.AgentScalars(item.GetProperty("text").GetString()!));
    }

    [Fact]
    public void RotationFollowsFilesAcrossRenames()
    {
        var rotation = Expected.GetProperty("rotation");
        using var temp = new TempDirectory();
        var folder = temp.Combine("logs");
        Directory.CreateDirectory(folder);
        var source = new HermesSource(folder, chosen: true, new Deduper(), HermesClock.FindZone(rotation.GetProperty("timeZone").GetString()!));
        var keys = new List<string>();
        var seeded = false;
        var number = 0;
        foreach (var step in rotation.GetProperty("steps").EnumerateArray())
        {
            number++;
            if (step.TryGetProperty("append", out var append))
            {
                Append(Path.Combine(folder, append.GetString()!), step);
            }
            else if (step.TryGetProperty("rename", out var rename))
            {
                File.Move(Path.Combine(folder, rename[0].GetString()!), Path.Combine(folder, rename[1].GetString()!));
            }
            else if (step.TryGetProperty("delete", out var delete))
            {
                File.Delete(Path.Combine(folder, delete.GetString()!));
            }
            else
            {
                var update = seeded ? source.Refresh(discover: true) : source.Seed(0, CancellationToken.None);
                seeded = true;
                keys.AddRange(update.Responses.Select(r => r.Response.Key));
                Assert.True(Sorted(step.GetProperty("responseKeys")).SequenceEqual(keys.Order(StringComparer.Ordinal)),
                    $"step {number}: keys {string.Join(", ", keys)}");
                var set = source.Sets.Single();
                foreach (var want in step.GetProperty("agents").EnumerateObject())
                {
                    AssertAgent(set.Agent(want.Name), want.Value, $"step {number} {want.Name}");
                }
            }
        }
    }

    [Fact]
    public void CodexRuntimeSwitchFollowsEachProfilesConfig()
    {
        var scenario = Expected.GetProperty("codexRuntime");
        using var temp = new TempDirectory();
        var home = temp.Combine("home");
        foreach (var folder in scenario.GetProperty("logFolders").EnumerateArray())
        {
            Directory.CreateDirectory(Path.Combine(home, Native(folder.GetString()!)));
        }

        var source = new HermesSource(home, chosen: false, new Deduper(), HermesClock.FindZone(scenario.GetProperty("timeZone").GetString()!));
        var keys = new List<string>();
        var seeded = false;
        var number = 0;
        foreach (var step in scenario.GetProperty("steps").EnumerateArray())
        {
            number++;
            if (step.TryGetProperty("append", out var append))
            {
                Append(Path.Combine(home, Native(append.GetString()!)), step);
            }
            else if (step.TryGetProperty("write", out var write))
            {
                var path = Path.Combine(home, Native(write.GetString()!));
                var data = Encoding.UTF8.GetBytes(step.GetProperty("text").GetString()!);
                var size = step.TryGetProperty("size", out var padded) ? padded.GetInt32() : data.Length;
                File.WriteAllBytes(path, [.. data, .. Enumerable.Repeat((byte)'#', size - data.Length)]);
                File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(step.GetProperty("mtime").GetInt64()));
            }
            else if (step.TryGetProperty("remove", out var remove))
            {
                File.Delete(Path.Combine(home, Native(remove.GetString()!)));
            }
            else
            {
                var update = seeded ? source.Refresh(discover: true) : source.Seed(0, CancellationToken.None);
                seeded = true;
                keys.AddRange(update.Responses.Select(r => r.Response.Key));
                Assert.True(Sorted(step.GetProperty("responseKeys")).SequenceEqual(keys.Order(StringComparer.Ordinal)),
                    $"step {number}: keys {string.Join(", ", keys)}");
                foreach (var want in step.GetProperty("sets").EnumerateObject())
                {
                    var set = source.Sets.Single(s => s.Folder == Path.Combine(home, Native(want.Name)));
                    Assert.True(want.Value.GetProperty("codexRuntime").GetBoolean() == set.CodexRuntime, $"step {number} {want.Name} switch");
                    foreach (var agent in want.Value.GetProperty("agents").EnumerateObject())
                    {
                        AssertAgent(set.Agent(agent.Name), agent.Value, $"step {number} {want.Name}#{agent.Name}");
                    }
                }
            }
        }
    }

    [Fact]
    public void StateDatabaseAndOtherLogsAreNeverRead()
    {
        // Only agent.log, agent.log.<digits> and config.yaml: errors.log, gateway.log, lock files, archives and
        // folders starting with '.' (profiles/.deleted) are left alone.
        var found = HermesPaths.LogFolders(HermesFixture.Home).SelectMany(HermesPaths.LogFiles).Select(Path.GetFileName).ToList();
        Assert.All(found, name => Assert.True(HermesPaths.IsLogName(name)));
        Assert.DoesNotContain(HermesPaths.LogFolders(HermesFixture.Home), f => f.Contains(".deleted", StringComparison.Ordinal));
        Assert.False(HermesPaths.IsLogName("agent.log.1.gz"));
        Assert.False(HermesPaths.IsLogName("agent.log."));
        Assert.False(HermesPaths.IsLogName("agent.log.x"));
        Assert.False(HermesPaths.IsLogName("Agent.log"));
        Assert.True(HermesPaths.IsLogName("agent.log.12"));
    }

    [Fact]
    public void ConfigFileIsTheParentOfALogsFolder()
    {
        Assert.Equal(Path.Combine("h", "profiles", "p", "config.yaml"), HermesPaths.ConfigPath(Path.Combine("h", "profiles", "p", "logs"), windows: false));
        Assert.Null(HermesPaths.ConfigPath(Path.Combine("h", "LOGS"), windows: false));
        Assert.Equal(Path.Combine("h", "config.yaml"), HermesPaths.ConfigPath(Path.Combine("h", "LOGS"), windows: true));
        Assert.Null(HermesPaths.ConfigPath(Path.Combine("h", "my-logs"), windows: true));
    }

    internal static void AssertResponse(JsonElement e, ParsedResponse r, string label)
    {
        label += " " + r.Key;
        Assert.True(ParserFixtureTests.NullableLong(e.GetProperty("startMs")) == r.StartMs, $"{label} startMs: got {r.StartMs}");
        Assert.True(e.GetProperty("endMs").GetInt64() == r.EndMs, $"{label} endMs: got {r.EndMs}");
        Assert.True(e.GetProperty("input").GetInt64() == r.Tokens.Input, $"{label} input: got {r.Tokens.Input}");
        Assert.True(e.GetProperty("cachedInput").GetInt64() == r.Tokens.CachedInput, $"{label} cachedInput: got {r.Tokens.CachedInput}");
        Assert.True(e.GetProperty("cacheWriteInput").GetInt64() == r.Tokens.CacheWriteInput, $"{label} cacheWriteInput: got {r.Tokens.CacheWriteInput}");
        Assert.True(e.GetProperty("cacheWrite1hInput").GetInt64() == r.Tokens.CacheWrite1hInput, $"{label} cacheWrite1hInput");
        Assert.True(e.GetProperty("output").GetInt64() == r.Tokens.Output, $"{label} output: got {r.Tokens.Output}");
        Assert.True(e.GetProperty("reasoning").GetInt64() == r.Tokens.Reasoning, $"{label} reasoning: got {r.Tokens.Reasoning}");
        Assert.True(e.GetProperty("model").GetString() == r.Model, $"{label} model: got {r.Model}");
        Assert.True(e.GetProperty("provider").GetString() == r.Provider, $"{label} provider: got {r.Provider}");
        Assert.True(e.GetProperty("final").GetBoolean() == r.Final, $"{label} final");
        Assert.True(e.GetProperty("fast").GetBoolean() == r.Fast, $"{label} fast");
    }

    private static void AssertAgent(HermesAgent? agent, JsonElement want, string label)
    {
        Assert.True(agent is not null, label + ": no agent");
        foreach (var field in want.EnumerateObject())
        {
            object? got = field.Name switch
            {
                "phase" => ParserFixtureTests.PhaseName(agent!.Phase),
                "inFlightStartMs" => agent!.InFlightStartMs,
                "lastLineMs" => agent!.LastLineMs,
                "model" => agent!.Model,
                _ => throw new InvalidOperationException(field.Name),
            };
            object? expected = field.Value.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.Number => field.Value.GetInt64(),
                _ => field.Value.GetString(),
            };
            Assert.True(Equals(expected, got), $"{label} {field.Name}: expected {expected}, got {got}");
        }
    }

    /// <summary>Feeds the files of <c>order</c> to one log set per folder (config.yaml checked first), optionally stopping after a line of one file.</summary>
    private static (Dictionary<string, HermesLogSet> Sets, RecordingSink Sink) FeedOrder((string File, int Lines)? stop = null)
    {
        var deduper = new Deduper();
        var sink = new RecordingSink();
        var sets = new Dictionary<string, HermesLogSet>(StringComparer.Ordinal);
        foreach (var relative in Expected.GetProperty("order").EnumerateArray().Select(e => e.GetString()!))
        {
            var path = FixtureLocator.PathOf(relative);
            var folder = Path.GetDirectoryName(path)!;
            if (!sets.TryGetValue(folder, out var set))
            {
                sets[folder] = set = new HermesLogSet(folder, deduper, Zone);
                set.CheckConfig();
            }

            var limit = stop is { } at && at.File == relative ? at.Lines : int.MaxValue;
            var count = 0;
            foreach (var line in PhysicalLines(File.ReadAllBytes(path)))
            {
                if (++count > limit)
                {
                    break;
                }

                set.Line(line, sink);
            }

            if (limit != int.MaxValue)
            {
                break;
            }
        }

        foreach (var set in sets.Values)
        {
            set.RefreshSessions();
        }

        return (sets, sink);
    }

    /// <summary>Complete lines split at <c>\n</c> with one trailing <c>\r</c> removed; an unterminated last line is not a line.</summary>
    private static IEnumerable<byte[]> PhysicalLines(byte[] content)
    {
        var start = 0;
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != (byte)'\n')
            {
                continue;
            }

            var end = i > start && content[i - 1] == (byte)'\r' ? i - 1 : i;
            yield return content[start..end];
            start = i + 1;
        }
    }

    private static void Append(string path, JsonElement step)
    {
        var text = step.TryGetProperty("text", out var raw)
            ? raw.GetString()!
            : string.Concat(step.GetProperty("lines").EnumerateArray().Select(l => l.GetString() + "\n"));
        using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Write(Encoding.UTF8.GetBytes(text));
        }

        File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(step.GetProperty("mtime").GetInt64()));
    }

    private static IEnumerable<string> Sorted(JsonElement keys) => keys.EnumerateArray().Select(k => k.GetString()!).Order(StringComparer.Ordinal);

    private static string Native(string relative) => relative.Replace('/', Path.DirectorySeparatorChar);

    private static string Relative(string path) =>
        Path.GetRelativePath(FixtureLocator.FixturesDirectory, path).Replace(Path.DirectorySeparatorChar, '/');
}
