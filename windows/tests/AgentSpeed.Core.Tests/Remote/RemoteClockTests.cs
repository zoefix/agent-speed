using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RelayLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The clock offset estimate and the conversion of relay times to local time (REMOTE 6).</summary>
public sealed class RemoteClockTests
{
    [Fact]
    public void HelloGivesTheOffsetUntilTheFirstSample()
    {
        var clock = new RemoteClockEstimator();
        Assert.Null(clock.OffsetMs);
        Assert.False(clock.IsSkewedNow);
        clock.NoteHello(remoteNowMs: 10_000, localMs: 3_000);
        Assert.Equal(7_000, clock.OffsetMs);
        Assert.True(clock.IsSkewedNow);
        clock.AddSample(sendMs: 1_000, receiveMs: 1_100, remoteNowMs: 1_060);
        Assert.Equal(10, clock.OffsetMs);
        Assert.False(clock.IsSkewedNow);
    }

    [Fact]
    public void SamplesWithoutHelloGiveTheOffset()
    {
        var clock = new RemoteClockEstimator();
        clock.AddSample(sendMs: 0, receiveMs: 20, remoteNowMs: -9_990);
        Assert.Equal(-10_000, clock.OffsetMs);
        Assert.True(clock.IsSkewedNow);
    }

    [Fact]
    public void SmallestRoundTripOfTheLastEightWins()
    {
        var clock = new RemoteClockEstimator();
        clock.AddSample(sendMs: 0, receiveMs: 10, remoteNowMs: 105);
        clock.AddSample(sendMs: 0, receiveMs: 400, remoteNowMs: 900);
        Assert.Equal(100, clock.OffsetMs);
        for (long index = 0; index < 8; index++)
        {
            clock.AddSample(sendMs: index * 1000, receiveMs: (index * 1000) + 50, remoteNowMs: (index * 1000) + 25 - 2000);
        }

        Assert.Equal(8, clock.Samples.Count);
        Assert.Equal(-2000, clock.OffsetMs);
        clock.AddSample(sendMs: 100_000, receiveMs: 100_003, remoteNowMs: 106_001);
        Assert.Equal(8, clock.Samples.Count);
        Assert.Equal(6000, clock.OffsetMs);
        Assert.True(clock.IsSkewedNow);
    }

    [Fact]
    public void EqualRoundTripsKeepTheOldestOfTheWindow()
    {
        var clock = new RemoteClockEstimator();
        clock.AddSample(sendMs: 0, receiveMs: 10, remoteNowMs: 105);
        clock.AddSample(sendMs: 0, receiveMs: 10, remoteNowMs: 205);
        Assert.Equal(100, clock.OffsetMs);
    }

    [Fact]
    public void ReversedSamplesAreIgnored()
    {
        var clock = new RemoteClockEstimator();
        clock.AddSample(sendMs: 100, receiveMs: 50, remoteNowMs: 9_999_999);
        Assert.Empty(clock.Samples);
        Assert.Null(clock.OffsetMs);
    }

    [Fact]
    public void HalfMillisecondsRound()
    {
        var clock = new RemoteClockEstimator();
        clock.AddSample(sendMs: 0, receiveMs: 1, remoteNowMs: 100);
        Assert.Equal(100, clock.OffsetMs);
        var negative = new RemoteClockEstimator();
        negative.AddSample(sendMs: 0, receiveMs: 1, remoteNowMs: -100);
        Assert.Equal(-101, negative.OffsetMs);
        var large = new RemoteClockEstimator();
        large.AddSample(sendMs: L.BaseMs, receiveMs: L.BaseMs + 3, remoteNowMs: L.BaseMs + 1);
        Assert.Equal(-1, large.OffsetMs);
        var positive = new RemoteClockEstimator();
        positive.AddSample(sendMs: L.BaseMs, receiveMs: L.BaseMs + 3, remoteNowMs: L.BaseMs + 2);
        Assert.Equal(1, positive.OffsetMs);
    }

    [Fact]
    public void SkewThresholdIsFiveSeconds()
    {
        Assert.False(RemoteClockEstimator.IsSkewed(4_999));
        Assert.False(RemoteClockEstimator.IsSkewed(-4_999));
        Assert.True(RemoteClockEstimator.IsSkewed(5_000));
        Assert.True(RemoteClockEstimator.IsSkewed(-5_000));
        Assert.True(RemoteClockEstimator.IsSkewed(long.MinValue + 1));
        Assert.True(new RemoteHostStatus(RemoteStatus.Connected, ClockOffsetMs: -12_000).IsClockSkewed);
        Assert.False(new RemoteHostStatus(RemoteStatus.Connected).IsClockSkewed);
        Assert.False(new RemoteHostStatus(RemoteStatus.Connected, ClockOffsetMs: 4_999).IsClockSkewed);
    }

