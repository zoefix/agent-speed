using System.Diagnostics;
using System.Runtime.Versioning;
using AgentSpeed.Core.Quota.Local;
using AgentSpeed.Core.Tests.Quota.Cookies;
using AgentSpeed.Core.Traffic.Windows;

namespace AgentSpeed.Core.Tests.Quota.Local;

/// <summary>
/// The Windows locator with real processes (the stand-in agy under its own name and as a plain
/// <c>language_server.exe</c>): a user's agy and a plain server with Antigravity's arguments are found, and the same
/// processes as Agent Speed's own descendants are not (METRICS 10.5).
/// </summary>
[SupportedOSPlatform("windows")]
[Collection(FakeAgy.Collection)]
public sealed class AntigravityLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "agentspeed-locator-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _started = [];

    public AntigravityLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                    process.WaitForExit(5_000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [WindowsFact]
    public void AUsersAgyAndPlainServerCountButAgentSpeedsOwnDoNot()
    {
        var bin = Path.Combine(_root, "bin");
        FakeAgy.Install(bin);
        File.Copy(Path.Combine(bin, "agy.exe"), Path.Combine(bin, "language_server.exe"));
        var agy = FakeAgy.Linger(Path.Combine(bin, "agy.exe"), Path.Combine(_root, "agy.pid"));
        _started.Add(agy);
        var server = FakeAgy.Linger(Path.Combine(bin, "language_server.exe"), Path.Combine(_root, "server.pid"), "--app_data_dir", "antigravity",
            "--csrf_token", "t-1");
        _started.Add(server);
        var generic = FakeAgy.Linger(Path.Combine(bin, "language_server.exe"), Path.Combine(_root, "generic.pid"), "--stdio");
        _started.Add(generic);

        // Seen from outside Agent Speed (no own process), they are a user's.
        using (var outside = new AntigravityProcessLocator(_root, app: null))
        {
            var servers = outside.Servers();
            Assert.Contains(servers, s => s.Pid == server.Id);
            Assert.DoesNotContain(servers, s => s.Pid == generic.Id || s.Pid == agy.Id);
            Assert.True(outside.AgyRuns);
        }

        // This test process stands for Agent Speed: its children are its own.
        using var own = new AntigravityProcessLocator(_root, ProcessReader.Current());
        Assert.DoesNotContain(own.Servers(), s => s.Pid == server.Id);
        var others = Process.GetProcessesByName("agy").Select(p => p.Id).Where(id => id != agy.Id).ToList();
        if (others.Count == 0)
        {
            Assert.False(own.AgyRuns);
        }
    }

    [WindowsFact]
    public void ThisProcessIsTheListingsOwn()
    {
        var self = ProcessReader.Current();
        Assert.NotNull(self);
        Assert.Equal(Environment.ProcessId, self.Value.Pid);
        using var reader = new ProcessReader();
        var entries = new List<AgentSpeed.Core.Traffic.Engine.ProcessEntry>();
        Assert.True(reader.List(entries));
        Assert.Contains(entries, e => e.Identity == self.Value);
    }
}
