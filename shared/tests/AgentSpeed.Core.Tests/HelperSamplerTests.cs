using AgentSpeed.Core.Traffic.Engine;
using AgentSpeed.Core.Traffic.Helper;
using AgentSpeed.Core.Traffic.Monitor;
using AgentSpeed.Core.Traffic.Windows;

namespace AgentSpeed.Core.Tests;

/// <summary>WINDOWS-SERVICE 6 and METRICS 10.3/10.4: the per-tick helper state machine, with fakes for the SCM and pipe.</summary>
public sealed class HelperSamplerTests
{
    [Fact]
    public void InactiveQueriesAtOnceThenEveryThirtySeconds()
    {
        var (sampler, service, connection) = Create();

        Assert.Null(sampler.Poll(false, false, 0));
        Assert.Equal(1, service.Queries);
        Assert.Equal(ProcessCounterStatus.Idle, sampler.Status);
        sampler.Poll(false, false, 1_000);
        sampler.Poll(false, false, 29_999);
        Assert.Equal(1, service.Queries);
        sampler.Poll(false, false, 30_000);
        Assert.Equal(2, service.Queries);
        Assert.Equal(0, connection.Connects);
        Assert.Equal(0, service.Starts);
    }

    [Fact]
    public void InactiveReportsTheInstallation()
    {
        var (sampler, service, _) = Create();
        service.Info = HelperServiceInfo.NotInstalled;
        sampler.Poll(false, false, 0);
        Assert.Equal(ProcessCounterStatus.HelperNotInstalled, sampler.Status);

        service.Info = Installed(HelperServiceState.Stopped, version: new HelperVersion(0, 9, 0));
        sampler.Poll(false, false, 5_000);
        Assert.Equal(ProcessCounterStatus.HelperNotInstalled, sampler.Status);
        sampler.Retry();
        sampler.Poll(false, false, 5_000);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        Assert.Equal(0, service.Starts);
    }

    [Fact]
    public void RetryForcesAnImmediateRequery()
    {
        var (sampler, service, _) = Create();
        sampler.Poll(false, false, 0);
        sampler.Retry();
        sampler.Poll(false, false, 10);
        Assert.Equal(2, service.Queries);
    }

    [Fact]
    public void InactiveNeverStartsOrConnects()
    {
        var (sampler, service, connection) = Create(Installed(HelperServiceState.Stopped));
        for (var t = 0; t < 120_000; t += 1_000)
        {
            sampler.Poll(false, false, t);
        }

        Assert.Equal(0, service.Starts);
        Assert.Equal(0, connection.Connects);
        Assert.Equal(ProcessCounterStatus.Idle, sampler.Status);
    }

    [Fact]
    public void GoingInactiveDisconnectsAndRequeriesAtOnce()
    {
        var (sampler, service, connection) = Create();
        Assert.NotNull(sampler.Poll(true, true, 0));
        Assert.True(connection.IsOpen);
        var queries = service.Queries;

        sampler.Poll(false, false, 1_000);

        Assert.False(connection.IsOpen);
        Assert.Equal(queries + 1, service.Queries);
        Assert.Equal(ProcessCounterStatus.Idle, sampler.Status);
    }

    [Fact]
    public void BecomingActiveQueriesAtOnce()
    {
        var (sampler, service, connection) = Create();
        sampler.Poll(false, false, 0);
        Assert.NotNull(sampler.Poll(true, true, 1_000));
        Assert.Equal(2, service.Queries);
        Assert.Equal(1, connection.Connects);
    }

    [Fact]
    public void MissingServiceIsNotInstalled()
    {
        var (sampler, service, connection) = Create(HelperServiceInfo.NotInstalled);

        Assert.Null(sampler.Poll(true, true, 0));
        Assert.Equal(ProcessCounterStatus.HelperNotInstalled, sampler.Status);
        sampler.Poll(true, true, 29_999);
        Assert.Equal(1, service.Queries);
        sampler.Poll(true, true, 30_000);
        Assert.Equal(2, service.Queries);
        Assert.Equal(0, connection.Connects);
        Assert.Equal(0, service.Starts);
    }