    [Fact]
    public void ConversionShiftsEveryTimeAndKeepsDurations()
    {
        var response = L.Message(L.Response());
        var agent = L.Message(L.Agent());
        var shifted = Assert.IsType<RelayMessage.Response>(response.Converted(3_000)).Value.Record;
        Assert.Equal(L.BaseMs - 3_000, shifted.StartMs);
        Assert.Equal(L.BaseMs + 2000 - 3_000, shifted.EndMs);
        Assert.Equal(L.BaseMs + 300 - 3_000, shifted.FirstMs);
        Assert.Equal(Assert.IsType<RelayMessage.Response>(response).Value.Record.Tokens, shifted.Tokens);

        var moved = Assert.IsType<RelayMessage.Agent>(agent.Converted(-1_000)).Value;
        Assert.Equal(L.BaseMs + 1_000, moved.InFlightStartMs);
        Assert.Equal(L.BaseMs + 1_500, moved.LastLineMs);
        Assert.Equal(L.BaseMs + 1_000, moved.Actions[0].AtMs);
        Assert.Equal(L.BaseMs + 1_400, moved.Actions[0].ResultMs);
        Assert.Equal(Assert.IsType<RelayMessage.Agent>(agent).Value.Actions[0].Digest, moved.Actions[0].Digest);

        var tool = L.Message("""{"t":"tool","agent":"/a","source":"codex","callMs":1780000000000,"resultMs":1780000001000,"failed":true,"model":null,"seeded":false}""");
        var call = Assert.IsType<RelayMessage.Tool>(tool.Converted(500)).Value.Call;
        Assert.Equal(1_780_000_000_000 - 500, call.AtMs);
        Assert.Equal(1_780_000_001_000 - 500, call.ResultMs);
        Assert.True(call.Failed);

        Assert.Equal(new RelayMessage.Ready(), new RelayMessage.Ready().Converted(99));
        var clock = new RelayMessage.Clock(1, L.BaseMs);
        Assert.Same(clock, clock.Converted(99));
    }

    [Fact]
    public void NullTimesStayNull()
    {
        var agent = L.Message(L.Agent(a =>
        {
            a["inFlightStartMs"] = null;
            a["lastLineMs"] = null;
            a["actions"] = new List<object?> { L.Action(resultMs: null) };
        }));
        var moved = Assert.IsType<RelayMessage.Agent>(agent.Converted(1_000)).Value;
        Assert.Null(moved.InFlightStartMs);
        Assert.Null(moved.LastLineMs);
        Assert.Null(moved.Actions[0].ResultMs);
        Assert.Equal(L.BaseMs - 1_000, moved.Actions[0].AtMs);
    }

    [Fact]
    public void ConversionThatLeavesTheRangeIsInvalid()
    {
        var response = L.Message(L.Response(r =>
        {
            r["endMs"] = RemoteFields.EarliestMs + 10;
            r["startMs"] = RemoteFields.EarliestMs;
            r["firstMs"] = null;
        }));
        Assert.NotNull(response.Converted(-5));
        Assert.NotNull(response.Converted(0));
        Assert.Null(response.Converted(1));
        Assert.Null(response.Converted(RemoteFields.LatestMs));
        Assert.Null(response.Converted(long.MinValue));
        Assert.Null(response.Converted(long.MaxValue));

        var late = L.Message(L.Agent(a => a["lastLineMs"] = RemoteFields.LatestMs));
        Assert.NotNull(late.Converted(0));
        Assert.Null(late.Converted(-1));
        var action = L.Message(L.Agent(a => a["actions"] = new List<object?> { L.Action(callMs: RemoteFields.EarliestMs, resultMs: null) }));
        Assert.Null(action.Converted(1));
        var tool = L.Message($$"""{"t":"tool","agent":"/a","source":"codex","callMs":{{RemoteFields.EarliestMs}},"resultMs":{{RemoteFields.LatestMs}},"failed":null,"model":null,"seeded":false}""");
        Assert.NotNull(tool.Converted(0));
        Assert.Null(tool.Converted(1));
        Assert.Null(tool.Converted(-1));
    }
}
