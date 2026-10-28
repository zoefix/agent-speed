using System.Globalization;
using System.Text.Json.Nodes;
using AgentSpeed.Core.History;
using AgentSpeed.Core.Traffic;
using AgentSpeed.Core.Traffic.Engine;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// Runs every <c>spec/fixtures/traffic-cases.json</c> case through <see cref="TrafficEngine"/> the way
/// <c>spec/tools/traffic.py</c> does and compares the keys the case lists (numbers within 0.001).
/// </summary>
public sealed class TrafficCaseTests
{
    private static readonly Lazy<JsonObject> Document = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "traffic-cases.json")))!);

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var item in Document.Value["cases"]!.AsArray())
        {
            data.Add(item!["name"]!.GetValue<string>());
        }

        return data;
    }

    public static TheoryData<string, double, string> FormatRows()
    {
        var data = new TheoryData<string, double, string>();
        foreach (var row in Document.Value["format"]!.AsArray())
        {
            data.Add(row!["kind"]!.GetValue<string>(), row["value"]!.GetValue<double>(), row["text"]!.GetValue<string>());
        }

        return data;
    }

    [Fact]
    public void FixturesCoverBothPlatforms()
    {
        var names = Document.Value["cases"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToArray();
        Assert.True(names.Length >= 6);
        Assert.Contains("classify-macos", names);
        Assert.Contains("classify-windows", names);
    }

    [Theory]
    [MemberData(nameof(FormatRows))]
    public void FormatMatchesFixture(string kind, double value, string text)
    {
        var expected = text.Replace(' ', TrafficFormat.NoBreakSpace);
        var actual = kind switch
        {
            "total" => TrafficFormat.Total(value),
            "bits" => TrafficFormat.Rate(value, bits: true),
            _ => TrafficFormat.Rate(value),
        };
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void CaseMatches(string name)
    {
        var testCase = Document.Value["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var result = new TrafficCaseRunner(testCase).Run();
        var failures = new List<string>();
        Diff(string.Empty, testCase["expected"], result, failures);
        Assert.True(failures.Count == 0, $"{name}: {string.Join("; ", failures)}");
    }

    /// <summary>
    /// The WorkBuddy rows of METRICS 10.5 with the two sources resolved (a build that has them classifies these rows in
    /// <see cref="CaseMatches"/> too): every expected class of a WorkBuddy process matches.
    /// </summary>
    [Theory]
    [InlineData("classify-macos")]
    [InlineData("classify-windows")]
    public void WorkBuddyRowsMatch(string name)
    {
        var testCase = Document.Value["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var runner = new TrafficCaseRunner(testCase);
        var editions = new Dictionary<string, SourceKind>(StringComparer.Ordinal)
        {
            ["workbuddy"] = (SourceKind)100,
            ["workbuddyAi"] = (SourceKind)101,
        };
        SourceKind? Resolve(string key) => editions.TryGetValue(key, out var kind) ? kind : SourceNames.Parse(key);
        var platform = runner.Platform();
        var table = new ProcessTable(platform, rules: new AgentProcessRules(platform, Resolve));
        var inspector = new FixtureInspector();
        foreach (var sample in testCase["samples"]!.AsArray())
        {
            if (sample!["processes"] is JsonArray rows)
            {
                inspector.Update(rows);
                table.Refresh([.. rows.Select(r => FixtureInspector.Entry(r!.AsObject()))], inspector, sample["t"]!.GetValue<long>());
            }
        }

        var checkedRows = 0;
        foreach (var (pid, want) in testCase["expected"]!["classes"]!.AsObject())
        {
            if (want?.GetValue<string>() is not { } expected || !editions.ContainsKey(expected.Split('/')[0]))
            {
                continue;
            }

            var record = table.Records.Values.Single(r => r.Entry.Pid.ToString(CultureInfo.InvariantCulture) == pid);
            var name0 = editions.Single(e => e.Value == record.Class?.Source).Key;
            Assert.Equal(expected, $"{name0}/{record.Class!.Value.Role.ToString().ToLowerInvariant()}");
            checkedRows++;
        }

        Assert.True(checkedRows >= 3);
        foreach (var record in table.Records.Values.Where(r => r.Class is { } c && editions.ContainsValue(c.Source)))
        {
            // No WorkBuddy app, helper or daemon process counts: only the expected CLI rows (and their tools).
            Assert.Equal(testCase["expected"]!["classes"]![record.Entry.Pid.ToString(CultureInfo.InvariantCulture)]!.GetValue<string>(),
                $"{editions.Single(e => e.Value == record.Class!.Value.Source).Key}/{record.Class!.Value.Role.ToString().ToLowerInvariant()}");
        }
    }

    /// <summary>
    /// <c>traffic.py _diff</c>: only the wanted keys; lists must have the same length. A class of a source this build does
    /// not know yet (WorkBuddy, until its sources land) is checked by <see cref="WorkBuddyRowsMatch"/> instead.
    /// </summary>
    private static void Diff(string path, JsonNode? want, JsonNode? got, List<string> failures)
    {
        if (path.StartsWith(".classes.", StringComparison.Ordinal) && want is JsonValue pending && pending.TryGetValue<string>(out var text)
            && SourceNames.Parse(text.Split('/')[0]) is null && got is null)
        {
            return;
        }

        if (want is JsonObject wantObject && got is JsonObject gotObject)
        {
            foreach (var (key, value) in wantObject)
            {
                Diff($"{path}.{key}", value, gotObject[key], failures);
            }

            return;
        }

        if (want is JsonArray wantArray && got is JsonArray gotArray && wantArray.Count == gotArray.Count)
        {
            for (var i = 0; i < wantArray.Count; i++)
            {
                Diff($"{path}[{i}]", wantArray[i], gotArray[i], failures);
            }

            return;
        }

        if (want is null || got is null)
        {
            if ((want is null) != (got is null))
            {
                failures.Add($"{path}: want {want?.ToJsonString() ?? "null"}, got {got?.ToJsonString() ?? "null"}");
            }

            return;
        }

        if (want is JsonValue wv && got is JsonValue gv && wv.TryGetValue<double>(out var w) && gv.TryGetValue<double>(out var g))
        {
            if (Math.Abs(w - g) > 0.001)
            {
                failures.Add($"{path}: want {w}, got {g}");
            }

            return;
        }

        if (want.ToJsonString() != got.ToJsonString())
        {
            failures.Add($"{path}: want {want.ToJsonString()}, got {got.ToJsonString()}");
        }
    }
}

/// <summary>Fixture processes as an inspector: everything a case records for each identity.</summary>
internal sealed class FixtureInspector : IProcessInspector
{
    private readonly Dictionary<ProcessIdentity, JsonObject> _processes = new();

    public List<ProcessIdentity> ArgumentReads { get; } = new();

    public void Update(JsonArray rows)
    {
        foreach (var row in rows)
        {
            _processes[Identity(row!.AsObject())] = row.AsObject();
        }
    }

    public static ProcessIdentity Identity(JsonObject row) => new(row["pid"]!.GetValue<int>(), row["start"]!.GetValue<long>() * 1000);

    public static ProcessEntry Entry(JsonObject row) =>
        new(row["pid"]!.GetValue<int>(), row["ppid"]?.GetValue<int>() ?? 0, row["start"]!.GetValue<long>() * 1000);

    public string? ExecutablePath(in ProcessEntry entry) => _processes.GetValueOrDefault(entry.Identity)?["exe"]?.GetValue<string>();

    public IReadOnlyList<string>? Arguments(in ProcessEntry entry)
    {
        ArgumentReads.Add(entry.Identity);
        return _processes.GetValueOrDefault(entry.Identity)?["args"]?.AsArray().Select(a => a!.GetValue<string>()).ToArray();
    }

    public string? WorkingDirectory(in ProcessEntry entry) => _processes.GetValueOrDefault(entry.Identity)?["cwd"]?.GetValue<string>();

    public string? BundleId(in ProcessEntry entry) => _processes.GetValueOrDefault(entry.Identity)?["bundle"]?.GetValue<string>();

    public IReadOnlyCollection<string> OpenFiles(in ProcessEntry entry, long nowMs) =>
        _processes.GetValueOrDefault(entry.Identity)?["openFiles"]?.AsArray().Select(a => a!.GetValue<string>()).ToArray() ?? [];
}

/// <summary>The JSON shape of <c>traffic.py run_case</c>.</summary>
internal sealed class TrafficCaseRunner
{
    private readonly JsonObject _case;

    public TrafficCaseRunner(JsonObject testCase)
    {
        _case = testCase;
    }

    public TrafficPlatform Platform()
    {
        var kind = _case["platform"]?.GetValue<string>() == "windows" ? TrafficPlatformKind.Windows : TrafficPlatformKind.MacOS;
        var env = _case["env"]?.AsObject().ToDictionary(e => e.Key, e => e.Value!.GetValue<string>());
        return new TrafficPlatform(kind, _case["home"]?.GetValue<string>() ?? "/Users/dev", env);
    }

    public TimeZoneInfo Zone()
    {
        var offset = TimeSpan.FromMilliseconds(_case["tzOffsetMs"]?.GetValue<long>() ?? 0);
        return TimeZoneInfo.CreateCustomTimeZone("fixture", offset, "fixture", "fixture");
    }

    public IEnumerable<TrafficAgent> Agents() => (_case["agents"]?.AsArray() ?? []).Select(node =>
    {
        var agent = node!.AsObject();
        var intervals = (agent["active"]?.AsArray() ?? [])
            .Select(pair => (pair![0]!.GetValue<long>(), pair[1]!.GetValue<long>()))
            .ToArray();
        return new TrafficAgent(
            agent["id"]!.GetValue<string>(),
            SourceNames.Parse(agent["source"]!.GetValue<string>()) ?? SourceKind.Codex,
            agent["parent"]?.GetValue<string>(),
            agent["cwdPath"]?.GetValue<string>(),
            agent["transcript"]?.GetValue<string>(),
            intervals);
    });

    public TrafficEngine Engine { get; private set; } = null!;

    public FixtureInspector Inspector { get; } = new();

    public JsonObject Run()
    {
        var engine = new TrafficEngine(Platform(), Zone());
        Engine = engine;
        engine.SetAgents(Agents());
        var rateTimes = new HashSet<long>((_case["rateTimes"]?.AsArray() ?? []).Select(n => n!.GetValue<long>()));
        var streams = new Dictionary<string, int>(StringComparer.Ordinal);
        var interfaceKeys = new Dictionary<string, ulong>(StringComparer.Ordinal);
        var rates = new JsonObject();
        long lastT = 0;
        foreach (var node in _case["samples"]!.AsArray())
        {
            var sample = node!.AsObject();
            var t = sample["t"]!.GetValue<long>();
            lastT = t;
            List<ProcessEntry>? processes = null;
            if (sample["processes"] is JsonArray rows)
            {
                Inspector.Update(rows);
                processes = [.. rows.Select(r => FixtureInspector.Entry(r!.AsObject()))];
            }

            Dictionary<ulong, TrafficCounter>? interfaces = null;
            if (sample["interfaces"] is JsonObject raw)
            {
                interfaces = new Dictionary<ulong, TrafficCounter>();
                foreach (var (name, pair) in raw)
                {
                    if (!interfaceKeys.TryGetValue(name, out var key))
                    {
                        key = (ulong)interfaceKeys.Count + 1;
                        interfaceKeys[name] = key;
                    }

                    interfaces[key] = new TrafficCounter(pair![0]!.GetValue<long>(), pair[1]!.GetValue<long>());
                }
            }

            CounterSample? counters = null;
            if (sample["counters"] is JsonObject counterNode)
            {
                var id = counterNode["stream"]?.GetValue<string>() ?? string.Empty;
                if (!streams.TryGetValue(id, out var stream))
                {
                    stream = streams.Count;
                    streams[id] = stream;
                }

                var counterRows = (counterNode["rows"]?.AsArray() ?? [])
                    .Select(r => new CounterRow(FixtureInspector.Identity(r!.AsObject()),
                        new TrafficCounter(r["rx"]!.GetValue<long>(), r["tx"]!.GetValue<long>())))
                    .ToArray();
                counters = new CounterSample(stream, counterRows, counterNode["continues"]?.GetValue<bool>() ?? false);
            }

            engine.Tick(new TrafficSample(t, t, processes, interfaces, counters), Inspector);
            if (rateTimes.Contains(t))
            {
                rates[t.ToString(CultureInfo.InvariantCulture)] = Rates(engine);
            }
        }

        var classes = new JsonObject();
        foreach (var (identity, record) in engine.Table.Records.OrderBy(r => r.Key))
        {
            classes[identity.Pid.ToString(CultureInfo.InvariantCulture)] = record.Class is { } c
                ? JsonValue.Create($"{SourceNames.Of(c.Source)}/{c.Role.ToString().ToLowerInvariant()}")
                : null;
        }

        var today = engine.Ledger.Totals(engine.Ledger.Label(lastT)) ?? new TrafficDayTotals();
        return new JsonObject
        {
            ["classes"] = classes,
            ["today"] = Today(today),
            ["rates"] = rates,
            ["history"] = new JsonArray([.. engine.Ledger.History().Select(Row)]),
        };
    }

    private static JsonNode? Pair(TrafficRate? rate) => rate is { } r ? new JsonArray(r.Rx, r.Tx) : null;

    private static JsonObject Rates(TrafficEngine engine)
    {
        var sources = new JsonObject();
        foreach (var (source, series) in engine.SourceSeries)
        {
            sources[SourceNames.Of(source)] = new JsonObject { ["withTools"] = Pair(series.WithTools), ["selfOnly"] = Pair(series.SelfOnly) };
        }

        var agents = new JsonObject();
        foreach (var (id, series) in engine.AgentSeries)
        {
            agents[id] = new JsonObject { ["withTools"] = Pair(series.WithTools), ["selfOnly"] = Pair(series.SelfOnly) };
        }

        return new JsonObject { ["machine"] = Pair(engine.MachineRate), ["sources"] = sources, ["agents"] = agents };
    }

    private static JsonObject Split(TrafficSplit split) => new()
    {
        ["rxSelf"] = split.Self.Rx,
        ["txSelf"] = split.Self.Tx,
        ["rxTools"] = split.Tools.Rx,
        ["txTools"] = split.Tools.Tx,
    };

    private static JsonObject Today(TrafficDayTotals totals)
    {
        var sources = new JsonObject();
        foreach (var (source, traffic) in totals.Sources)
        {
            var row = Split(traffic.Split);
            row["unassignedRx"] = traffic.Unassigned.Rx;
            row["unassignedTx"] = traffic.Unassigned.Tx;
            sources[SourceNames.Of(source)] = row;
        }

        var agents = new JsonObject();
        foreach (var (id, split) in totals.Agents)
        {
            agents[id] = Split(split);
        }

        return new JsonObject
        {
            ["machine"] = new JsonObject { ["rx"] = totals.Machine.Rx, ["tx"] = totals.Machine.Tx, ["sampledMs"] = totals.MachineSampledMs },
            ["sources"] = sources,
            ["agents"] = agents,
        };
    }

    private static JsonObject Row(TrafficDay day) => new()
    {
        ["day"] = day.Day,
        ["key"] = day.Key,
        ["rx"] = day.Rx,
        ["tx"] = day.Tx,
        ["rxTools"] = day.RxTools,
        ["txTools"] = day.TxTools,
        ["sampledMs"] = day.SampledMs,
    };
}
