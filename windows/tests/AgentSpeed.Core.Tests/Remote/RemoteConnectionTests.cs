using AgentSpeed.Core.Remote;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>REMOTE 4.2–4.5, 5.5, 5.6, 6, 8.2: one host's connection with a scripted relay instead of <c>ssh</c>.</summary>
public sealed class RemoteConnectionTests
{
    [Fact]
    public void ProbeRunHelloSeedingReadyConnected()
    {
        using var harness = new ConnectionHarness { RemoteOffsetMs = 2_000 };
        harness.Start();
        Assert.Equal([RemoteStatus.Connecting], harness.Events.Statuses());
        var launched = harness.Transport.Launched;
        Assert.Equal(2, launched.Count);
        Assert.True(launched[0].IsProbe && launched[1].IsRun);
        var run = harness.Run!;
        Assert.Equal(@"C:\Windows\System32\OpenSSH\ssh.exe".Replace('\\', Path.DirectorySeparatorChar), run.Request.Executable.Replace('\\', Path.DirectorySeparatorChar));
        Assert.Equal("C", run.Request.Environment["LC_ALL"]);
        Assert.Equal(["--", "devbox"], run.Request.Arguments.TakeLast(3).Take(2));
        Assert.EndsWith($"agent-speed ~/.cache/agent-speed {harness.Relays.X86.InstalledName}", run.RemoteCommand, StringComparison.Ordinal);
        Assert.Equal(["start"], run.WrittenTypes);
        var start = run.WrittenLines[0];
        Assert.Equal(["codex", "claudeCode"], start.GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
        Assert.Equal(1_779_926_400_000, start.GetProperty("todayStartMs").GetInt64());
        Assert.Equal(3, start.GetProperty("prices").GetProperty("schema").GetInt32());
        Assert.StartsWith("TZif2", System.Text.Encoding.ASCII.GetString(Convert.FromBase64String(start.GetProperty("zone").GetProperty("tzif").GetString()!)), StringComparison.Ordinal);

        run.Send(RelayLines.Hello(harness.NowMs + 2_000));
        harness.Settle();
        Assert.Equal(new RemoteHostStatus(RemoteStatus.Seeding(0), ClockOffsetMs: 2_000, RelayVersion: "1.7.0", RelayArch: "x86_64"), harness.Events.LastStatus);
        Assert.False(harness.Events.LastStatus!.IsClockSkewed);
        Assert.Equal(["start", "ping"], run.WrittenTypes);

        run.Send("""{"t":"progress","value":0.5}""");
        run.Send(RelayLines.Agent());
        run.Send(RelayLines.Response());
        harness.Settle();
        Assert.Equal(RemoteStatus.Seeding(0.5), harness.Events.Statuses()[^1]);
        var seeded = harness.Events.Messages.Where(m => m.Seeded).Select(m => m.Message).ToList();
        Assert.Equal(3, seeded.Count);
        var agent = Assert.IsType<RelayMessage.Agent>(seeded[1]).Value;
        var response = Assert.IsType<RelayMessage.Response>(seeded[2]).Value;
        Assert.Equal(RelayLines.BaseMs + 500 - 2_000, agent.LastLineMs);
        Assert.Equal(RelayLines.BaseMs + 2_000 - 2_000, response.Record.EndMs);

        run.Send(RelayLines.Clock(1, harness.NowMs + 6_000));
        harness.Settle();
        Assert.Equal(6_000, harness.Events.LastStatus!.ClockOffsetMs);
        Assert.True(harness.Events.LastStatus!.IsClockSkewed);

        run.Send(RelayLines.Ready);
        run.Send(RelayLines.Response(RelayLines.ResponseObject("live")));
        harness.Settle();
        Assert.Equal(RemoteStatus.Connected, harness.Events.Statuses()[^1]);
        Assert.Equal(1, harness.Events.HistoryDueCount());
        var last = harness.Events.Messages[^1];
        Assert.Equal("live", Assert.IsType<RelayMessage.Response>(last.Message).Value.Record.Key);
        Assert.False(last.Seeded);
        Assert.Single(harness.Events.Events.Select(e => e.Connection).Distinct());
    }

    [Fact]
    public void UploadsWhenTheRelayIsMissing()
    {
        using var harness = new ConnectionHarness(installed: false);
        harness.Start();
        var launched = harness.Transport.Launched;
        Assert.Equal([(true, false, false), (false, true, false), (false, false, true)], launched.Select(p => (p.IsProbe, p.IsUpload, p.IsRun)));
        var upload = launched[1];
        Assert.Equal(File.ReadAllBytes(harness.Relays.X86.Path!), upload.WrittenData);
        Assert.True(upload.IsInputClosed);
        Assert.EndsWith($"{harness.Relays.X86.InstalledName} {harness.Relays.X86.Sha256}", upload.RemoteCommand, StringComparison.Ordinal);
        var statuses = harness.Events.Statuses();
        Assert.Equal(RemoteStatus.Connecting, statuses[0]);
        Assert.Contains(RemoteStatus.Installing(0), statuses);
        Assert.Contains(RemoteStatus.Installing(1), statuses);
    }

    [Fact]
    public void UploadsTheEmbeddedRelay()
    {
        var bundle = RemoteRelayBundle.FromResources(typeof(RemoteConnectionTests).Assembly, "1.7.0");
        var relay = bundle.Executable(RemoteArch.X86_64)!;
        using var harness = new ConnectionHarness(installed: false, bundle: bundle);
        harness.Start();
        var upload = harness.Transport.Launched[1];
        Assert.True(upload.IsUpload);
        Assert.Equal("fake embedded relay x86_64\n"u8.ToArray(), upload.WrittenData);
        Assert.EndsWith($"{relay.InstalledName} {relay.Sha256}", upload.RemoteCommand, StringComparison.Ordinal);
        Assert.Contains(relay.InstalledName, harness.Run!.RemoteCommand, StringComparison.Ordinal);
    }

    [Fact]
    public void UploadedBytesMustMatchTheDigestSent()
    {
        using var harness = new ConnectionHarness(installed: false);
        var path = harness.Relays.X86.Path!;
        var stamp = File.GetLastWriteTimeUtc(path);
        var bytes = File.ReadAllBytes(path);
        bytes[0] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
        harness.Start();
        Assert.Equal(RemoteStatus.UploadFailed, harness.Events.Statuses()[^1]);
        Assert.True(harness.Transport.Launched[1].WasTerminated);
        Assert.Empty(harness.Transport.Runs);
    }

    [Fact]
    public void TheProbeGetsNoInput()
    {
        using var harness = new ConnectionHarness();
        harness.Start();
        Assert.True(harness.Transport.Launched[0].IsInputClosed);
        Assert.False(harness.Run!.IsInputClosed);
    }

    [Fact]
    public void UploadMismatchIsUploadFailed()
    {
        using var harness = new ConnectionHarness(installed: false, upload: (process, _) =>
        {
            process.SendStdout($"hash {new string('0', 64)}\n");
            process.Finish(0);
        });
        harness.Start();
        Assert.Equal(RemoteStatus.UploadFailed, harness.Events.Statuses()[^1]);
        Assert.Empty(harness.Transport.Runs);
        Assert.Equal([300.0], harness.RetryDelays);

        using var exit3 = new ConnectionHarness(installed: false, upload: (process, _) => process.Finish(3));
        exit3.Start();
        Assert.Equal(RemoteStatus.UploadFailed, exit3.Events.Statuses()[^1]);
    }

    [Fact]
    public void ProbeVerdictsBecomeStatuses()
    {
        using var unsupported = new ConnectionHarness(probe: (process, _) =>
        {
            process.SendStdout("system Linux riscv64\ntool sha256sum\nend\n");
            process.Finish(0);
        });
        unsupported.Start();
        Assert.Equal(RemoteStatus.UnsupportedSystem("Linux riscv64"), unsupported.Events.Statuses()[^1]);

        using var noTool = new ConnectionHarness(probe: (process, _) =>
        {
            process.SendStdout("system Linux aarch64\ntool none\nend\n");
            process.Finish(0);
        });
        noTool.Start();
        Assert.Equal(RemoteStatus.CannotVerify, noTool.Events.Statuses()[^1]);

        using var hostKey = new ConnectionHarness(probe: (process, _) =>
        {
            process.SendStderr("Host key verification failed.\r\n");
            process.Finish(255);
        });
        hostKey.Start();
        Assert.Equal(RemoteStatus.HostKeyUnknown, hostKey.Events.Statuses()[^1]);
        Assert.Equal([300.0], hostKey.RetryDelays);
    }

    [Fact]
    public void InvalidDestinationAndMissingSshLaunchNothing()
    {
        using var invalid = new ConnectionHarness(destination: "-oProxyCommand=x");
        invalid.Start();
        Assert.Equal([RemoteStatus.InvalidDestination], invalid.Events.Statuses());
        Assert.Empty(invalid.Transport.Launched);
        Assert.Empty(invalid.Scheduling.Deadlines);

        using var missing = new ConnectionHarness();
        missing.Transport.SetAvailable(false);
        missing.Start();
        Assert.Equal([RemoteStatus.SshMissing], missing.Events.Statuses());
        Assert.Empty(missing.Transport.Launched);
        Assert.Equal([300.0], missing.RetryDelays);
    }

    [Fact]
    public void SilenceEndsTheConnectionAsUnreachable()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect();
        harness.Advance(149);
        Assert.Equal(RemoteStatus.Connected, harness.Events.Statuses()[^1]);
        Assert.Equal(5, run.WrittenTypes.Count(t => t == "ping"));
        harness.Advance(1);
        Assert.True(run.IsInputClosed && run.WasTerminated);
        Assert.Equal(RemoteStatus.Unreachable, harness.Events.Statuses()[^1]);
        Assert.Equal([5.0], harness.RetryDelays);
        harness.Advance(5);
        Assert.Equal(4, harness.Transport.Launched.Count);
        Assert.Equal(2, harness.Transport.Runs.Count);
    }

