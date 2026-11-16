using System.Globalization;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Traffic.Engine;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The CodeBuddy Code row of METRICS 10.5 in <c>traffic-cases.json</c> (processes 1700 and up of both classify cases): a script host
/// running the npm package <c>@tencent-ai/codebuddy-code</c> is the agent; a native <c>codebuddy</c> or a checkout of the same name is not.
/// </summary>
public sealed class CodeBuddyTrafficTests
{
    private static readonly Lazy<JsonObject> Document = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "traffic-cases.json")))!);

    [Theory]
    [InlineData("classify-macos", 6, 4)]
    [InlineData("classify-windows", 2, 2)]
    public void TheNpmPackageOnAScriptHostIsTheAgent(string name, int rows, int classified)
    {
        var testCase = Document.Value["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var classes = new TrafficCaseRunner(testCase).Run()["classes"]!.AsObject();
        var wanted = testCase["expected"]!["classes"]!.AsObject()
            .Where(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture) is >= 1700 and < 1800)
            .ToList();

        Assert.Equal((rows, classified), (wanted.Count, wanted.Count(pair => pair.Value is not null)));
        foreach (var (pid, want) in wanted)
        {
            Assert.True(want?.GetValue<string>() == classes[pid]?.GetValue<string>(), $"{name} {pid}: got {classes[pid]?.ToJsonString() ?? "null"}");
        }
    }

    [Fact]
    public void OnlyTheNpmPackagePathCounts()
    {
        var mac = new AgentProcessRules(new TrafficPlatform(TrafficPlatformKind.MacOS, "/Users/dev"));
        var windows = new AgentProcessRules(new TrafficPlatform(TrafficPlatformKind.Windows, @"C:\Users\dev"));
        SourceKind? Mac(string exe, params string[] args) => mac.Source(exe, () => args, []);
        SourceKind? Win(string exe, params string[] args) => windows.Source(exe, () => args, []);

        Assert.Equal(SourceKind.CodeBuddyCode, Win(@"C:\Program Files\nodejs\node.exe", "node.exe",
            @"C:\Users\dev\AppData\Roaming\npm\node_modules\@Tencent-AI\CodeBuddy-Code\bin\codebuddy", "-p", "hi"));
        Assert.Equal(SourceKind.CodeBuddyCode, Win(@"C:\Program Files\nodejs\node.exe", "node.exe",
            "C:/Users/dev/AppData/Roaming/npm/node_modules/@tencent-ai/codebuddy-code/bin/codebuddy-lowmem"));
        Assert.Equal(SourceKind.CodeBuddyCode, Mac("/opt/homebrew/bin/bun", "bun", "/opt/homebrew/lib/node_modules/@tencent-ai/codebuddy-code/bin/cbc-prewarm"));
        Assert.True(mac.NeedsArguments("/opt/homebrew/bin/node", () => null));

        Assert.Null(Mac("/opt/homebrew/bin/node", "node", "/opt/homebrew/lib/node_modules/@Tencent-AI/CodeBuddy-Code/bin/codebuddy"));
        Assert.Null(Mac("/Users/dev/.local/bin/codebuddy", "codebuddy"));
        Assert.Null(Win(@"C:\Users\dev\.local\bin\codebuddy.exe", "codebuddy.exe"));
        Assert.Null(Mac("/usr/bin/python3", "python3", "/opt/homebrew/lib/node_modules/@tencent-ai/codebuddy-code/bin/codebuddy"));
        Assert.Null(Mac("/opt/homebrew/bin/node", "node", "/Users/dev/src/codebuddy-code/bin/codebuddy"));
    }

    /// <summary>WorkBuddy's embedded copy of the CLI runs on the app's own executable and stays WorkBuddy's row.</summary>
    [Fact]
    public void WorkBuddysEmbeddedCopyIsWorkBuddy()
    {
        var windows = new AgentProcessRules(new TrafficPlatform(TrafficPlatformKind.Windows, @"C:\Users\dev"));
        var source = windows.Source(@"C:\Users\dev\AppData\Local\Programs\WorkBuddy\WorkBuddy.exe",
            () => ["WorkBuddy.exe", @"C:\Users\dev\AppData\Local\Programs\WorkBuddy\resources\app.asar.unpacked\cli\dist\codebuddy.js"], []);

        Assert.Equal(SourceKind.WorkBuddy, source);
    }
}
