using AgentSpeed.Core.IO;
using AgentSpeed.Core.Monitoring;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// parser-expected.json <c>codebuddyCode</c> (METRICS 4.20): the traces, the discovery and the roots of the reference's
/// <c>check_codebuddy_roots</c>; the files themselves run with every other fixture in <see cref="ParserFixtureTests"/>.
/// </summary>
public sealed class CodeBuddyFixtureTests
{
    public static TheoryData<string, int, string, long?> Traces()
    {
        var data = new TheoryData<string, int, string, long?>();
        foreach (var trace in CodeBuddyCodeFixture.Expected.GetProperty("traces").EnumerateArray())
        {
            data.Add(trace.GetProperty("file").GetString()!, trace.GetProperty("afterLine").GetInt32(), trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    [Fact]
    public void EveryTraceIsListed() => Assert.Equal(22, Traces().Count);

    [Theory]
    [MemberData(nameof(Traces))]
    public void PhaseAfterLineMatchesTrace(string file, int afterLine, string phase, long? inFlightStartMs)
    {
        var deduper = new Deduper();
        foreach (var relative in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray().Select(e => e.GetString()!).TakeWhile(r => r != file))
        {
            ParserFixtureTests.ParseFixture(relative, deduper, new RecordingSink());
        }

        var parser = ParserFixtureTests.ParseFixture(file, deduper, new RecordingSink(), afterLine);
        Assert.Equal((phase, inFlightStartMs), (ParserFixtureTests.PhaseName(parser.Phase), parser.InFlightStartMs));
    }

    /// <summary>Main sessions and their sub-agents only: not the stream-json log, tool results, workflows or a stray file.</summary>
    [Fact]
    public void DiscoveryFindsMainSessionsAndTheirSubAgentsOnly()
    {
        var found = TranscriptDiscovery.Enumerate(CodeBuddyCodeFixture.Root, Families.Naming(RootFamily.CodeBuddyCode))
            .Select(f => Path.GetRelativePath(FixtureLocator.FixturesDirectory, f.Path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

        Assert.Equal(CodeBuddyCodeFixture.Expected.GetProperty("discover").EnumerateArray().Select(d => d.GetString()!), found);
        Assert.Equal(TranscriptNaming.WorkBuddySession, Families.Naming(RootFamily.CodeBuddyCode));
    }

    [Fact]
    public void EveryFixtureFileIsCodeBuddyCodeAndItsSubAgentKnowsItsMainSession()
    {
        var files = ParserFixtureTests.Expected.GetProperty("files").EnumerateObject()
            .Where(f => f.Name.StartsWith("codebuddy/", StringComparison.Ordinal)).ToList();

        Assert.Equal(3, files.Count);
        Assert.All(files, f => Assert.Equal("codebuddyCode", f.Value.GetProperty("source").GetString()));
        Assert.All(files, f => Assert.Equal(("high", "configured"),
            (f.Value.GetProperty("effort").GetProperty("mode").GetString(), f.Value.GetProperty("effort").GetProperty("evidence").GetString())));
        Assert.Single(files, f => f.Value.GetProperty("parentFile").GetString() is not null);
    }

    /// <summary>The cases of the reference's <c>check_codebuddy_roots</c>, then the same rules with Windows paths.</summary>
    [Theory]
    [InlineData(null, null, "/home/dev/.codebuddy/projects")]
    [InlineData("/data/cb", null, "/data/cb/projects")]
    [InlineData("/data/cb/projects", null, "/data/cb/projects/projects")]
    [InlineData(" \t", null, "/home/dev/.codebuddy/projects")]
    [InlineData("/data/cb", "/work/cb/", "/work/cb/projects")]
    [InlineData(null, "/work/cb/projects", "/work/cb/projects")]
    [InlineData("", null, "/home/dev/.codebuddy/projects")]
    [InlineData("\u001c\u00a0\u3000\n", null, "/home/dev/.codebuddy/projects")]
    [InlineData(" /data/cb ", null, " /data/cb /projects")]
    [InlineData("\u200b", null, "\u200b/projects")]
    [InlineData("/data/cb/", null, "/data/cb/projects")]
    [InlineData(null, "/work/Projects", "/work/Projects/projects")]
    public void TheRootIsTheConfigFolderOfTheVariableOrTheHomeOrTheChosenOne(string? variable, string? setting, string root)
    {
        Func<string, string?> environment = key => key == CodeBuddyCodePaths.ConfigVariable ? variable : null;
        var actual = setting is null
            ? CodeBuddyCodePaths.AutomaticRoot(environment, windows: false, "/home/dev")
            : CodeBuddyCodePaths.RootOf(setting, windows: false);

        Assert.Equal(root, actual);
    }

    [Theory]
    [InlineData(null, null, @"C:\Users\me\.codebuddy\projects")]
    [InlineData(@"E:\cfg\", null, @"E:\cfg\projects")]
    [InlineData("  ", null, @"C:\Users\me\.codebuddy\projects")]
    [InlineData(null, @"D:\cb\", @"D:\cb\projects")]
    [InlineData(null, @"D:\cb\Projects", @"D:\cb\Projects")]
    [InlineData(null, @"D:\cb/projects/", @"D:\cb/projects")]
    public void WindowsPathsFollowTheSameRules(string? variable, string? setting, string root)
    {
        Func<string, string?> environment = key => key == CodeBuddyCodePaths.ConfigVariable ? variable : null;
        var actual = setting is null
            ? CodeBuddyCodePaths.AutomaticRoot(environment, windows: true, @"C:\Users\me")
            : CodeBuddyCodePaths.RootOf(setting, windows: true);

        Assert.Equal(root, actual);
    }

    [Fact]
    public void TheSettingsFileIsInTheConfigFolderThatHoldsTheRoot()
    {
        Assert.Equal(FixtureLocator.PathOf("codebuddy/settings.json"), CodeBuddyCodePaths.SettingsPath(CodeBuddyCodeFixture.Root));
        Assert.Equal(FixtureLocator.PathOf("codebuddy/settings.json"),
            CodeBuddyCodePaths.SettingsPath(CodeBuddyCodeFixture.Root + Path.DirectorySeparatorChar));
        Assert.Equal(Path.Combine(Path.GetTempPath(), "cb", "projects", "settings.json"),
            CodeBuddyCodePaths.SettingsPath(Path.Combine(Path.GetTempPath(), "cb", "projects", "projects")));
    }
}