    [Fact]
    public void AnsweredPingsKeepItAlive()
    {
        using var harness = new ConnectionHarness(answersPings: true);
        var run = harness.Connect();
        harness.Advance(600);
        Assert.Equal(RemoteStatus.Connected, harness.Events.Statuses()[^1]);
        Assert.False(run.WasTerminated);
        Assert.Equal(21, run.WrittenTypes.Count(t => t == "ping"));
    }

    [Fact]
    public void BackoffSequence()
    {
        using var harness = new ConnectionHarness(probe: (process, _) =>
        {
            process.SendStderr("ssh: connect to host devbox port 22: Connection refused\n");
            process.Finish(255);
        });
        harness.Start();
        foreach (var delay in new[] { 5, 10, 20, 40, 60, 60 })
        {
            harness.Advance(delay);
        }

        Assert.Equal([5.0, 10, 20, 40, 60, 60, 60], harness.RetryDelays);
        Assert.All(harness.Events.Statuses(), s => Assert.True(s == RemoteStatus.Connecting || s == RemoteStatus.Unreachable));
    }

    [Fact]
    public void BackoffResetsAfterSixtySecondsUp()
    {
        var failing = true;
        using var harness = new ConnectionHarness(answersPings: true, probe: (process, relays) =>
        {
            if (Volatile.Read(ref failing))
            {
                process.SendStderr("Connection closed by 10.0.0.1 port 22\n");
                process.Finish(255);
            }
            else
            {
                process.SendStdout(ConnectionHarness.ProbeOutput(relays, installed: true));
                process.Finish(0);
            }
        });
        harness.Start();
        harness.Advance(5);
        Assert.Equal([5.0, 10], harness.RetryDelays);
        Volatile.Write(ref failing, false);
        harness.Advance(10);
        var first = harness.Run!;
        first.Send(RelayLines.Hello(harness.NowMs));
        harness.Settle();
        harness.Advance(30);
        first.SendStderr("Connection closed by 10.0.0.1 port 22\n");
        first.Finish(255);
        harness.Settle();
        Assert.Equal([5.0, 10, 20], harness.RetryDelays);
        harness.Advance(20);
        var second = harness.Run!;
        Assert.NotSame(first, second);
        second.Send(RelayLines.Hello(harness.NowMs));
        harness.Settle();
        harness.Advance(61);
        second.Finish(255);
        harness.Settle();
        Assert.Equal([5.0, 10, 20, 5], harness.RetryDelays);
    }

