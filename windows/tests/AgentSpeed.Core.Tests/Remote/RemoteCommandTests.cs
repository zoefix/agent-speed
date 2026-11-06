using System.Text;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The <c>ssh</c> argument vector, the remote commands and the probe's output (REMOTE 4.1–4.3).</summary>
public sealed class RemoteCommandTests
{
    private static readonly string Digest = string.Concat(Enumerable.Repeat("ab", 32));

    /// <summary>The scripts exactly as the macOS app (RemoteScripts.swift) sends them.</summary>
    private const string SelectHashTool =
        "if command -v sha256sum >/dev/null 2>&1; then h=sha256sum; "
        + "elif command -v shasum >/dev/null 2>&1; then h=\"shasum -a 256\"; "
        + "elif command -v openssl >/dev/null 2>&1; then h=\"openssl dgst -sha256 -r\"; "
        + "else h=; fi";

    private const string MacProbe =
        "echo \"system $(uname -s -m)\"; " + SelectHashTool + "; "
        + "if [ -z \"$h\" ]; then echo \"tool none\"; echo end; exit 0; fi; "
        + "echo \"tool $h\"; "
        + "for f in \"$1\"/agent-speed-relay-*; do if [ -f \"$f\" ]; then x=$($h < \"$f\") && echo \"hash ${x%% *} ${f##*/}\"; fi; done; "
        + "echo end";

    private const string MacUpload =
        "umask 077; mkdir -p \"$1\" || exit 4; chmod 700 \"$1\" || exit 4; t=\"$1/.upload-$$\"; "
        + "if cat > \"$t\" && chmod 700 \"$t\"; then :; else rm -f \"$t\"; exit 4; fi; "
        + SelectHashTool + "; "
        + "if [ -z \"$h\" ]; then rm -f \"$t\"; exit 5; fi; x=$($h < \"$t\"); x=${x%% *}; echo \"hash $x\"; "
        + "if [ \"$x\" = \"$3\" ]; then :; else rm -f \"$t\"; exit 3; fi; "
        + "if mv -f \"$t\" \"$1/$2\"; then :; else rm -f \"$t\"; exit 4; fi; "
        + "for f in \"$1\"/agent-speed-relay-* \"$1\"/.upload-*; do if [ \"$f\" = \"$1/$2\" ]; then :; elif [ -f \"$f\" ]; then rm -f \"$f\"; fi; done; "
        + "exit 0";

    private const string MacRun = "chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\"";

    [Fact]
    public void ArgumentVectorIsExactlyTheSpecs()
    {
        var arguments = RemoteSsh.Arguments("user@devbox", "true");
        Assert.NotNull(arguments);
        Assert.Equal(
        [
            "-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=15", "-o", "ServerAliveInterval=15",
            "-o", "ServerAliveCountMax=3", "-o", "Compression=yes", "-o", "ControlMaster=no",
            "-o", "ClearAllForwardings=yes", "-o", "ForwardAgent=no", "-o", "ForwardX11=no",
            "-o", "PermitLocalCommand=no", "-o", "RequestTTY=no", "-o", "LogLevel=ERROR",
            "--", "user@devbox", "true",
        ], arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("StrictHostKeyChecking", StringComparison.Ordinal));
        Assert.Equal("--", arguments[^3]);
        Assert.Equal("-T", arguments[0]);
    }

    [Fact]
    public void ConfigFileGoesBeforeTheDoubleDash()
    {
        var config = OperatingSystem.IsWindows() ? @"C:\tests\ssh_config" : "/tmp/tests/ssh_config";
        var arguments = RemoteSsh.Arguments("devbox", "true", config);
        Assert.NotNull(arguments);
        Assert.Equal([.. RemoteSsh.Options, "-F", config, "--", "devbox", "true"], arguments);
    }

