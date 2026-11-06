using System.Runtime.Versioning;
using System.Text;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The remote scripts (REMOTE 4.2, 4.3) run for real with the local shells: everything they do is POSIX, <c>uname</c> aside.</summary>
[UnsupportedOSPlatform("windows")]
public sealed class RemoteScriptTests
{
    private const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public static TheoryData<string, string, string> HashTools() => new()
    {
        { "sha256sum", "sha256sum", "/sbin/sha256sum" },
        { "shasum -a 256", "shasum", "/usr/bin/shasum" },
        { "openssl dgst -sha256 -r", "openssl", "/usr/bin/openssl" },
    };

    [UnixFact]
    public void ProbeListsSystemToolAndRelayHashes()
    {
        using var directory = new TempDirectory();
        var folder = directory.Combine("relay");
        Directory.CreateDirectory(folder);
        var relay = TestBytes.PseudoRandom(70_001, 3);
        File.WriteAllBytes(Path.Combine(folder, "agent-speed-relay-1.7.0-x86_64-0123456789ab"), relay);
        File.WriteAllText(Path.Combine(folder, "agent-speed-relay-1.6.0-x86_64-ffffffffffff"), "old");
        File.WriteAllText(Path.Combine(folder, "history.sqlite"), "keep");
        Directory.CreateDirectory(Path.Combine(folder, "agent-speed-relay-dir"));

        var result = ShellRunner.Script(RemoteScripts.Probe, [folder]);
        Assert.True(result.Status == 0, result.Stderr);
        var probe = Parse(result.Stdout);
        Assert.Equal(ShellRunner.Run("/usr/bin/uname", ["-s"]).Stdout.Trim(), probe.Kernel);
        Assert.Equal(ShellRunner.Run("/usr/bin/uname", ["-m"]).Stdout.Trim(), probe.Machine);
        Assert.True(probe.IsComplete);
        Assert.NotNull(probe.HashTool);
        Assert.Equal(new Dictionary<string, string>
        {
            ["agent-speed-relay-1.7.0-x86_64-0123456789ab"] = TestBytes.Sha256(relay),
            ["agent-speed-relay-1.6.0-x86_64-ffffffffffff"] = TestBytes.Sha256("old"),
        }, probe.Hashes);
        var expected = OperatingSystem.IsLinux() && RemoteArchNames.FromMachine(probe.Machine) is { } arch
            ? new RemoteProbeVerdict(RemoteProbeVerdictKind.Supported, arch)
            : new RemoteProbeVerdict(RemoteProbeVerdictKind.UnsupportedSystem, System: probe.System);
        Assert.Equal(expected, probe.Verdict);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, "history.sqlite")));
    }

    [UnixFact]
    public void ProbeOfAMissingFolderListsNothing()
    {
        using var directory = new TempDirectory();
        var missing = directory.Combine("missing");
        var result = ShellRunner.Script(RemoteScripts.Probe, [missing]);
        Assert.Equal(0, result.Status);
        var probe = Parse(result.Stdout);
        Assert.True(probe.IsComplete);
        Assert.Empty(probe.Hashes);
        Assert.False(Directory.Exists(missing));
    }

    [UnixTheory]
    [MemberData(nameof(HashTools))]
    public void ProbeAndUploadUseEveryHashTool(string expectedTool, string tool, string target)
    {
        using var directory = new TempDirectory();
        var environment = new Dictionary<string, string>
        {
            ["PATH"] = ToolFolder(directory, new Dictionary<string, string> { [tool] = target }), ["HOME"] = directory.Path,
        };
        var folder = directory.Combine("relay");
        var relay = TestBytes.PseudoRandom(300_000, 4);
        var digest = TestBytes.Sha256(relay);
        var name = "agent-speed-relay-1.7.0-x86_64-" + digest[..12];

        var upload = ShellRunner.Script(RemoteScripts.Upload, [folder, name, digest], stdin: relay, environment: environment);
        Assert.True(upload.Status == 0, upload.Stderr);
        Assert.Equal(digest, RemoteProbeResult.UploadedHash(Encoding.UTF8.GetBytes(upload.Stdout)));

        var probe = ShellRunner.Script(RemoteScripts.Probe, [folder], environment: environment);
        var result = Parse(probe.Stdout);
        Assert.Equal(expectedTool, result.HashTool);
        Assert.True(result.HasInstalled(name, digest));
    }

    [UnixFact]
    public void ProbeWithoutHashToolSaysNone()
    {
        using var directory = new TempDirectory();
        var environment = new Dictionary<string, string> { ["PATH"] = ToolFolder(directory, []) };
        var result = ShellRunner.Script(RemoteScripts.Probe, [directory.Path], environment: environment);
        var probe = Parse(result.Stdout);
        Assert.True(probe.IsComplete);
        Assert.Null(probe.HashTool);

        var folder = directory.Combine("f");
        var upload = ShellRunner.Script(RemoteScripts.Upload, [folder, "agent-speed-relay-x", new string('0', 64)],
            stdin: "x"u8.ToArray(), environment: environment);
        Assert.Equal(RemoteCommand.NoHashToolStatus, upload.Status);
        Assert.Empty(Directory.EnumerateFileSystemEntries(folder));
        Assert.Equal(RemoteStatus.CannotVerify, RemoteStatusClassifier.Classify(upload.Status, Encoding.UTF8.GetBytes(upload.Stderr), RemoteStage.Upload));
    }

    [UnixFact]
    public void UploadVerifiesRenamesAndCleansOnlyRelayFiles()
    {
        using var directory = new TempDirectory();
        var folder = directory.Combine("deep", "relay");
        var relay = TestBytes.PseudoRandom(1_000_003, 5);
        var digest = TestBytes.Sha256(relay);
        var name = "agent-speed-relay-1.7.0-x86_64-" + digest[..12];
        Directory.CreateDirectory(folder, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        foreach (var stale in new[] { "agent-speed-relay-1.6.0-x86_64-aaaaaaaaaaaa", ".upload-123", "agent-speed-relay-junk" })
        {
            File.WriteAllText(Path.Combine(folder, stale), "stale");
        }

        foreach (var kept in new[] { "history.sqlite", "zone.tzif", "notes.txt", "agent-speed-relayX" })
        {
            File.WriteAllText(Path.Combine(folder, kept), "keep");
        }

        Directory.CreateDirectory(Path.Combine(folder, "agent-speed-relay-dir"));

        var result = ShellRunner.Script(RemoteScripts.Upload, [folder, name, digest], stdin: relay);
        Assert.True(result.Status == 0, result.Stderr);
        Assert.Equal($"hash {digest}\n", result.Stdout);
        var installed = Path.Combine(folder, name);
        Assert.Equal(relay, File.ReadAllBytes(installed));
        Assert.Equal(Private, File.GetUnixFileMode(installed));
        Assert.Equal(Private, File.GetUnixFileMode(folder));
        var names = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(new HashSet<string?> { name, "history.sqlite", "zone.tzif", "notes.txt", "agent-speed-relayX", "agent-speed-relay-dir" }, names);
        foreach (var kept in new[] { "history.sqlite", "zone.tzif", "notes.txt", "agent-speed-relayX" })
        {
            Assert.Equal("keep", File.ReadAllText(Path.Combine(folder, kept)));
        }
    }

    [UnixFact]
    public void UploadCreatesTheFolderPrivate()
    {
        using var directory = new TempDirectory();
        var folder = directory.Combine("new", "agent-speed");
        var relay = "relay"u8.ToArray();
        var result = ShellRunner.Script(RemoteScripts.Upload, [folder, "agent-speed-relay-t", TestBytes.Sha256(relay)], stdin: relay);
        Assert.True(result.Status == 0, result.Stderr);
        Assert.Equal(Private, File.GetUnixFileMode(folder));
        Assert.Equal(Private, File.GetUnixFileMode(directory.Combine("new")));
        Assert.Equal(relay, File.ReadAllBytes(Path.Combine(folder, "agent-speed-relay-t")));
    }

    [UnixFact]
    public void UploadOfTheSameFileAgainKeepsIt()
    {
        using var directory = new TempDirectory();
        var folder = directory.Combine("relay");
        var relay = TestBytes.PseudoRandom(10_000, 9);
        var digest = TestBytes.Sha256(relay);
        Assert.Equal(0, ShellRunner.Script(RemoteScripts.Upload, [folder, "agent-speed-relay-t", digest], stdin: relay).Status);
        Assert.Equal(0, ShellRunner.Script(RemoteScripts.Upload, [folder, "agent-speed-relay-t", digest], stdin: relay).Status);
        Assert.Equal(["agent-speed-relay-t"], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        Assert.Equal(relay, File.ReadAllBytes(Path.Combine(folder, "agent-speed-relay-t")));
    }

    [UnixFact]
    public void UploadMismatchDeletesTheCopyAndExits3()
    {
        using var directory = new TempDirectory();
        var folder = directory.Combine("relay");
        Directory.CreateDirectory(folder);
        var previous = Path.Combine(folder, "agent-speed-relay-1.6.0-x86_64-aaaaaaaaaaaa");
        File.WriteAllText(previous, "previous");
        var result = ShellRunner.Script(RemoteScripts.Upload, [folder, "agent-speed-relay-new", new string('0', 64)], stdin: "truncated"u8.ToArray());
        Assert.Equal(RemoteCommand.MismatchStatus, result.Status);
        Assert.Equal(TestBytes.Sha256("truncated"), RemoteProbeResult.UploadedHash(Encoding.UTF8.GetBytes(result.Stdout)));
        Assert.Equal([Path.GetFileName(previous)], Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName));
        Assert.Equal("previous", File.ReadAllText(previous));
        Assert.Equal(RemoteStatus.UploadFailed, RemoteStatusClassifier.Classify(result.Status, [], RemoteStage.Upload));
    }

    [UnixFact]
    public void UploadIntoAFileFailsWith4()
    {
        using var directory = new TempDirectory();
        var blocker = directory.WriteFile("blocker", "x");
        var result = ShellRunner.Script(RemoteScripts.Upload, [blocker, "agent-speed-relay-t", TestBytes.Sha256("relay")], stdin: "relay"u8.ToArray());
        Assert.Equal(RemoteCommand.WriteFailedStatus, result.Status);
        Assert.Equal("x", File.ReadAllText(blocker));
        Assert.Equal(RemoteStatus.UploadFailed, RemoteStatusClassifier.Classify(result.Status, [], RemoteStage.Upload));
    }

    [UnixFact]
    public void RunExecsTheRelayWithItsFolderAndMakesTheFolderPrivate()
    {
        using var directory = new TempDirectory();
        var folder = directory.Combine("relay");
        Directory.CreateDirectory(folder, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var relay = Path.Combine(folder, "agent-speed-relay-t");
        File.WriteAllText(relay, "#!/bin/sh\necho \"argv: $*\"\nread line\necho \"got $line\"\n");
        File.SetUnixFileMode(relay, Private);
        var result = ShellRunner.Script(RemoteScripts.Run, [folder, "agent-speed-relay-t"], stdin: "start\n"u8.ToArray());
        Assert.True(result.Status == 0, result.Stderr);
        Assert.Equal($"argv: serve --folder {folder}\ngot start\n", result.Stdout);
        Assert.Equal(Private, File.GetUnixFileMode(folder));
    }

    [UnixFact]
    public void RunOfANonExecutableFileIsNotExecutable()
    {
        using var directory = new TempDirectory();
        var relay = directory.WriteFile("agent-speed-relay-t", "#!/bin/sh\necho hi\n");
        File.SetUnixFileMode(relay, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        foreach (var shell in new[] { "/bin/sh", "/bin/dash", "/bin/bash" }.Where(File.Exists))
        {
            var result = ShellRunner.Script(RemoteScripts.Run, [directory.Path, "agent-speed-relay-t"], shell);
            Assert.True(result.Status == 126, $"{shell}: {result.Status}");
            Assert.Equal(string.Empty, result.Stdout);
            Assert.Equal(RemoteStatus.NotExecutable,
                RemoteStatusClassifier.Classify(result.Status, Encoding.UTF8.GetBytes(result.Stderr), RemoteStage.Run));
        }
    }

    [UnixFact]
    public void RunOfAMissingFileExits126()
    {
        using var directory = new TempDirectory();
        var result = ShellRunner.Script(RemoteScripts.Run, [directory.Path, "agent-speed-relay-missing"]);
        Assert.Equal(126, result.Status);
        Assert.Equal(string.Empty, result.Stdout);
    }

    /// <summary>A relay file the user does not own (here a link to a system tool) fails <c>[ -O ]</c>: nothing runs.</summary>
    [UnixFact]
    public void RunOfAFileOfAnotherOwnerExits126()
    {
        if (Environment.UserName == "root")
        {
            return;
        }

        using var directory = new TempDirectory();
        var folder = directory.Combine("relay");
        Directory.CreateDirectory(folder);
        File.CreateSymbolicLink(Path.Combine(folder, "agent-speed-relay-t"), "/bin/echo");
        var result = ShellRunner.Script(RemoteScripts.Run, [folder, "agent-speed-relay-t"]);
        Assert.Equal(126, result.Status);
        Assert.Equal(string.Empty, result.Stdout);
        Assert.Equal(RemoteStatus.NotExecutable, RemoteStatusClassifier.Classify(result.Status, [], RemoteStage.Run));
    }

    /// <summary>
    /// The whole remote command through each login shell: the folder's <c>~</c> expands, the script stays intact and the
    /// arguments arrive as <c>$1</c> ….
    /// </summary>
    [UnixTheory]
    [InlineData("/bin/sh")]
    [InlineData("/bin/bash")]
    [InlineData("/bin/zsh")]
    [InlineData("/bin/dash")]
    [InlineData("/bin/ksh")]
    [InlineData("/bin/tcsh")]
    [InlineData("/bin/csh")]
    public void RemoteCommandsThroughLoginShells(string loginShell)
    {
        if (!File.Exists(loginShell))
        {
            return;
        }

        using var home = new TempDirectory();
        var environment = new Dictionary<string, string> { ["HOME"] = home.Path, ["PATH"] = "/usr/bin:/bin:/usr/sbin:/sbin" };
        var relay = Encoding.UTF8.GetBytes("#!/bin/sh\necho \"relay $*\"\n");
        var digest = TestBytes.Sha256(relay);
        var name = "agent-speed-relay-1.7.0-x86_64-" + digest[..12];

        var upload = RemoteCommand.Upload("~/.cache/agent-speed", name, digest);
        Assert.NotNull(upload);
        var uploaded = ShellRunner.Run(loginShell, ["-c", upload], relay, environment);
        Assert.True(uploaded.Status == 0, $"{loginShell}: {uploaded.Stderr}");
        var folder = home.Combine(".cache", "agent-speed");
        Assert.True(File.Exists(Path.Combine(folder, name)), loginShell);

        var probe = RemoteCommand.Probe("~/.cache/agent-speed");
        Assert.NotNull(probe);
        var probed = ShellRunner.Run(loginShell, ["-c", probe], environment: environment);
        Assert.True(Parse(probed.Stdout).HasInstalled(name, digest), $"{loginShell}: {probed.Stdout} {probed.Stderr}");

        var run = RemoteCommand.Run("~/.cache/agent-speed", name);
        Assert.NotNull(run);
        var ran = ShellRunner.Run(loginShell, ["-c", run], environment: environment);
        Assert.True($"relay serve --folder {folder}\n" == ran.Stdout, $"{loginShell}: {ran.Stdout} {ran.Stderr}");
    }

    /// <summary>Hostile-looking but valid values stay literal: nothing beyond the fixed script runs.</summary>
    [UnixFact]
    public void ValidatedArgumentsCannotInject()
    {
        using var home = new TempDirectory();
        var environment = new Dictionary<string, string> { ["HOME"] = home.Path, ["PATH"] = "/usr/bin:/bin" };
        var probe = RemoteCommand.Probe("~/a-b_c.d/~e");
        Assert.NotNull(probe);
        var result = ShellRunner.Run(File.Exists("/bin/zsh") ? "/bin/zsh" : "/bin/sh", ["-c", probe], environment: environment);
        Assert.Equal(0, result.Status);
        Assert.True(Parse(result.Stdout).IsComplete);
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Path));
        foreach (var hostile in new[] { "~/x;touch pwned", "~/$(touch pwned)", "~/`touch pwned`", "~/x' ; touch pwned; '", "~/x\ntouch pwned" })
        {
            Assert.Null(RemoteCommand.Probe(hostile));
            Assert.Null(RemoteCommand.Run(hostile, "agent-speed-relay-t"));
            Assert.Null(RemoteCommand.Upload(hostile, "agent-speed-relay-t", new string('0', 64)));
        }
    }

    private static RemoteProbeResult Parse(string stdout) => RemoteProbeResult.Parse(Encoding.UTF8.GetBytes(stdout));

    /// <summary>A PATH folder with only these tools (and the basics the scripts use), to force each hash tool in turn.</summary>
    private static string ToolFolder(TempDirectory directory, Dictionary<string, string> hashTools)
    {
        var bin = directory.Combine("bin");
        Directory.CreateDirectory(bin);
        var tools = new Dictionary<string, string>
        {
            ["uname"] = "/usr/bin/uname", ["cat"] = "/bin/cat", ["mkdir"] = "/bin/mkdir", ["chmod"] = "/bin/chmod", ["mv"] = "/bin/mv",
            ["rm"] = "/bin/rm",
        };
        foreach (var (name, target) in hashTools)
        {
            tools[name] = target;
        }

        foreach (var (name, target) in tools)
        {
            File.CreateSymbolicLink(Path.Combine(bin, name), File.Exists(target) ? target : Path.Combine("/usr/bin", Path.GetFileName(target)));
        }

        return bin;
    }
}