    [Fact]
    public void UserActionableErrorsRetryEveryFiveMinutesOrOnRetry()
    {
        using var harness = new ConnectionHarness(probe: (process, _) =>
        {
            process.SendStderr("devbox: Permission denied (publickey).\n");
            process.Finish(255);
        });
        harness.Start();
        Assert.Equal([RemoteStatus.Connecting, RemoteStatus.AuthFailed], harness.Events.Statuses());
        Assert.Equal([300.0], harness.RetryDelays);
        harness.Connection.NoteWake();
        harness.Advance(299);
        Assert.Single(harness.Transport.Launched);
        harness.Advance(1);
        Assert.Equal(2, harness.Transport.Launched.Count);
        harness.Connection.RetryNow();
        harness.Settle();
        Assert.Equal(3, harness.Transport.Launched.Count);
    }

    [Fact]
    public void WakeRetriesAWaitingHostAtOnce()
    {
        using var harness = new ConnectionHarness(probe: (process, _) =>
        {
            process.SendStderr("ssh: Could not resolve hostname devbox: No such host is known.\n");
            process.Finish(255);
        });
        harness.Start();
        harness.Advance(5);
        harness.Advance(10);
        Assert.Equal(3, harness.Transport.Launched.Count);
        harness.Connection.NoteWake();
        harness.Settle();
        Assert.Equal(4, harness.Transport.Launched.Count);
    }