    [Fact]
    public void UserNotAllowedIsNotInstalled()
    {
        var (sampler, service, connection) = Create(Installed(HelperServiceState.Running, allowed: false));
        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperNotInstalled, sampler.Status);
        Assert.Equal(0, connection.Connects);
        Assert.Equal(0, service.Starts);
    }

    [Theory]
    [InlineData(0, 9, 9, 1u)]
    [InlineData(1, 0, 0, 0u)]
    public void OutdatedIsNeverStartedAndARunningOneIsStoppedOnce(int major, int minor, int patch, uint protocol)
    {
        var (sampler, service, connection) = Create(Installed(HelperServiceState.Running, version: new HelperVersion(major, minor, patch), protocol: protocol));

        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        sampler.Poll(true, true, 30_000);
        sampler.Poll(false, false, 31_000);
        sampler.Poll(true, true, 62_000);

        Assert.Equal(1, service.Stops);
        Assert.Equal(0, service.Starts);
        Assert.Equal(0, connection.Connects);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
    }

    [Fact]
    public void UnreadableVersionIsOutdated()
    {
        var (sampler, service, _) = Create(Installed(HelperServiceState.Stopped) with { Version = null });
        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        Assert.Equal(0, service.Starts);
    }

    [Fact]
    public void StoppedServiceIsStartedThenPolledUntilRunning()
    {
        var (sampler, service, connection) = Create(Installed(HelperServiceState.Stopped));

        Assert.Null(sampler.Poll(true, true, 0));
        Assert.Equal(1, service.Starts);
        Assert.Equal(ProcessCounterStatus.Starting, sampler.Status);

        service.Info = Installed(HelperServiceState.StartPending);
        Assert.Null(sampler.Poll(true, true, 1_000));
        Assert.Equal(ProcessCounterStatus.Starting, sampler.Status);

        service.Info = Installed(HelperServiceState.Running);
        Assert.NotNull(sampler.Poll(true, true, 2_000));
        Assert.Equal(1, service.Starts);
        Assert.Equal(1, connection.Connects);
        Assert.Equal(ProcessCounterStatus.Sampling, sampler.Status);
        Assert.Equal(1, sampler.Stream);
    }

    [Fact]
    public void StartGivesUpAfterTenSeconds()
    {
        var (sampler, service, _) = Create(Installed(HelperServiceState.Stopped));

        sampler.Poll(true, true, 0);
        sampler.Poll(true, true, 9_999);
        Assert.Equal(ProcessCounterStatus.Starting, sampler.Status);
        sampler.Poll(true, true, 10_000);
        Assert.Equal(ProcessCounterStatus.HelperStopped, sampler.Status);
        Assert.NotNull(sampler.LastError);
        Assert.Equal(1, service.Starts);

        var queries = service.Queries;
        sampler.Poll(true, true, 10_999);
        Assert.Equal(queries, service.Queries);
        sampler.Poll(true, true, 11_000);
        Assert.Equal(2, service.Starts);
    }

    [Fact]
    public void StartDeniedIsDenied()
    {
        var (sampler, service, _) = Create(Installed(HelperServiceState.Stopped));
        service.StartError = 5;
        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperDenied, sampler.Status);
    }

    [Fact]
    public void StartFailureIsStopped()
    {
        var (sampler, service, _) = Create(Installed(HelperServiceState.Stopped));
        service.StartError = 1058;
        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperStopped, sampler.Status);
        Assert.NotNull(sampler.LastError);
    }

    [Fact]
    public void UnknownServiceStateWithAccessDeniedIsDenied()
    {
        var (sampler, _, connection) = Create(Installed(HelperServiceState.Unknown) with { Win32Error = 5 });
        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperDenied, sampler.Status);
        Assert.Equal(0, connection.Connects);
    }

    [Fact]
    public void DeniedConnectionIsDenied()
    {
        var (sampler, _, connection) = Create();
        connection.ConnectResult = HelperConnectResult.Denied;
        connection.LastError = 5;

        Assert.Null(sampler.Poll(true, true, 0));
        Assert.Equal(ProcessCounterStatus.HelperDenied, sampler.Status);
        Assert.Equal(0, connection.Hellos);
        sampler.Poll(true, true, 999);
        Assert.Equal(1, connection.Connects);
    }

    [Fact]
    public void FailedConnectionIsAnError()
    {
        var (sampler, _, connection) = Create();
        connection.ConnectResult = HelperConnectResult.Failed;
        connection.LastError = 87;
        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperError, sampler.Status);
        Assert.NotNull(sampler.LastError);
    }

    [Fact]
    public void PipeNotReadyWaitsThenGivesUp()
    {
        var (sampler, _, connection) = Create();
        connection.ConnectResult = HelperConnectResult.NotReady;

        sampler.Poll(true, true, 0);
        sampler.Poll(true, true, 5_000);
        Assert.Equal(ProcessCounterStatus.Starting, sampler.Status);
        sampler.Poll(true, true, 10_000);
        Assert.Equal(ProcessCounterStatus.HelperStopped, sampler.Status);
        Assert.Equal(3, connection.Connects);
    }

    [Fact]
    public void UnsupportedHelloIsOutdated()
    {
        var (sampler, service, connection) = Create();
        connection.HelloResult = HelperCallResult.UnsupportedVersion;

        Assert.Null(sampler.Poll(true, true, 0));
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        Assert.False(connection.IsOpen);
        Assert.Equal(0, sampler.Stream);
        sampler.Poll(true, true, 29_000);
        Assert.Equal(1, service.Queries);
    }

    [Fact]
    public void HelloWithoutACommonProtocolIsOutdated()
    {
        var (sampler, _, connection) = Create();
        connection.HelloValue = connection.HelloValue with { MinProtocol = 2, MaxProtocol = 3 };

        sampler.Poll(true, true, 0);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        Assert.Equal(0, connection.Snapshots);
    }

    [Fact]
    public void HelloFromABinaryOlderThanTheMinimumIsOutdatedAndStopped()
    {
        // The registry says 1.0.0, but the running binary reports 0.9.0.
        var (sampler, service, connection) = Create();
        connection.HelloValue = connection.HelloValue with { HelperVersion = new HelperVersion(0, 9, 0).Packed };

        Assert.Null(sampler.Poll(true, true, 0));
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        Assert.False(connection.IsOpen);
        Assert.Equal(0, connection.Snapshots);
        Assert.Equal(0, sampler.Stream);
        Assert.Equal(1, service.Stops);

        // Later queries still see a registry that looks current: the sampler must not reconnect or start it again.
        service.Info = Installed(HelperServiceState.Stopped);
        sampler.Poll(true, true, HelperSampler.QueryIntervalMs);
        sampler.Poll(true, true, 3 * HelperSampler.QueryIntervalMs);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);
        Assert.Equal(0, service.Starts);
        Assert.Equal(1, connection.Connects);
        Assert.Equal(1, service.Stops);

        sampler.Poll(false, false, 4 * HelperSampler.QueryIntervalMs);
        Assert.Equal(ProcessCounterStatus.HelperOutdated, sampler.Status);

        // After an update the coordinator calls Retry(): the helper is tried again.
        connection.HelloValue = connection.HelloValue with { HelperVersion = HelperProtocol.BundledVersion.Packed };
        service.Info = Installed(HelperServiceState.Running);
        sampler.Retry();
        sampler.Poll(true, true, 5 * HelperSampler.QueryIntervalMs);
        Assert.Equal(ProcessCounterStatus.Sampling, sampler.Status);
        Assert.Equal(1, sampler.Stream);
    }

    [Fact]
    public void FailuresBackOffUpToAMinute()
    {
        var (sampler, _, connection) = Create();
        connection.HelloResult = HelperCallResult.Failed;
        long t = 0;

        foreach (var delay in new long[] { 1_000, 2_000, 4_000, 8_000, 16_000, 32_000, 60_000, 60_000 })
        {
            var before = connection.Connects;
            sampler.Poll(true, true, t);
            Assert.Equal(before + 1, connection.Connects);
            Assert.Equal(ProcessCounterStatus.HelperStopped, sampler.Status);
            sampler.Poll(true, true, t + delay - 1);
            Assert.Equal(before + 1, connection.Connects);
            t += delay;
        }
    }

    [Fact]
    public void RetryResetsTheBackoff()
    {
        var (sampler, _, connection) = Create();
        connection.HelloResult = HelperCallResult.Failed;
        sampler.Poll(true, true, 0);
        sampler.Poll(true, true, 1_000);
        sampler.Poll(true, true, 3_000);
        Assert.Equal(3, connection.Connects);

        sampler.Retry();
        sampler.Poll(true, true, 3_001);
        Assert.Equal(4, connection.Connects);
        sampler.Poll(true, true, 4_001);
        Assert.Equal(5, connection.Connects);
    }

    [Fact]
    public void SuccessResetsTheBackoff()
    {
        var (sampler, _, connection) = Create();
        connection.HelloResult = HelperCallResult.Failed;
        sampler.Poll(true, true, 0);
        sampler.Poll(true, true, 1_000);
        connection.HelloResult = HelperCallResult.Ok;
        Assert.NotNull(sampler.Poll(true, true, 3_000));

        connection.SnapshotResult = HelperCallResult.Failed;
        sampler.Poll(true, true, 4_000);
        Assert.Equal(ProcessCounterStatus.HelperStopped, sampler.Status);
        connection.SnapshotResult = HelperCallResult.Ok;
        Assert.NotNull(sampler.Poll(true, true, 5_000));
    }

    [Fact]
    public void EveryConnectionIsANewStream()
    {
        var (sampler, _, connection) = Create();
        sampler.Poll(true, true, 0);
        Assert.Equal(1, sampler.Stream);

        connection.SnapshotResult = HelperCallResult.Failed;
        sampler.Poll(true, true, 1_000);
        Assert.Equal(1, sampler.Stream);
        connection.SnapshotResult = HelperCallResult.Ok;
        sampler.Poll(true, true, 2_000);
        Assert.Equal(2, sampler.Stream);
        Assert.Equal(2, connection.Connects);
    }

    [Fact]
    public void AChangedEpochIsANewStream()
    {
        var (sampler, _, connection) = Create();
        sampler.Poll(true, true, 0);
        sampler.Poll(true, true, 1_000);
        Assert.Equal(1, sampler.Stream);

        connection.Epoch = 999;
        sampler.Poll(true, true, 2_000);
        Assert.Equal(2, sampler.Stream);
        Assert.Equal(1, connection.Connects);
    }

    [Fact]
    public void RowsCarryTheKernelIdentityAndCounters()
    {
        var (sampler, _, connection) = Create();
        connection.Entries.Add(new HelperEntry(4321, 0, 134_000_000_000_000_000, 1_000, 2_000));
        connection.Entries.Add(new HelperEntry(77, HelperEntry.ExitedFlag, 134_000_000_000_500_000, 5, 6));

        var rows = sampler.Poll(true, true, 0);

        Assert.NotNull(rows);
        Assert.Equal(
            new[]
            {
                new CounterRow(ProcessIdentity.FromFileTime(4321, 134_000_000_000_000_000), new TrafficCounter(1_000, 2_000)),
                new CounterRow(ProcessIdentity.FromFileTime(77, 134_000_000_000_500_000), new TrafficCounter(5, 6)),
            },
            rows);
        Assert.Equal(ProcessCounterStatus.Sampling, sampler.Status);
        Assert.Null(sampler.LastError);
    }

    [Fact]
    public void TheRowBufferIsReused()
    {
        var (sampler, _, connection) = Create();
        connection.Entries.Add(new HelperEntry(10, 0, 1, 1, 1));
        var first = sampler.Poll(true, true, 0);
        connection.Entries[0] = new HelperEntry(10, 0, 1, 2, 2);
        var second = sampler.Poll(true, true, 1_000);

        Assert.Same(first, second);
        Assert.Equal(new TrafficCounter(2, 2), second![0].Counter);
    }

    [Fact]
    public void NoFetchMeansNoRows()
    {
        var (sampler, _, connection) = Create();
        Assert.Null(sampler.Poll(true, false, 0));
        Assert.Equal(1, sampler.Stream);
        Assert.Equal(ProcessCounterStatus.Starting, sampler.Status);
        Assert.Equal(0, connection.Snapshots);
        Assert.NotNull(sampler.Poll(true, true, 1_000));
    }

    [Fact]
    public void AnIdleConnectionSendsAKeepAliveHello()
    {
        var (sampler, _, connection) = Create();
        sampler.Poll(true, true, 0);
        Assert.Equal(1, connection.Hellos);

        sampler.Poll(true, false, 29_999);
        Assert.Equal(1, connection.Hellos);
        sampler.Poll(true, false, 30_000);
        Assert.Equal(2, connection.Hellos);
        sampler.Poll(true, true, 31_000);
        sampler.Poll(true, false, 60_999);
        Assert.Equal(2, connection.Hellos);
        Assert.Equal(1, connection.Connects);
    }

    [Fact]
    public void RateLimitedSnapshotKeepsTheConnection()
    {
        var (sampler, _, connection) = Create();
        sampler.Poll(true, true, 0);
        connection.SnapshotResult = HelperCallResult.RateLimited;

        Assert.Null(sampler.Poll(true, true, 100));
        Assert.True(connection.IsOpen);
        Assert.Equal(ProcessCounterStatus.Sampling, sampler.Status);
        Assert.Equal(1, sampler.Stream);
    }

    [Fact]
    public void ADroppedConnectionIsNoticed()
    {
        var (sampler, _, connection) = Create();
        sampler.Poll(true, true, 0);
        connection.Close();

        Assert.Null(sampler.Poll(true, true, 1_000));
        Assert.Equal(ProcessCounterStatus.HelperStopped, sampler.Status);
    }

    [Fact]
    public void DisposeClosesTheConnection()
    {
        var (sampler, _, connection) = Create();
        sampler.Poll(true, true, 0);
        sampler.Dispose();
        Assert.False(connection.IsOpen);
        Assert.True(connection.Disposed);
    }

    [Fact]
    public void LimiterAllowsFourRequestsPerSecond()
    {
        var limiter = new HelperRequestLimiter();
        Assert.True(limiter.TryAcquire(0));
        Assert.True(limiter.TryAcquire(10));
        Assert.True(limiter.TryAcquire(20));
        Assert.True(limiter.TryAcquire(30));
        Assert.False(limiter.TryAcquire(999));
        Assert.True(limiter.TryAcquire(1_000));
        Assert.False(limiter.TryAcquire(1_009));
        Assert.True(limiter.TryAcquire(1_010));
    }

    private static (HelperSampler Sampler, FakeService Service, FakeConnection Connection) Create(HelperServiceInfo? info = null)
    {
        var service = new FakeService { Info = info ?? Installed(HelperServiceState.Running) };
        var connection = new FakeConnection();
        return (new HelperSampler(service, connection), service, connection);
    }

    private static HelperServiceInfo Installed(
        HelperServiceState state, HelperVersion? version = null, uint protocol = HelperProtocol.MaxVersion, bool allowed = true) =>
        new(
            state,
            state == HelperServiceState.Running ? 4321 : 0,
            @"""C:\Program Files\Agent Speed\Traffic Helper\AgentSpeedTrafficHelper.exe""",
            version ?? HelperProtocol.BundledVersion,
            protocol,
            ["S-1-5-21-1-2-3-1001"],
            allowed,
            0);

    private sealed class FakeService : IHelperService
    {
        public HelperServiceInfo Info { get; set; } = HelperServiceInfo.NotInstalled;

        public int StartError { get; set; }

        public int Queries { get; private set; }

        public int Starts { get; private set; }

        public int Stops { get; private set; }

        public HelperServiceInfo Query()
        {
            Queries++;
            return Info;
        }

        public int Start()
        {
            Starts++;
            return StartError;
        }

        public int Stop()
        {
            Stops++;
            return 0;
        }
    }

    private sealed class FakeConnection : IHelperConnection
    {
        public HelperConnectResult ConnectResult { get; set; } = HelperConnectResult.Connected;

        public HelperCallResult HelloResult { get; set; } = HelperCallResult.Ok;

        public HelperHello HelloValue { get; set; } =
            new(HelperProtocol.MinVersion, HelperProtocol.MaxVersion, HelperProtocol.BundledVersion.Packed, HelperHello.SessionHealthy, 0, 0, 0);

        public HelperCallResult SnapshotResult { get; set; } = HelperCallResult.Ok;

        public ulong Epoch { get; set; } = 133_900_000_000_000_000;

        public List<HelperEntry> Entries { get; } = [];

        public bool IsOpen { get; private set; }

        public bool Disposed { get; private set; }

        public int LastError { get; set; }

        public int Connects { get; private set; }

        public int Hellos { get; private set; }

        public int Snapshots { get; private set; }

        public HelperConnectResult Connect()
        {
            Connects++;
            IsOpen = ConnectResult == HelperConnectResult.Connected;
            return ConnectResult;
        }

        public HelperCallResult Hello(long nowMs, out HelperHello hello, out ulong epoch)
        {
            Assert.True(IsOpen);
            Hellos++;
            hello = HelloValue;
            epoch = Epoch;
            if (HelloResult == HelperCallResult.Failed)
            {
                IsOpen = false;
            }

            return HelloResult;
        }

        public HelperCallResult Snapshot(long nowMs, List<HelperEntry> entries, out ulong epoch)
        {
            Assert.True(IsOpen);
            Snapshots++;
            entries.Clear();
            epoch = Epoch;
            if (SnapshotResult == HelperCallResult.Failed)
            {
                IsOpen = false;
                return SnapshotResult;
            }

            entries.AddRange(Entries);
            return SnapshotResult;
        }

        public void Close() => IsOpen = false;

        public void Dispose()
        {
            IsOpen = false;
            Disposed = true;
        }
    }
}