    [Fact]
    public void ConfigFileComesOnlyFromTheDebugHookAndMustBeFullyQualified()
    {
        var absolute = OperatingSystem.IsWindows() ? @"C:\tests\ssh_config" : "/tmp/tests/ssh_config";
        var expected = IsDebugBuild ? absolute : null;
        Assert.Equal("AGENT_SPEED_SSH_CONFIG", RemoteSsh.ConfigFileVariable);
        Assert.Equal(expected, RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = absolute }));
        Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string>()));
        Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = "" }));
        Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = "ssh_config" }));
        Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = "tests/ssh_config" }));
        Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = "~/.ssh/config" }));
        if (OperatingSystem.IsWindows())
        {
            Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = @"\tests\ssh_config" }));
            Assert.Null(RemoteSsh.DevelopmentConfigFile(new Dictionary<string, string> { ["AGENT_SPEED_SSH_CONFIG"] = @"C:tests\ssh_config" }));
        }
    }

    [Fact]
    public void InvalidDestinationsGetNoArguments()
    {
        Assert.Null(RemoteSsh.Arguments("-oProxyCommand=touch /tmp/x", "true"));
        Assert.Null(RemoteSsh.Arguments("-oProxyCommand=x", "true"));
        Assert.Null(RemoteSsh.Arguments("host;rm", "true"));
        Assert.Null(RemoteSsh.Arguments("", "true"));
        Assert.Null(RemoteSsh.Arguments("host name", "true"));
        Assert.Null(RemoteSsh.Arguments("hôst", "true"));
    }

    [Fact]
    public void ExecutableIsTheSystemOpenSshClient()
    {
        var root = OperatingSystem.IsWindows() ? @"D:\Win" : "/win";
        Assert.Equal(Path.Combine(root, "System32", "OpenSSH", "ssh.exe"),
            RemoteSsh.Executable(new Dictionary<string, string> { ["SystemRoot"] = root }));

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var fallback = Path.Combine(windows.Length > 0 ? windows : @"C:\Windows", "System32", "OpenSSH", "ssh.exe");
        Assert.Equal(fallback, RemoteSsh.Executable(new Dictionary<string, string>()));
        Assert.Equal(fallback, RemoteSsh.Executable(new Dictionary<string, string> { ["SystemRoot"] = "Windows" }));
        Assert.Equal(fallback, RemoteSsh.Executable(new Dictionary<string, string> { ["SystemRoot"] = "" }));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(fallback, RemoteSsh.Executable(new Dictionary<string, string> { ["SystemRoot"] = @"\Windows" }));
            Assert.Equal(fallback, RemoteSsh.Executable(new Dictionary<string, string> { ["SystemRoot"] = "D:Windows" }));
            Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "OpenSSH", "ssh.exe"),
                RemoteSsh.Executable(RemoteSsh.ProcessEnvironment()));
            Assert.True(Path.IsPathFullyQualified(RemoteSsh.Executable(RemoteSsh.ProcessEnvironment())));
        }
    }

    [Fact]
    public void EnvironmentAddsCLocale()
    {
        var environment = RemoteSsh.Environment(new Dictionary<string, string>
        {
            ["HOME"] = "/Users/me", ["LC_ALL"] = "zh_TW.UTF-8", ["LANG"] = "zh_TW.UTF-8",
        });
        Assert.Equal("C", environment["LC_ALL"]);
        Assert.Equal("/Users/me", environment["HOME"]);
        Assert.Equal("zh_TW.UTF-8", environment["LANG"]);
        Assert.Equal("C", RemoteSsh.Environment(new Dictionary<string, string> { ["lc_all"] = "x" })["LC_ALL"]);
        Assert.Single(RemoteSsh.Environment(new Dictionary<string, string> { ["lc_all"] = "x" }));
    }

    [Fact]
    public void MachinesMapToArchitectures()
    {
        Assert.Equal(RemoteArch.X86_64, RemoteArchNames.FromMachine("x86_64"));
        Assert.Equal(RemoteArch.X86_64, RemoteArchNames.FromMachine("amd64"));
        Assert.Equal(RemoteArch.X86_64, RemoteArchNames.FromMachine("AMD64"));
        Assert.Equal(RemoteArch.Aarch64, RemoteArchNames.FromMachine("aarch64"));
        Assert.Equal(RemoteArch.Aarch64, RemoteArchNames.FromMachine("arm64"));
        Assert.Null(RemoteArchNames.FromMachine("armv7l"));
        Assert.Null(RemoteArchNames.FromMachine("riscv64"));
        Assert.Null(RemoteArchNames.FromMachine("i686"));
        Assert.Null(RemoteArchNames.FromMachine(""));
        Assert.Equal("x86_64", RemoteArch.X86_64.Name());
        Assert.Equal("aarch64", RemoteArch.Aarch64.Name());
    }

    [Fact]
    public void RelayFileNames()
    {
        Assert.Equal("agent-speed-relay-linux-x86_64", RemoteRelayName.Bundled(RemoteArch.X86_64));
        Assert.Equal("agent-speed-relay-linux-aarch64", RemoteRelayName.Bundled(RemoteArch.Aarch64));
        Assert.Equal("agent-speed-relay-1.7.0-aarch64-abababababab",
            RemoteRelayName.Installed("1.7.0", RemoteArch.Aarch64, Digest.ToUpperInvariant()));
        Assert.Equal("agent-speed-relay-1.7.0-b-1-x-x86_64-abababababab", RemoteRelayName.Installed("1.7.0+b 1;x", RemoteArch.X86_64, Digest));
        Assert.Equal("agent-speed-relay-0-x86_64-abababababab", RemoteRelayName.Installed("", RemoteArch.X86_64, Digest));
        Assert.Equal("agent-speed-relay-1.7.0---x86_64-abababababab", RemoteRelayName.Installed("1.7.0é", RemoteArch.X86_64, Digest));
        Assert.True(RemoteRelayName.IsSafe(RemoteRelayName.Installed("$(id)`x`'\"", RemoteArch.X86_64, Digest)));
        Assert.True(RemoteRelayName.IsSafe("agent-speed-relay-1.7.0-x86_64-abababababab"));
        Assert.False(RemoteRelayName.IsSafe("a b"));
        Assert.False(RemoteRelayName.IsSafe("../x"));
        Assert.False(RemoteRelayName.IsSafe("-x"));
        Assert.False(RemoteRelayName.IsSafe(""));
        Assert.False(RemoteRelayName.IsSafe(".."));
        Assert.False(RemoteRelayName.IsSafe("a;b"));
        Assert.False(RemoteRelayName.IsSafe(new string('a', 256)));
    }

    [Fact]
    public void ScriptsAreTheMacOsStrings()
    {
        Assert.Equal(MacProbe, RemoteScripts.Probe);
        Assert.Equal(MacUpload, RemoteScripts.Upload);
        Assert.Equal(MacRun, RemoteScripts.Run);
    }

    [Fact]
    public void CommandsAreFixedScriptsWithValidatedArguments()
    {
        Assert.Equal($"sh -c '{RemoteScripts.Probe}' agent-speed ~/.cache/agent-speed", RemoteCommand.Probe("~/.cache/agent-speed"));
        Assert.Equal($"sh -c '{RemoteScripts.Upload}' agent-speed /opt/as agent-speed-relay-1-x86_64-ab {Digest}",
            RemoteCommand.Upload("/opt/as/", "agent-speed-relay-1-x86_64-ab", Digest.ToUpperInvariant()));
        Assert.Equal(
            "sh -c 'chmod 700 \"$1\" 2>/dev/null && [ -O \"$1/$2\" ] || exit 126; exec \"$1/$2\" serve --folder \"$1\"' agent-speed "
            + "~/.cache/agent-speed agent-speed-relay-1-x86_64-ab",
            RemoteCommand.Run("~/.cache/agent-speed", "agent-speed-relay-1-x86_64-ab"));
        Assert.Equal($"sh -c '{RemoteScripts.Probe}' agent-speed ~", RemoteCommand.Probe("~/"));
    }

    [Fact]
    public void CommandsRefuseUnvalidatedValues()
    {
        Assert.Null(RemoteCommand.Probe("~/x; rm -rf ~"));
        Assert.Null(RemoteCommand.Probe("~/$(id)"));
        Assert.Null(RemoteCommand.Probe("~/'x'"));
        Assert.Null(RemoteCommand.Probe("~/../x"));
        Assert.Null(RemoteCommand.Probe("relative"));
        Assert.Null(RemoteCommand.Run("~/x", "a;b"));
        Assert.Null(RemoteCommand.Run("~/x", "a b"));
        Assert.Null(RemoteCommand.Run("~/x", "../a"));
        Assert.Null(RemoteCommand.Run("~/x", "-a"));
        Assert.Null(RemoteCommand.Run("~/x y", "a"));
        Assert.Null(RemoteCommand.Upload("~/x", "agent-speed-relay-x", "abc"));
        Assert.Null(RemoteCommand.Upload("~/x", "agent-speed-relay-x", new string('g', 64)));
        Assert.Null(RemoteCommand.Upload("~/x", "agent-speed-relay-x", Digest + " ; id"));
        Assert.Null(RemoteCommand.Upload("~/x", "a b", Digest));
        Assert.Null(RemoteCommand.Upload("~/x;id", "agent-speed-relay-x", Digest));
    }

    [Fact]
    public void ScriptsSurviveEveryLoginShellsSingleQuotes()
    {
        foreach (var script in new[] { RemoteScripts.Probe, RemoteScripts.Upload, RemoteScripts.Run })
        {
            Assert.True(RemoteScripts.IsQuotable(script));
            Assert.DoesNotContain("'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\\", script, StringComparison.Ordinal);
            Assert.DoesNotContain("!", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\r", script, StringComparison.Ordinal);
        }

        Assert.False(RemoteScripts.IsQuotable("echo 'x'"));
        Assert.False(RemoteScripts.IsQuotable("echo x\\"));
        Assert.False(RemoteScripts.IsQuotable("echo !!"));
        Assert.False(RemoteScripts.IsQuotable("echo\rx"));
        Assert.Throws<InvalidOperationException>(() => RemoteCommand.Command("echo 'x'", "a"));
    }

    [Fact]
    public void ProbeOutputParses()
    {
        const string name = "agent-speed-relay-1.7.0-x86_64-abababababab";
        var output = $"""
            Welcome banner
            system Linux x86_64
            tool shasum -a 256
            hash {Digest.ToUpperInvariant()} {name}
            hash {Digest} agent-speed-relay-weird name
            hash {Digest} other-file
            hash {Digest} agent-speed-relay-$(x)
            hash nothex agent-speed-relay-x
            hash {Digest}
            end

            """;
        var result = RemoteProbeResult.Parse(Encoding.UTF8.GetBytes(output));
        Assert.Equal("Linux x86_64", result.System);
        Assert.Equal("Linux", result.Kernel);
        Assert.Equal("x86_64", result.Machine);
        Assert.Equal("shasum -a 256", result.HashTool);
        Assert.Equal(new Dictionary<string, string> { [name] = Digest }, result.Hashes);
        Assert.True(result.IsComplete);
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.Supported, RemoteArch.X86_64), result.Verdict);
        Assert.True(result.HasInstalled(name, Digest.ToUpperInvariant()));
        Assert.False(result.HasInstalled(name, new string('0', 64)));
        Assert.False(result.HasInstalled("agent-speed-relay-other", Digest));
    }

    [Fact]
    public void ProbeVerdicts()
    {
        static RemoteProbeVerdict Verdict(string text) => RemoteProbeResult.Parse(Encoding.UTF8.GetBytes(text)).Verdict;

        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.UnsupportedSystem, System: "Darwin arm64"),
            Verdict("system Darwin arm64\ntool shasum -a 256\nend\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.UnsupportedSystem, System: "Linux riscv64"),
            Verdict("system Linux riscv64\ntool sha256sum\nend\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.CannotVerify), Verdict("system Linux aarch64\ntool none\nend\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.Supported, RemoteArch.Aarch64),
            Verdict("system Linux arm64\ntool openssl dgst -sha256 -r\nend\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.Malformed), Verdict("system Linux x86_64\ntool sha256sum\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.Malformed), Verdict(""));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.Malformed), Verdict("tool sha256sum\nend\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.Supported, RemoteArch.X86_64),
            Verdict("system Linux\u0007 amd64\r\ntool sha256sum\r\nend\r\n"));
        Assert.Equal(new RemoteProbeVerdict(RemoteProbeVerdictKind.UnsupportedSystem, System: "Linux"), Verdict("system Linux\ntool sha256sum\nend\n"));
    }

    [Fact]
    public void ProbeSystemTextIsPrintableAndBounded()
    {
        var result = RemoteProbeResult.Parse(Encoding.UTF8.GetBytes("system \u001b[31mLinux\u202E " + new string('x', 100) + "\nend\n"));
        Assert.Equal(64, result.System.Length);
        Assert.All(result.System, c => Assert.InRange(c, ' ', '~'));
        Assert.StartsWith("[31mLinux ", result.System, StringComparison.Ordinal);
    }

    [Fact]
    public void ProbeReadsOnlyTheFirst64KiB()
    {
        var result = RemoteProbeResult.Parse(Encoding.UTF8.GetBytes(new string('x', 70_000) + "\nsystem Linux x86_64\ntool sha256sum\nend\n"));
        Assert.False(result.IsComplete);
        Assert.Equal(RemoteProbeVerdictKind.Malformed, result.Verdict.Kind);
    }

    [Fact]
    public void UploadedHashIsTheScriptsLine()
    {
        Assert.Equal(Digest, RemoteProbeResult.UploadedHash(Encoding.UTF8.GetBytes($"noise\nhash {Digest.ToUpperInvariant()}\n")));
        Assert.Null(RemoteProbeResult.UploadedHash(Encoding.UTF8.GetBytes("hash short\n")));
        Assert.Null(RemoteProbeResult.UploadedHash(Encoding.UTF8.GetBytes($"hash {Digest} extra\n")));
        Assert.Null(RemoteProbeResult.UploadedHash([]));
    }

    private static bool IsDebugBuild =>
#if DEBUG
        true;
#else
        false;
#endif
}
