using AgentSpeed.Core.Remote;
using AgentSpeed.Core.Remote.Windows;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>DESIGN 13 <i>Test Connection</i>: one probe, nothing installed or started; and the Windows process launch.</summary>
public sealed class RemoteConnectionTestTests
{
    [Fact]
    public void ASupportedHostReportsItsSystemAndArchitecture()
    {
        using var harness = new Harness((process, _) =>
        {
            process.SendStdout("system Linux aarch64\ntool sha256sum\nend\n");
            process.Finish(0);
        });
        var outcome = harness.Run(new RemoteHost("devbox"));
        Assert.Equal(RemoteTestOutcome.Supported("Linux aarch64", RemoteArch.Aarch64), outcome);
        var probe = Assert.Single(harness.Transport.Launched);
        Assert.True(probe.IsProbe);
        Assert.Equal("devbox", probe.Request.Arguments[^2]);
        Assert.True(probe.IsInputClosed);
    }

    [Theory]
    [InlineData("Host key verification failed.\r\n", 255, "", "hostKeyUnknown")]
    [InlineData("devbox: Permission denied (publickey).\n", 255, "", "authFailed")]
    [InlineData("", 0, "system Darwin arm64\ntool shasum -a 256\nend\n", "unsupportedSystem")]
    [InlineData("", 0, "system Linux x86_64\ntool none\nend\n", "cannotVerify")]
    [InlineData("", 0, "Welcome to devbox\n", "exited")]
    public void ProbeFailuresReadAsTheConnectionsStatuses(string stderr, int status, string stdout, string code)
    {
        using var harness = new Harness((process, _) =>
        {
            process.SendStderr(stderr);
            process.SendStdout(stdout);
            process.Finish(status);
        });
        var outcome = harness.Run(new RemoteHost("devbox"));
        Assert.False(outcome.IsSupported);
        Assert.Equal(code, outcome.Failure!.Value.Code);
    }

    [Fact]
    public void AnInvalidHostOrAMissingSshNeverLaunches()
    {
        using var invalid = new Harness();
        Assert.Equal(RemoteTestOutcome.Failed(RemoteStatus.InvalidDestination), invalid.Run(new RemoteHost("-oProxyCommand=x")));
        Assert.Equal(RemoteTestOutcome.Failed(RemoteStatus.InvalidDestination), invalid.Run(new RemoteHost("devbox") { RelayFolder = "../x" }));
        Assert.Empty(invalid.Transport.Launched);
        using var missing = new Harness();
        missing.Transport.SetAvailable(false);
        Assert.Equal(RemoteTestOutcome.Failed(RemoteStatus.SshMissing), missing.Run(new RemoteHost("devbox")));
        Assert.Empty(missing.Transport.Launched);
    }

    [Fact]
    public void CancellingEndsTheProbeAndDropsItsResult()
    {
        using var harness = new Harness((_, _) => { });
        var outcomes = new List<RemoteTestOutcome>();
        var test = RemoteConnectionTest.Start(new RemoteHost("devbox"), harness.Environment, outcomes.Add);
        Assert.True(SpinWait.SpinUntil(() => harness.Transport.Launched.Count == 1, TimeSpan.FromSeconds(5)));
        test.Cancel();
        Assert.True(SpinWait.SpinUntil(() => harness.Transport.Launched[0].WasTerminated, TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        Assert.Empty(outcomes);
    }

    [Fact]
    public void AHostThatNeverAnswersTimesOutAsUnreachable()
    {
        using var harness = new Harness((process, _) => process.ExitOnTerminate = null);
        RemoteTestOutcome? outcome = null;
        RemoteConnectionTest.Start(new RemoteHost("devbox"), harness.Environment, o => outcome = o);
        Assert.True(SpinWait.SpinUntil(() => harness.Transport.Launched.Count == 1, TimeSpan.FromSeconds(5)));
        Thread.Sleep(50);
        harness.Scheduling.Advance(RemoteConnectionTest.TimeLimit);
        Assert.True(harness.Transport.Launched[0].WasTerminated);
        harness.Scheduling.Advance(RemoteConnection.KillDelay);
        Assert.True(SpinWait.SpinUntil(() => outcome is not null, TimeSpan.FromSeconds(5)));
        Assert.Equal(RemoteTestOutcome.Failed(RemoteStatus.Unreachable), outcome);
    }

    [Theory]
    [InlineData(new[] { "-T", "--", "devbox" }, "C:\\Windows\\ssh.exe -T -- devbox")]
    [InlineData(new[] { "sh -c 'echo \"$1\"' agent-speed ~/.cache" }, "C:\\Windows\\ssh.exe \"sh -c 'echo \\\"$1\\\"' agent-speed ~/.cache\"")]
    [InlineData(new[] { "", "a\\b", "a b\\", "x\\\"y" }, "C:\\Windows\\ssh.exe \"\" a\\b \"a b\\\\\" \"x\\\\\\\"y\"")]
    public void WindowsCommandLinesQuoteEveryArgumentForTheRuntime(string[] arguments, string expected)
    {
        Assert.Equal(expected, WindowsCommandLine.Of(@"C:\Windows\ssh.exe", arguments));
        Assert.Equal(arguments, Split(WindowsCommandLine.Of(@"C:\Windows\ssh.exe", arguments))[1..]);
    }

    /// <summary>The C runtime's parsing rules (2N backslashes + quote → N and a toggle; 2N+1 → N and a literal quote).</summary>
    private static string[] Split(string line)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        var any = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '\\')
            {
                var count = 0;
                while (i < line.Length && line[i] == '\\')
                {
                    count++;
                    i++;
                }

                if (i < line.Length && line[i] == '"')
                {
                    current.Append('\\', count / 2);
                    if (count % 2 == 1)
                    {
                        current.Append('"');
                    }
                    else
                    {
                        quoted = !quoted;
                        any = true;
                    }
                }
                else
                {
                    current.Append('\\', count);
                    i--;
                }

                continue;
            }

            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (current.Length > 0 || any)
                {
                    arguments.Add(current.ToString());
                }

                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0 || any)
        {
            arguments.Add(current.ToString());
        }

        return [.. arguments];
    }

    private sealed class Harness : IDisposable
    {
        public Harness(Action<FakeProcess, FakeRelayBundle>? probe = null)
        {
            Environment = new RemoteEnvironment(Transport, Relays.Bundle, Scheduling, static () => TimeZoneInfo.Utc, new Dictionary<string, string>());
            var relays = Relays;
            Transport.Script = process => probe?.Invoke(process, relays);
        }

        public ManualScheduling Scheduling { get; } = new();

        public FakeTransport Transport { get; } = new();

        public FakeRelayBundle Relays { get; } = new();

        public RemoteEnvironment Environment { get; }

        public RemoteTestOutcome Run(RemoteHost host)
        {
            using var done = new ManualResetEventSlim();
            RemoteTestOutcome? outcome = null;
            RemoteConnectionTest.Start(host, Environment, o =>
            {
                outcome = o;
                done.Set();
            });
            Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
            return outcome!;
        }

        public void Dispose() => Relays.Dispose();
    }
}
