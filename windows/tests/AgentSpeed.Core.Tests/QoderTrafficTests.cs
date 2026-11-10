using System.Globalization;
using System.Text.Json.Nodes;
using AgentSpeed.Core.Traffic.Engine;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// The Qoder rows of METRICS 10.5 in <c>traffic-cases.json</c> (processes 1600 and up of both classify cases): the IDE's agent core
/// in <c>resources/bin/&lt;platform&gt;</c> and Qoder CLI (native or npm) are agents; the IDE's own executable and its helpers are not.
/// </summary>
public sealed class QoderTrafficTests
{
    private static readonly Lazy<JsonObject> Document = new(() =>
        (JsonObject)JsonNode.Parse(File.ReadAllText(Path.Combine(FixtureLocator.FixturesDirectory, "traffic-cases.json")))!);

    [Theory]
    [InlineData("classify-macos", 8, 5)]
    [InlineData("classify-windows", 4, 3)]
    public void TheAgentCoreAndTheCliAreAgentsAndTheIdeIsNot(string name, int rows, int agents)
    {
        var testCase = Document.Value["cases"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!.AsObject();
        var classes = new TrafficCaseRunner(testCase).Run()["classes"]!.AsObject();
        var wanted = testCase["expected"]!["classes"]!.AsObject()
            .Where(pair => int.Parse(pair.Key, CultureInfo.InvariantCulture) is >= 1600 and < 1700)
            .ToList();

        Assert.Equal((rows, agents), (wanted.Count, wanted.Count(pair => pair.Value is not null)));
        foreach (var (pid, want) in wanted)
        {
            Assert.True(want?.GetValue<string>() == classes[pid]?.GetValue<string>(), $"{name} {pid}: got {classes[pid]?.ToJsonString() ?? "null"}");
        }
    }

    [Fact]
    public void OnlyTheCoreNamedQoderInTheIdesBinFolderIsQoder()
    {
        var mac = new AgentProcessRules(new TrafficPlatform(TrafficPlatformKind.MacOS, "/Users/dev"));
        var windows = new AgentProcessRules(new TrafficPlatform(TrafficPlatformKind.Windows, @"C:\Users\dev"));
        SourceKind? Mac(string exe, params string[] args) => mac.Source(exe, () => args, []);
        SourceKind? Win(string exe, params string[] args) => windows.Source(exe, () => args, []);

        Assert.Equal(SourceKind.Qoder, Mac("/Applications/Qoder IDE.app/Contents/Resources/app/resources/bin/x86_64_darwin/Qoder"));
        Assert.Null(Mac("/Applications/Qoder IDE.app/Contents/MacOS/Qoder"));
        Assert.True(mac.IsDesktopApp("/Applications/Qoder IDE.app/Contents/MacOS/Qoder"));
        Assert.Null(Mac("/Applications/Qoder IDE.app/Contents/Resources/app/resources/bin/x86_64_darwin/qoder"));
        Assert.Equal(SourceKind.Qoder, Win(@"D:\Apps\Qoder\resources\app\resources\bin\x86_64_windows\QODER.EXE"));
        Assert.Null(Win(@"C:\Users\dev\AppData\Local\Programs\Qoder\Qoder.exe"));
        Assert.Equal(SourceKind.QoderCli, Win(@"C:\Users\dev\.local\bin\qodercli.exe", "qodercli.exe", "-p", "hi"));
        Assert.Equal(SourceKind.QoderCli, Mac("/opt/homebrew/bin/bun", "bun", "/opt/homebrew/lib/node_modules/@qodercn-ai/qoderclicn/bundle/qodercli.js"));
        Assert.Null(Mac("/opt/homebrew/bin/node", "node", "/opt/homebrew/lib/node_modules/@qoder-ai/other/index.js"));
    }
}