    [Fact]
    public void StopClosesInputAndKillsAfterTwoSeconds()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect();
        run.ExitOnTerminate = null;
        harness.Connection.Stop(RemoteStatus.Disabled);
        harness.Settle();
        Assert.True(run.IsInputClosed);
        Assert.True(run.WasTerminated);
        Assert.False(run.WasKilled);
        Assert.Equal(RemoteStatus.Disabled, harness.Events.Statuses()[^1]);
        harness.Advance(2);
        Assert.True(run.WasKilled);
        harness.Advance(600);
        Assert.Equal(RemoteStatus.Disabled, harness.Events.Statuses()[^1]);
        Assert.Equal(2, harness.Transport.Launched.Count);
        harness.Connection.RetryNow();
        harness.Connection.Start();
        harness.Settle();
        Assert.Equal(2, harness.Transport.Launched.Count);
    }

    [Fact]
    public void MoreThan100InvalidLinesIsAProtocolError()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect();
        for (var i = 0; i < 100; i++)
        {
            run.Send(i % 2 == 0 ? "garbage" : """{"t":"agent","a":{}}""");
        }

        harness.Settle();
        Assert.False(run.WasTerminated);
        run.Send("[]");
        harness.Settle();
        Assert.True(run.WasTerminated);
        Assert.Equal(RemoteStatus.ProtocolError, harness.Events.Statuses()[^1]);
        Assert.Equal([5.0], harness.RetryDelays);
    }

    [Fact]
    public void LinesBeforeHelloAreInvalidAndAWrongProtocolIsAMismatch()
    {
        using var harness = new ConnectionHarness();
        harness.Start();
        var run = harness.Run!;
        run.Send(RelayLines.Ready);
        run.Send(RelayLines.Hello(protocol: 2));
        harness.Settle();
        Assert.Empty(harness.Events.Messages);
        Assert.True(run.WasTerminated);
        Assert.Equal(RemoteStatus.VersionMismatch(2), harness.Events.Statuses()[^1]);
        Assert.Equal([300.0], harness.RetryDelays);
    }

    [Fact]
    public void TimesThatLeaveTheRangeAfterConversionAreInvalid()
    {
        using var harness = new ConnectionHarness();
        harness.Start();
        var run = harness.Run!;
        run.Send(RelayLines.Hello(harness.NowMs + 10_000));
        var early = RelayLines.ResponseObject();
        early["startMs"] = RemoteFields.EarliestMs + 5_000;
        early["firstMs"] = null;
        run.Send(RelayLines.Response(early));
        run.Send(RelayLines.Response());
        harness.Settle();
        var responses = harness.Events.Messages.Select(m => m.Message).OfType<RelayMessage.Response>().ToList();
        Assert.Single(responses);
        Assert.Equal(RelayLines.BaseMs - 10_000, responses[0].Value.Record.StartMs);
    }

    [Fact]
    public void AtMost4096AgentsPerHost()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect();
        harness.Events.Clear();
        var text = string.Concat(Enumerable.Range(0, RemoteLimits.Agents + 1).Select(i => RelayLines.Agent(RelayLines.AgentObject($"/a/{i}")) + "\n"));
        run.SendStdout(text);
        run.Send(RelayLines.Agent(RelayLines.AgentObject("/a/1")));
        run.Send("""{"t":"agentRemoved","id":"/a/0"}""");
        run.Send(RelayLines.Agent(RelayLines.AgentObject("/a/new")));
        harness.Settle();
        var agents = harness.Events.Messages.Select(m => m.Message).OfType<RelayMessage.Agent>().Select(a => a.Value.RelayId).ToList();
        Assert.Equal(RemoteLimits.Agents + 2, agents.Count);
        Assert.DoesNotContain("/a/4096", agents);
        Assert.Equal("/a/new", agents[^1]);
    }

    [Fact]
    public void HistoryScheduleAfterReadyEveryTenMinutesAndAtMidnight()
    {
        var midnight = DateTimeOffset.FromUnixTimeSeconds(1_780_012_800);
        using var harness = new ConnectionHarness(answersPings: true, start: midnight.AddSeconds(-300));
        var run = harness.Connect();
        Assert.Equal(1, harness.Events.HistoryDueCount());
        harness.Connection.RequestHistory(new Dictionary<string, string> { ["2026-05-28/codex"] = "0123456789abcdef" });
        harness.Settle();
        Assert.Equal("history", run.WrittenTypes[^1]);
        Assert.Equal("0123456789abcdef", run.WrittenLines[^1].GetProperty("digests").GetProperty("2026-05-28/codex").GetString());
        harness.Advance(300);
        var today = run.WrittenLines.Last(l => l.GetProperty("t").GetString() == "today");
        Assert.Equal(midnight.ToUnixTimeMilliseconds(), today.GetProperty("todayStartMs").GetInt64());
        Assert.Equal(1, harness.Events.HistoryDueCount());
        run.Send("""{"t":"historyDone","days":2,"analyzed":5}""");
        harness.Settle();
        Assert.Equal(2, harness.Events.HistoryDueCount());
        harness.Advance(300);
        Assert.Equal(3, harness.Events.HistoryDueCount());
        harness.Advance(600);
        Assert.Equal(4, harness.Events.HistoryDueCount());
    }

    [Fact]
    public void AFailedHistoryRunLetsTheNextSyncGo()
    {
        using var harness = new ConnectionHarness(answersPings: true);
        var run = harness.Connect();
        Assert.Equal(1, harness.Events.HistoryDueCount());
        harness.Connection.RequestHistory(new Dictionary<string, string>());
        harness.Settle();
        harness.Advance(600);
        Assert.Equal(1, harness.Events.HistoryDueCount());
        run.Send("""{"t":"error","code":"historyFailed","detail":"disk full"}""");
        harness.Settle();
        Assert.Equal(2, harness.Events.HistoryDueCount());
        Assert.False(run.IsInputClosed);
        harness.Connection.RequestHistory(new Dictionary<string, string>());
        harness.Settle();
        run.Send("""{"t":"error","code":"historyFailed","detail":"disk full"}""");
        harness.Settle();
        harness.Advance(600);
        Assert.Equal(3, harness.Events.HistoryDueCount());
    }

    [Fact]
    public void HistoryRequestsBeforeReadyAreIgnored()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect(ready: false);
        harness.Connection.RequestHistory(new Dictionary<string, string>());
        harness.Settle();
        Assert.DoesNotContain("history", run.WrittenTypes);
        Assert.Equal(0, harness.Events.HistoryDueCount());
    }

    [Fact]
    public void PricesWatchAndZoneGoToTheRunningRelay()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect();
        harness.Connection.UpdatePrices(RemotePriceTableJson.From("""{"schema":4}"""u8)!);
        harness.Connection.SetWatch(true);
        harness.Connection.SetWatch(true);
        harness.Connection.NoteTimeZoneChange();
        harness.Settle();
        Assert.Equal(["start", "ping", "prices", "watch", "zone", "today"], run.WrittenTypes);
        Assert.Equal(4, run.WrittenLines[2].GetProperty("table").GetProperty("schema").GetInt32());
    }

    [Fact]
    public void ReconnectStartsAFreshConnectionNumber()
    {
        using var harness = new ConnectionHarness();
        var run = harness.Connect();
        run.SendStderr("Connection closed by remote host\n");
        run.Finish(255);
        harness.Advance(5);
        Assert.NotSame(run, harness.Run);
        var numbers = harness.Events.Events.Select(e => e.Connection).ToList();
        Assert.Equal(2, numbers.Distinct().Count());
        Assert.Equal(numbers.Order(), numbers);
    }
}
