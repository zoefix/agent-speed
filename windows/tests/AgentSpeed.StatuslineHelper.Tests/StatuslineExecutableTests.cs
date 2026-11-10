using System.Diagnostics;
using System.Text;
using AgentSpeed.Core.Quota;

namespace AgentSpeed.StatuslineHelper.Tests;

/// <summary>Runs only on Windows with <c>AGENT_SPEED_STATUSLINE_EXE</c> naming a published helper.</summary>
public sealed class StatuslineExeFactAttribute : FactAttribute
{
    public StatuslineExeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("AGENT_SPEED_STATUSLINE_EXE") is not { Length: > 0 } exe || !File.Exists(exe))
        {
            Skip = "Set AGENT_SPEED_STATUSLINE_EXE to a published agent-speed-statusline.exe (Windows).";
        }
    }
}

/// <summary>
/// The published helper as Claude Code would run it (QUOTAS 5.2 step 3, 5.5): the installed command string
/// <c>"C:/…/agent-speed-statusline.exe"</c> through Git Bash (<c>bash -c</c>) and through <c>cmd /c</c>, and a previous
/// command chained through Git Bash and through cmd.
/// </summary>
public sealed class StatuslineExecutableTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "agent speed statusline " + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _exe;

    public StatuslineExecutableTests()
    {
        _exe = Path.Combine(_data, "bin", "agent-speed-statusline.exe");
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_STATUSLINE_EXE") is { Length: > 0 } source && File.Exists(source))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_exe)!);
            File.Copy(source, _exe);
        }
    }

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

    /// <summary>The command the bridge installs on Windows: the path with <c>/</c> in double quotes (QUOTAS 5.2).</summary>
    private string Command => "\"" + _exe.Replace('\\', '/') + "\"";

    private static string GitBash
    {
        get
        {
            var shell = PreviousCommandRunner.Shell(Environment.GetEnvironmentVariable);
            Assert.False(shell.IsCmd, "Git for Windows must be installed for this test");
            return shell.Executable;
        }
    }

    private static string Input()
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var five = now + 7200;
        var seven = now + 86400 * 3;
        return "{\"model\":{\"id\":\"claude-opus-5-5\"},\"rate_limits\":{\"five_hour\":{\"used_percentage\":23.5,\"resets_at\":" + five
            + "},\"seven_day\":{\"used_percentage\":41.2,\"resets_at\":" + seven + "}}}";
    }

    private static (int Code, string Output) Start(ProcessStartInfo info, string input, IDictionary<string, string?>? environment = null)
    {
        info.UseShellExecute = false;
        info.RedirectStandardInput = true;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.CreateNoWindow = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        foreach (var (key, value) in environment ?? new Dictionary<string, string?>())
        {
            info.Environment[key] = value;
        }

        using var process = Process.Start(info)!;
        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        Assert.True(process.WaitForExit(20_000), "the helper did not finish");
        return (process.ExitCode, output.Result);
    }

    private static ProcessStartInfo Bash(string command)
    {
        var info = new ProcessStartInfo(GitBash);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);
        return info;
    }

    private static ProcessStartInfo Cmd(string command) => new("cmd.exe", "/d /s /c \"" + command + "\"");

    [StatuslineExeFact]
    public void TheInstalledCommandRunsThroughGitBash()
    {
        var (code, output) = Start(Bash(Command), Input());
        Assert.Equal(0, code);
        Assert.Equal("5h 24% · 7d 41%\n", output);
        Assert.True(File.Exists(Path.Combine(_data, "quota", "claude-cli.json")));
    }

    [StatuslineExeFact]
    public void TheInstalledCommandRunsThroughCmd()
    {
        var (code, output) = Start(Cmd(Command), Input());
        Assert.Equal(0, code);
        Assert.Equal("5h 24% · 7d 41%\n", output);
        var stored = ClaudeCliQuota.Read(File.ReadAllBytes(Path.Combine(_data, "quota", "claude-cli.json")));
        Assert.Equal(41.2, stored.SevenDay!.Value.UsedPercentage);
    }

    [StatuslineExeFact]
    public void APreviousCommandChainsThroughGitBash()
    {
        WriteSidecar("printf 'prev %s' \"$(head -c 1)\"; exit 7");
        var (code, output) = Start(Bash(Command), Input());
        Assert.Equal(7, code);
        Assert.Equal("prev {", output);
        Assert.True(File.Exists(Path.Combine(_data, "quota", "claude-cli.json")));
    }

    [StatuslineExeFact]
    public void APreviousCommandChainsThroughClaudeCodesGitBashPath()
    {
        WriteSidecar("echo bash-$((40+2))");
        var (code, output) = Start(Cmd(Command), Input(), new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_GIT_BASH_PATH"] = GitBash,
            ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
        });
        Assert.Equal(0, code);
        Assert.Equal("bash-42\n", output);
    }

    [StatuslineExeFact]
    public void APreviousCommandChainsThroughCmdWithoutGit()
    {
        WriteSidecar("echo prev-cmd& exit /b 5");
        var (code, output) = Start(Cmd(Command), Input(), new Dictionary<string, string?>
        {
            ["CLAUDE_CODE_GIT_BASH_PATH"] = null,
            ["PATH"] = Environment.GetFolderPath(Environment.SpecialFolder.System),
        });
        Assert.Equal(5, code);
        Assert.Equal("prev-cmd\r\n", output);
    }

    [StatuslineExeFact]
    public void TheHelperStartsQuickly()
    {
        Start(Cmd(Command), Input());
        var times = new List<double>();
        for (var i = 0; i < 20; i++)
        {
            var watch = Stopwatch.StartNew();
            var (code, output) = Start(new ProcessStartInfo(_exe), """{"model":{}}""");
            times.Add(watch.Elapsed.TotalMilliseconds);
            Assert.Equal(0, code);
            Assert.StartsWith("5h ", output, StringComparison.Ordinal);
        }

        times.Sort();
        var median = times[times.Count / 2];
        Console.WriteLine($"helper start-to-exit median {median:0.0} ms, min {times[0]:0.0} ms, max {times[^1]:0.0} ms");
        Assert.True(median < 50, $"median {median:0.0} ms");
    }

    private void WriteSidecar(string command)
    {
        Directory.CreateDirectory(Path.Combine(_data, "quota"));
        var sidecar = new System.Text.Json.Nodes.JsonObject
        {
            ["version"] = 1,
            ["statusLine"] = new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["command"] = command },
        };
        File.WriteAllText(Path.Combine(_data, "quota", "claude-statusline-previous.json"), sidecar.ToJsonString());
    }
}
