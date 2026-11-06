using System.Text;
using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>How a command's end maps to a host status (REMOTE 4.5) and the status groups of the life cycle (4.4).</summary>
public sealed class RemoteStatusTests
{
    [Fact]
    public void SshFailures()
    {
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: Could not resolve hostname devbox: nodename nor servname provided"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: connect to host 10.0.0.1 port 22: Operation timed out"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: connect to host h port 22: Connection refused"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: connect to host h port 22: No route to host"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: connect to host h port 22: Network is unreachable"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "Connection closed by 10.0.0.1 port 22"));
        Assert.Equal(RemoteStatus.AuthFailed, Classify(255, "user@h: Permission denied (publickey,password).", RemoteStage.Probe));
        Assert.Equal(RemoteStatus.AuthFailed, Classify(255, "user@h: Permission denied (publickey).", RemoteStage.Upload));
        Assert.Equal(RemoteStatus.HostKeyUnknown, Classify(255, "Host key verification failed."));
        Assert.Equal(RemoteStatus.HostKeyUnknown, Classify(255, "Host key verification failed.", RemoteStage.Probe));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "kex_exchange_identification: read: Connection reset by peer"));

        // A connect failure is never a refused login, whatever its reason (a firewall's WSAEACCES reads "Permission denied").
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: connect to host 192.0.2.10 port 2224: Permission denied"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "ssh: connect to host h port 22: Address family not supported"));
        Assert.Equal(RemoteStatus.AuthFailed, Classify(255, "dev@192.0.2.10: Permission denied (publickey)."));
        Assert.Equal(RemoteStatus.Exited(255), Classify(255, "something else"));

        // LogLevel=ERROR hides "Connection to … closed by remote host"; the relay never exits 255 (REMOTE 4.5).
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, ""));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, "\r\n"));
        Assert.Equal(RemoteStatus.Unreachable, Classify(255, " \t\n"));
        Assert.Equal(RemoteStatus.Exited(254), Classify(254, ""));
    }

    [Fact]
    public void RemoteFailures()
    {
        Assert.Equal(RemoteStatus.NotExecutable, Classify(126, ""));
        Assert.Equal(RemoteStatus.NotExecutable, Classify(1, "sh: 1: exec: /home/u/.cache/agent-speed/x: Permission denied"));
        Assert.Equal(RemoteStatus.NotExecutable, Classify(2, "cannot execute binary file"));
        Assert.Equal(RemoteStatus.Exited(127), Classify(127, "not found"));
        Assert.Equal(RemoteStatus.Exited(0), Classify(0, ""));
        Assert.Equal(RemoteStatus.UploadFailed, Classify(3, "", RemoteStage.Upload));
        Assert.Equal(RemoteStatus.UploadFailed, Classify(4, "", RemoteStage.Upload));
        Assert.Equal(RemoteStatus.CannotVerify, Classify(5, "", RemoteStage.Upload));
        Assert.Equal(RemoteStatus.Exited(0), Classify(0, "", RemoteStage.Upload));
        Assert.Equal(RemoteStatus.Exited(1), Classify(1, "", RemoteStage.Probe));
        Assert.Equal(RemoteStatus.Exited(126), Classify(126, "", RemoteStage.Probe));
        Assert.Equal(RemoteStatus.Exited(1), Classify(1, "Permission denied", RemoteStage.Probe));
    }

    [Fact]
    public void OnlyTheLast64KiBCount()
    {
        var noise = new string('x', 70_000);
        Assert.Equal(RemoteStatus.Exited(255), Classify(255, "Permission denied" + noise));
        Assert.Equal(RemoteStatus.AuthFailed, Classify(255, noise + "Permission denied"));
        Assert.Equal(RemoteStatus.Exited(255), Classify(255, "Host key verification failed" + noise));
    }

    [Fact]
    public void StatusGroups()
    {
        foreach (var status in new[]
        {
            RemoteStatus.AuthFailed, RemoteStatus.HostKeyUnknown, RemoteStatus.UnsupportedSystem("x"), RemoteStatus.NotExecutable,
            RemoteStatus.CannotVerify, RemoteStatus.InvalidDestination, RemoteStatus.SshMissing, RemoteStatus.UploadFailed,
            RemoteStatus.VersionMismatch(2),
        })
        {
            Assert.True(status.NeedsUser, status.Code);
            Assert.False(status.IsRetrying, status.Code);
        }

        foreach (var status in new[] { RemoteStatus.Unreachable, RemoteStatus.ProtocolError, RemoteStatus.Exited(1) })
        {
            Assert.True(status.IsRetrying, status.Code);
            Assert.False(status.NeedsUser, status.Code);
        }

        Assert.True(RemoteStatus.Seeding(0.4).IsInProgress);
        Assert.True(RemoteStatus.Installing(0.4).IsInProgress);
        Assert.True(RemoteStatus.Connecting.IsInProgress);
        Assert.False(RemoteStatus.Connected.IsInProgress);
        Assert.True(RemoteStatus.Connected.IsLive);
        Assert.True(RemoteStatus.Seeding(0).IsLive);
        Assert.Equal("unsupportedSystem", RemoteStatus.UnsupportedSystem("Linux riscv64").Code);
        Assert.Equal("Linux riscv64", RemoteStatus.UnsupportedSystem("Linux riscv64").System);
        Assert.Equal(127, RemoteStatus.Exited(127).ExitStatus);
        Assert.Equal("exited", RemoteStatus.Exited(127).Code);
    }

    [Fact]
    public void EveryStatusHasItsSpecCode()
    {
        string[] codes =
        [
            "connecting", "installing", "seeding", "connected", "unreachable", "authFailed", "hostKeyUnknown", "unsupportedSystem",
            "cannotVerify", "uploadFailed", "notExecutable", "versionMismatch", "protocolError", "exited", "sshMissing",
            "invalidDestination", "networkOff", "disabled",
        ];
        Assert.Equal(codes, Enum.GetValues<RemoteStatusKind>().Select(kind => new RemoteStatus(kind).Code));
    }

    private static RemoteStatus Classify(int status, string stderr, RemoteStage stage = RemoteStage.Run) =>
        RemoteStatusClassifier.Classify(status, Encoding.UTF8.GetBytes(stderr), stage);
}
