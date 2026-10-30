using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Droid;
using AgentSpeed.Core.History;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// parser-expected.json <c>droid</c>: traces, discovery, roots, sidecars, allocations and the scripted read cycles, like
/// the reference's <c>check_droid</c> (METRICS 4.16).
/// </summary>
public sealed class DroidFixtureTests : IDisposable
{
    private static readonly string[] Fields = ["inputTokens", "cacheCreationTokens", "cacheReadTokens", "outputTokens", "thinkingTokens"];

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    public static TheoryData<string, int, string, long?> Traces()
    {
        var data = new TheoryData<string, int, string, long?>();
        foreach (var trace in DroidFixture.Expected.GetProperty("traces").EnumerateArray())
        {
            data.Add(
                trace.GetProperty("file").GetString()!,
                trace.GetProperty("afterLine").GetInt32(),
                trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    public static TheoryData<int> SidecarCases() => Indexes("sidecars");

    public static TheoryData<int> AllocationCases() => Indexes("allocations");

    public static TheoryData<int> RootCases() => Indexes("roots");

    [Fact]
    public void EveryCaseIsListed()
    {
        Assert.Equal(18, Traces().Count);
        Assert.Equal(15, SidecarCases().Count);
        Assert.Equal(7, AllocationCases().Count);
        Assert.Equal(6, RootCases().Count);
        Assert.Equal(11, ParserFixtureTests.Expected.GetProperty("order").EnumerateArray()
            .Count(e => e.GetString()!.StartsWith("droid/", StringComparison.Ordinal)));
    }

    [Theory]
    [MemberData(nameof(Traces))]
    public void PhaseAfterLineMatchesTrace(string file, int afterLine, string phase, long? inFlightStartMs)
    {
        var deduper = new Deduper();
        foreach (var element in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray())
        {
            var relative = element.GetString()!;
            if (relative == file)
            {
                break;
            }

            ParserFixtureTests.ParseFixture(relative, deduper, new RecordingSink());
        }

        var parser = ParserFixtureTests.ParseFixture(file, deduper, new RecordingSink(), afterLine);

        Assert.Equal((phase, inFlightStartMs), (ParserFixtureTests.PhaseName(parser.Phase), parser.InFlightStartMs));
    }

    [Fact]
    public void DiscoveryFindsSessionsInTheRootAndOneFolderDownButNoDecoys()
    {
        var fixtures = CopyWithLink();
        var expected = DroidFixture.Expected.GetProperty("discover").EnumerateArray().Select(e => e.GetString()!).ToList();

        var found = TranscriptDiscovery.Enumerate(DroidFixture.RootIn(fixtures), TranscriptNaming.DroidSession)
            .Select(f => Path.GetRelativePath(fixtures, f.Path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, found);
    }

    [Fact]
    public void DiscoveryTimesAreTheNewerOfTranscriptAndSidecar()
    {
        var copy = FixtureCopy.WithModificationTimes(_temp.Combine("fixtures"));
        var root = DroidFixture.RootIn(copy);
        var file = TranscriptDiscovery.Enumerate(root, TranscriptNaming.DroidSession)
            .Single(f => Path.GetFileName(f.Path) == "a1f0c3d2-0b6e-4c1a-9d7e-2f3a4b5c6d70.jsonl");
        var sidecar = DroidPaths.SidecarOf(file.Path);
        Assert.Equal(File.GetLastWriteTimeUtc(file.Path), file.LastWriteUtc);

        File.SetLastWriteTimeUtc(sidecar, file.LastWriteUtc.AddMinutes(5));
        var later = TranscriptDiscovery.Enumerate(root, TranscriptNaming.DroidSession).Single(f => f.Path == file.Path);
        Assert.Equal(file.LastWriteUtc.AddMinutes(5), later.LastWriteUtc);
        Assert.Equal(later.LastWriteUtc, DroidPaths.Modified(file.Path, File.GetLastWriteTimeUtc(file.Path)));
    }

    [Fact]
    public void WatcherPathsFollowTheDiscoveryRules()
    {
        var root = DroidFixture.RootIn(CopyWithLink());
        string P(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        const string A = "a1f0c3d2-0b6e-4c1a-9d7e-2f3a4b5c6d70";

        Assert.True(TranscriptDiscovery.IsAcceptablePath(root, P($"-Users-dev-projects-forge/{A}.jsonl"), TranscriptNaming.DroidSession));
        Assert.True(TranscriptDiscovery.IsAcceptablePath(root, P("dae9fc0b-9ef7-4da3-8a67-1c2d3e4f5a69.jsonl"), TranscriptNaming.DroidSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root, P("-Users-dev-projects-forge/nested/deep.jsonl"), TranscriptNaming.DroidSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root, P("-Users-dev-projects-forge/.draft.jsonl"), TranscriptNaming.DroidSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root, P($".trash/{A}.jsonl"), TranscriptNaming.DroidSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root, P($"-Users-dev-projects-forge/{A}.jsonl.tmp"), TranscriptNaming.DroidSession));
        Assert.False(TranscriptDiscovery.IsAcceptablePath(root, P("-Users-dev-projects-forge/link.jsonl"), TranscriptNaming.DroidSession));

        Assert.Equal(P($"-Users-dev-projects-forge/{A}.jsonl"),
            DroidDiscovery.TranscriptOfSidecarPath(root, P($"-Users-dev-projects-forge/{A}.settings.json")));
        Assert.Null(DroidDiscovery.TranscriptOfSidecarPath(root, P($"-Users-dev-projects-forge/{A}.settings.json.bak")));
        Assert.Null(DroidDiscovery.TranscriptOfSidecarPath(root, P($"-Users-dev-projects-forge/nested/{A}.settings.json")));
        Assert.Null(DroidDiscovery.TranscriptOfSidecarPath(root, P("sessions-index.json")));
    }

    [Theory]
    [MemberData(nameof(RootCases))]
    public void RootsFollowTheSetting(int index)
    {
        var item = DroidFixture.Expected.GetProperty("roots")[index];
        var home = _temp.Combine("home" + index);
        Directory.CreateDirectory(home);
        foreach (var folder in item.GetProperty("make").EnumerateArray())
        {
            Directory.CreateDirectory(Path.Combine(home, folder.GetString()!.Replace('/', Path.DirectorySeparatorChar)));
        }

        var setting = item.GetProperty("setting").GetString();
        var root = setting is null
            ? DroidPaths.DefaultRoot(home)
            : DroidPaths.RootOf(Path.Combine(home, setting.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal(item.GetProperty("expected").GetString(), Path.GetRelativePath(home, root).Replace(Path.DirectorySeparatorChar, '/'));
        if (setting is not null)
        {
            Assert.Equal(Path.GetFullPath(root), SourceLocations.DroidRootOf(Path.Combine(home, setting.Replace('/', Path.DirectorySeparatorChar))));
        }
    }

    [Fact]
    public void TheSessionsFolderOfASettingIsFoundCaseInsensitivelyWhereTheFileSystemIs()
    {
        var factory = _temp.Combine("Factory");
        Directory.CreateDirectory(Path.Combine(factory, "Sessions"));
        var caseInsensitive = Directory.Exists(Path.Combine(factory, "sessions"));

        Assert.Equal(caseInsensitive ? Path.Combine(factory, "sessions") : factory, DroidPaths.RootOf(factory));
    }

    [Theory]
    [MemberData(nameof(SidecarCases))]
    public void SidecarsReadLikeTheReference(int index)
    {
        var item = DroidFixture.Expected.GetProperty("sidecars")[index];
        var text = item.GetProperty("text").GetString()!;
        var bytes = item.TryGetProperty("encoding", out var encoding) && encoding.GetString() == "latin-1"
            ? Encoding.Latin1.GetBytes(text)
            : Encoding.UTF8.GetBytes(text);
        var size = item.TryGetProperty("size", out var padded) ? padded.GetInt32() : bytes.Length;
        var path = _temp.Combine("x" + index + ".settings.json");
        File.WriteAllBytes(path, [.. bytes, .. Enumerable.Repeat((byte)' ', size - bytes.Length)]);

        var read = DroidSidecar.Read(path);

        var label = item.GetProperty("name").GetString();
        Assert.True(item.GetProperty("readable").GetBoolean() == (read.Status == DroidSidecarStatus.Readable), label);
        Assert.True(item.GetProperty("model").GetString() == read.Model, label);
        var counters = item.GetProperty("counters");
        if (counters.ValueKind == JsonValueKind.Null)
        {
            Assert.True(read.Counters is null, label);
        }
        else
        {
            Assert.Equal(Counters(counters), read.Counters);
        }
    }

    [Fact]
    public void ASidecarIsReadAgainOnlyWhenItChanges()
    {
        var path = _temp.Combine("s.settings.json");
        File.WriteAllText(path, """{"model":"m","tokenUsage":{"inputTokens":1}}""");
        var first = DroidSidecar.Read(path);
        Assert.Equal(DroidSidecarStatus.Readable, first.Status);
        Assert.Equal(DroidSidecarStatus.Unchanged, DroidSidecar.Read(path, first.Signature).Status);

        File.WriteAllText(path, """{"model":"m","tokenUsage":{"inputTokens":22}}""");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
        var second = DroidSidecar.Read(path, first.Signature);
        Assert.Equal((DroidSidecarStatus.Readable, 22L), (second.Status, second.Counters!.Value.Input));
        Assert.Equal(DroidSidecarStatus.Missing, DroidSidecar.Read(_temp.Combine("none.settings.json")).Status);
    }

    [Fact]
    public void ASymlinkedSidecarIsNotFollowed()
    {
        var target = _temp.Combine("target.json");
        File.WriteAllText(target, """{"model":"m","tokenUsage":{"inputTokens":1}}""");
        var link = _temp.Combine("l.settings.json");
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Equal(DroidSidecarStatus.Unreadable, DroidSidecar.Read(link).Status);
    }

    [Theory]
    [MemberData(nameof(AllocationCases))]
    public void AllocationIsLargestRemainderInExactIntegers(int index)
    {
        var item = DroidFixture.Expected.GetProperty("allocations")[index];
        var weights = item.GetProperty("weights").EnumerateArray().Select(w => w.GetInt64()).ToArray();

        var shares = DroidAllocation.Shares(item.GetProperty("total").GetInt64(), weights);

        Assert.Equal(item.GetProperty("shares").EnumerateArray().Select(s => s.GetInt64()), shares);
    }

    /// <summary>The reference's <c>check_droid_live</c>: scripted writes and read cycles in a scratch home.</summary>
    [Fact]
    public void ScriptedReadCyclesMatchTheReference()
    {
        var deduper = new Deduper();
        var sessions = new Dictionary<string, LiveSession>(StringComparer.Ordinal);
        var number = 0;
        foreach (var step in DroidFixture.Expected.GetProperty("live").GetProperty("steps").EnumerateArray())
        {
            number++;
            if (step.TryGetProperty("append", out var append) || step.TryGetProperty("rewrite", out _))
            {
                var rewrite = append.ValueKind != JsonValueKind.String;
                var path = Scratch((rewrite ? step.GetProperty("rewrite") : append).GetString()!);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var data = Encoding.UTF8.GetBytes(string.Concat(step.GetProperty("lines").EnumerateArray().Select(l => Compact(l) + "\n")));
                if (rewrite)
                {
                    File.WriteAllBytes(path + ".new", data);
                    File.Move(path + ".new", path, overwrite: true);
                }
                else
                {
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Write(data);
                }
            }
            else if (step.TryGetProperty("sidecar", out var sidecar))
            {
                var path = DroidPaths.SidecarOf(Scratch(sidecar.GetString()!));
                File.WriteAllText(path, step.TryGetProperty("text", out var text) ? text.GetString() : step.GetProperty("doc").GetRawText());
                File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(step.GetProperty("mtime").GetInt64()));
            }
            else if (step.TryGetProperty("open", out var open))
            {
                var session = new LiveSession(Scratch(open.GetString()!), deduper);
                sessions[open.GetString()!] = session;
                if (step.GetProperty("atEnd").GetBoolean())
                {
                    session.OpenAtEnd();
                }
            }
            else
            {
                var name = step.GetProperty("read").GetString()!;
                var session = sessions[name];
                session.Read();
                var label = $"step {number}";
                var expected = step.GetProperty("responses").EnumerateArray().ToList();
                var actual = session.Sink.Latest;
                Assert.True(expected.Select(r => r.GetProperty("key").GetString()).SequenceEqual(actual.Select(r => r.Key)),
                    $"{label}: keys {string.Join(",", actual.Select(r => r.Key))}");
                for (var i = 0; i < expected.Count; i++)
                {
                    AssertResponse(expected[i], actual[i], label);
                }

                Assert.True(Counters(step.GetProperty("pending")) == session.Parser.Pending, $"{label}: pending {session.Parser.Pending}");
                if (step.TryGetProperty("phase", out var phase))
                {
                    Assert.True(phase.GetString() == ParserFixtureTests.PhaseName(session.Parser.Phase), $"{label}: phase {session.Parser.Phase}");
                }
            }
        }

        Assert.Equal(5, sessions.Count);
    }

    /// <summary>
    /// The reference's <c>check_droid_history</c>: analyses of a scratch home whose parent sidecars are unreadable. A
    /// skipped session is not stored, so the next analysis reads it again; stored ones keep their responses while their
    /// files and their parents' sidecars stay the same.
    /// </summary>
    [Fact]
    public void ScriptedHistoryAnalysesMatchTheReference()
    {
        var home = _temp.Combine("history-home");
        Directory.CreateDirectory(home);
        using var store = HistoryStore.Open(_temp.Combine("store", "history.sqlite"));
        var analyzer = new HistoryAnalyzer(store);
        var current = new Dictionary<string, List<ParsedResponse>>(StringComparer.Ordinal);
        var skipped = new List<string>();
        analyzer.FileAnalyzed = (path, responses) =>
        {
            var relative = Path.GetRelativePath(home, path).Replace(Path.DirectorySeparatorChar, '/');
            current.Remove(relative);
            if (responses is null)
            {
                skipped.Add(relative);
            }
            else if (responses.Count > 0)
            {
                current[relative] = [.. responses];
            }
        };
        var locations = new SourceLocations(string.Empty, string.Empty, string.Empty, string.Empty, [], DroidRoot: DroidPaths.DefaultRoot(home));
        var number = 0;
        var analyses = 0;
        foreach (var step in DroidFixture.Expected.GetProperty("history").GetProperty("steps").EnumerateArray())
        {
            number++;
            if (step.TryGetProperty("write", out var write))
            {
                var path = Path.Combine([home, .. write.GetString()!.Split('/')]);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, Encoding.UTF8.GetBytes(step.GetProperty("text").GetString()!));
                File.SetLastWriteTimeUtc(path, DateTime.UnixEpoch.AddMilliseconds(step.GetProperty("mtime").GetInt64()));
                continue;
            }

            analyses++;
            skipped.Clear();
            var report = analyzer.Run(locations, new HashSet<SourceKind> { SourceKind.Droid }, TimeZoneInfo.Utc, CancellationToken.None);
            Assert.False(report.Cancelled);
            var label = $"step {number}";
            var expected = step.GetProperty("responses").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().ToList());
            Assert.True(expected.Keys.Order(StringComparer.Ordinal).SequenceEqual(current.Keys.Order(StringComparer.Ordinal)),
                $"{label}: sessions {string.Join(",", current.Keys)}");
            foreach (var (file, responses) in expected)
            {
                Assert.True(responses.Select(r => r.GetProperty("key").GetString()).SequenceEqual(current[file].Select(r => r.Key)), $"{label} {file}");
                for (var i = 0; i < responses.Count; i++)
                {
                    AssertResponse(responses[i], current[file][i], $"{label} {file}");
                }
            }

            Assert.Equal(step.GetProperty("skipped").EnumerateArray().Select(e => e.GetString()!), skipped.Order(StringComparer.Ordinal));
        }

        Assert.Equal(3, analyses);
    }

    internal static DroidCounters Counters(JsonElement element) =>
        DroidCounters.FromFields(Fields.Select(f => element.GetProperty(f).GetInt64()).ToArray());

    /// <summary>A JSON value as Python's <c>json.dumps(…, ensure_ascii=False, separators=(",", ":"))</c> writes it.</summary>
    internal static string Compact(JsonElement element)
    {
        var raw = element.GetRawText();
        var builder = new StringBuilder(raw.Length);
        var inString = false;
        var escaped = false;
        foreach (var c in raw)
        {
            if (inString)
            {
                builder.Append(c);
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
                builder.Append(c);
            }
            else if (!char.IsWhiteSpace(c))
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private static void AssertResponse(JsonElement e, ParsedResponse r, string label)
    {
        label += " " + r.Key;
        Assert.True(ParserFixtureTests.NullableLong(e.GetProperty("startMs")) == r.StartMs, $"{label} startMs {r.StartMs}");
        Assert.True(e.GetProperty("endMs").GetInt64() == r.EndMs, $"{label} endMs {r.EndMs}");
        Assert.True(e.GetProperty("model").GetString() == r.Model, $"{label} model {r.Model}");
        Assert.True(e.GetProperty("provider").GetString() == r.Provider, $"{label} provider {r.Provider}");
        Assert.True(e.GetProperty("final").GetBoolean() == r.Final && e.GetProperty("fast").GetBoolean() == r.Fast, label);
        var tokens = new TokenCounts(
            e.GetProperty("input").GetInt64(), e.GetProperty("cachedInput").GetInt64(), e.GetProperty("cacheWriteInput").GetInt64(),
            e.GetProperty("output").GetInt64(), e.GetProperty("reasoning").GetInt64(), e.GetProperty("cacheWrite1hInput").GetInt64());
        Assert.True(tokens == r.Tokens, $"{label} tokens {r.Tokens}");
    }

    private static TheoryData<int> Indexes(string name)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < DroidFixture.Expected.GetProperty(name).GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    private string Scratch(string relative) => _temp.Combine(["scratch", .. relative.Split('/')]);

    /// <summary>
    /// A copy of the Droid fixtures with the decoy <c>link.jsonl</c> a symbolic link again: the build copies fixtures to
    /// the output folder as regular files. Without the right to create links, the decoy is removed instead.
    /// </summary>
    private string CopyWithLink()
    {
        var fixtures = _temp.Combine("linked");
        var source = DroidFixture.Root;
        var root = DroidFixture.RootIn(fixtures);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        var link = Path.Combine(root, "-Users-dev-projects-forge", "link.jsonl");
        File.Delete(link);
        try
        {
            File.CreateSymbolicLink(link, "a1f0c3d2-0b6e-4c1a-9d7e-2f3a4b5c6d70.jsonl");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        return fixtures;
    }

    /// <summary>One followed transcript, read like a tracked file of the monitor (tailer, batch, reset, cycle).</summary>
    private sealed class LiveSession(string path, IDeduper deduper)
    {
        private readonly DroidTailer _tailer = new(path);

        public DroidParser Parser { get; } = new(path, deduper);

        public RecordingSink Sink { get; } = new();

        /// <summary>Older than the startup window: the transcript starts at its end and the sidecar sets the mark now.</summary>
        public void OpenAtEnd()
        {
            _tailer.StartAt(new FileInfo(path).Length, StartAlignment.SkipPartialLine);
            Parser.StartAtEnd();
            Read();
        }

        public void Read()
        {
            var batch = Parser.NewBatch();
            Parser.PrepareBatch(batch, _tailer.Offset);
            var result = _tailer.ReadAppended(batch);
            if (result.WasReset)
            {
                Parser.Reset();
            }

            batch.ApplyTo(Sink);
        }
    }
}
