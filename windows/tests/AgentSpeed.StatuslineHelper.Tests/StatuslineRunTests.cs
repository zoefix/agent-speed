using System.Text;
using AgentSpeed.Core.Quota;

namespace AgentSpeed.StatuslineHelper.Tests;

/// <summary>The helper's run in-process (QUOTAS 5.5): merge, quota file, line, previous command.</summary>
public sealed class StatuslineRunTests : IDisposable
{
    private const long Now = 1794338773000;

    private readonly string _data = Path.Combine(Path.GetTempPath(), "agent-speed-statusline-" + Guid.NewGuid().ToString("N"));

    public StatuslineRunTests() => Directory.CreateDirectory(_data);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_data, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string QuotaFile => Path.Combine(_data, "quota", "claude-cli.json");

    private (int Code, string Output) Run(string input, Func<string, string?>? environment = null)
    {
        using var stdin = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var stdout = new MemoryStream();
        var code = new StatuslineRun(_data, () => Now, environment).Run(stdin, stdout);
        return (code, Encoding.UTF8.GetString(stdout.ToArray()));
    }

    private const string WithLimits =
        """{"session_id":"s","model":{"id":"claude-opus-5-5"},"rate_limits":{"five_hour":{"used_percentage":6,"resets_at":1794354000},"seven_day":{"used_percentage":86,"resets_at":1794448800}}}""";

    [Fact]
    public void MergesTheLimitsAndPrintsTheLine()
    {
        var (code, output) = Run(WithLimits);
        Assert.Equal(0, code);
        Assert.Equal("5h 6% · 7d 86%\n", output);
        var stored = ClaudeCliQuota.Read(File.ReadAllBytes(QuotaFile));
        Assert.Equal(Now, stored.ReceivedMs);
        Assert.Equal(new CliWindowValue(86, 1794448800), stored.SevenDay);
    }

    [Fact]
    public void InputWithAByteOrderMarkStillMerges()
    {
        using var stdin = new MemoryStream([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(WithLimits)]);
        using var stdout = new MemoryStream();
        Assert.Equal(0, new StatuslineRun(_data, () => Now).Run(stdin, stdout));
        Assert.Equal("5h 6% · 7d 86%\n", Encoding.UTF8.GetString(stdout.ToArray()));
    }

    [Fact]
    public void InputWithoutLimitsKeepsTheStoredLine()
    {
        Run(WithLimits);
        var before = File.GetLastWriteTimeUtc(QuotaFile);
        var (code, output) = Run("""{"model":{"id":"claude-opus-5-5"}}""");
        Assert.Equal(0, code);
        Assert.Equal("5h 6% · 7d 86%\n", output);
        Assert.Equal(before, File.GetLastWriteTimeUtc(QuotaFile));
    }

    [Fact]
    public void NothingIsPrintedWithoutAQuota()
    {
        Assert.Equal((0, string.Empty), Run("""{"model":{}}"""));
        Assert.Equal((0, string.Empty), Run("not json"));
        Assert.Equal((0, string.Empty), Run(string.Empty));
        Assert.False(File.Exists(QuotaFile));
    }

    [Fact]
    public void ThePreviousCommandGetsTheSameInput()
    {
        Directory.CreateDirectory(Path.Combine(_data, "quota"));
        File.WriteAllText(Path.Combine(_data, "quota", "claude-statusline-previous.json"),
            """{"version":1,"statusLine":{"type":"command","command":"cat","padding":0}}""");
        var (code, output) = Run(WithLimits);
        Assert.Equal(0, code);
        Assert.Equal(WithLimits, output);
        Assert.True(File.Exists(QuotaFile));
    }

    [Fact]
    public void ASidecarWithoutACommandPrintsTheLine()
    {
        Directory.CreateDirectory(Path.Combine(_data, "quota"));
        File.WriteAllText(Path.Combine(_data, "quota", "claude-statusline-previous.json"), """{"version":1}""");
        Assert.Equal("5h 6% · 7d 86%\n", Run(WithLimits).Output);
    }

    [Fact]
    public void TheDataFolderIsTheParentOfBin()
    {
        var exe = Path.Combine(_data, "bin", "agent-speed-statusline.exe");
        Assert.Equal(_data, StatuslineRun.DataFolderFor(exe));
        Assert.EndsWith("AgentSpeed", StatuslineRun.DataFolderFor(Path.Combine(_data, "app", "x.exe")).Replace("agent-speed", "AgentSpeed", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void WindowsPrefersClaudeCodesGitBashThenGitsBashThenCmd()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal("/bin/sh", PreviousCommandRunner.Shell(_ => null).Executable);
            return;
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"D:\Tools\Git\bin\bash.exe", @"C:\Program Files\Git\cmd\git.exe", @"C:\Program Files\Git\bin\bash.exe", @"C:\Windows\system32\cmd.exe",
        };
        string? Env(string name, string? bashPath) => name switch
        {
            "CLAUDE_CODE_GIT_BASH_PATH" => bashPath,
            "PATH" => @"C:\Windows\system32;""C:\Program Files\Git\cmd""",
            "ComSpec" => @"C:\Windows\system32\cmd.exe",
            _ => null,
        };

        Assert.Equal(new StatuslineShell(@"D:\Tools\Git\bin\bash.exe", false),
            PreviousCommandRunner.Shell(n => Env(n, @"D:\Tools\Git\bin\bash.exe"), files.Contains));
        Assert.Equal(new StatuslineShell(@"C:\Program Files\Git\bin\bash.exe", false),
            PreviousCommandRunner.Shell(n => Env(n, @"D:\missing\bash.exe"), files.Contains));
        files.Remove(@"C:\Program Files\Git\bin\bash.exe");
        Assert.Equal(new StatuslineShell(@"C:\Windows\system32\cmd.exe", true), PreviousCommandRunner.Shell(n => Env(n, null), files.Contains));
    }

    [Fact]
    public void CmdGetsTheCommandAsOneQuotedString()
    {
        var info = PreviousCommandRunner.StartInfo(new StatuslineShell("cmd.exe", true), "\"C:/a b/line.exe\" --x");
        Assert.Equal("/d /s /c \"\"C:/a b/line.exe\" --x\"", info.Arguments);
        var bash = PreviousCommandRunner.StartInfo(new StatuslineShell("bash.exe", false), "~/line.sh");
        Assert.Equal(["-c", "~/line.sh"], bash.ArgumentList);
    }
}
