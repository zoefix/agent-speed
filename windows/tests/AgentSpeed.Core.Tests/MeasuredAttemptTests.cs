using AgentSpeed.Core.Engine;
using AgentSpeed.Core.Parsing;
using AgentSpeed.Core.Perf;
using AgentSpeed.Core.WorkBuddy;

namespace AgentSpeed.Core.Tests;

/// <summary>
/// METRICS 4.9 <i>Measured attempts</i> (CLI 2.184.0 and later) for every source of the WorkBuddy format: the last element of
/// <c>rawUsage.model_calls</c> gives a call's start and first token, anything else keeps the inferred start.
/// </summary>
public sealed class MeasuredAttemptTests
{
    private const long T = 1_792_746_000_000;
    private const string Root = "/w/projects";
    private const string Session = "/w/projects/Users-me-nova/3aa9b700.jsonl";

    private readonly RecordingSink _sink = new();

    public static TheoryData<SourceKind> Sources() => [SourceKind.WorkBuddy, SourceKind.WorkBuddyAi, SourceKind.CodeBuddyCode];

    [Theory]
    [MemberData(nameof(Sources))]
    public void TheLastAttemptGivesTheStartAndTheFirstToken(SourceKind source)
    {
        var response = Respond(T + 9000, """[{"attempt_no":1,"duration_ms":4800,"result":"success","time_to_first_token_ms":900}]""", source);

        Assert.Equal((source, (long?)(T + 4200), T + 9000, (long?)(T + 5100)), (response.Source, response.StartMs, response.EndMs, response.FirstMs));
    }

    [Fact]
    public void EarlierFailedAttemptsAreNotPartOfTheCall()
    {
        var response = Respond(T + 9000, """[{"attempt_no":1,"duration_ms":3000,"result":"failed"},{"attempt_no":2,"duration_ms":2500,"time_to_first_token_ms":400}]""");

        Assert.Equal(((long?)(T + 6500), (long?)(T + 6900)), (response.StartMs, response.FirstMs));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"duration_ms":900}""")]
    [InlineData("""[{"duration_ms":900},"(text)"]""")]
    [InlineData("""[{"duration_ms":900},null]""")]
    [InlineData("""[{"duration_ms":900},[{"duration_ms":900}]]""")]
    [InlineData("""[{"duration_ms":"2500"}]""")]
    [InlineData("""[{"duration_ms":true}]""")]
    [InlineData("""[{"duration_ms":null}]""")]
    [InlineData("""[{"duration_ms":0.9,"time_to_first_token_ms":0.5}]""")]
    [InlineData("""[{"duration_ms":0}]""")]
    [InlineData("""[{"duration_ms":-1200}]""")]
    [InlineData("""[{"duration_ms":1e999}]""")]
    [InlineData("""[{"duration_ms":-1e999}]""")]
    [InlineData("""[{"time_to_first_token_ms":300}]""")]
    [InlineData("""[{"duration_ms":900,"duration_ms":"x"}]""")]
    [InlineData("\"model_calls\"")]
    public void AnythingElseKeepsTheInferredStart(string attempts)
    {
        var response = Respond(T + 9000, attempts);

        Assert.Equal(((long?)T, (long?)null), (response.StartMs, response.FirstMs));
    }

    [Theory]
    [InlineData("1200.9", "1200.4", 1200L)]
    [InlineData("1200", "1200", 1200L)]
    [InlineData("1500", "1500.7", 1500L)]
    [InlineData("2400.7", "600.9", 600L)]
    [InlineData("1200", "1201", null)]
    [InlineData("1200000", "-5", null)]
    [InlineData("1200", "0", null)]
    [InlineData("1200", "0.4", null)]
    [InlineData("1200", "\"300\"", null)]
    [InlineData("1200", "1e999", null)]
    [InlineData("1200", "true", null)]
    public void TheFirstTokenCountsWhenItIsAboveZeroAndWithinTheDuration(string duration, string first, long? firstAfterStart)
    {
        var response = Respond(T + 9000, "[{\"duration_ms\":" + duration + ",\"time_to_first_token_ms\":" + first + "}]");
        var start = T + 9000 - (long)double.Parse(duration, System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal(((long?)start, firstAfterStart is { } after ? start + after : null), (response.StartMs, response.FirstMs));
    }

    [Fact]
    public void ARepeatedRawUsageOrModelCallsKeyTakesItsLastValue()
    {
        var usage = WorkBuddyLines.Usage(1000, 50) + ""","rawUsage":{"model_calls":[{"duration_ms":4000}]},"rawUsage":{"prompt_tokens":1000}""";
        Assert.Equal(((long?)T, (long?)null), Times(Single(usage)));

        usage = WorkBuddyLines.Usage(1000, 50) + ""","rawUsage":{"model_calls":[],"model_calls":[{"duration_ms":4000}]}""";
        Assert.Equal(((long?)(T + 5000), (long?)null), Times(Single(usage)));

        usage = WorkBuddyLines.Usage(1000, 50) + ""","rawUsage":{"model_calls":[{"duration_ms":4000}]},"rawUsage":[]""";
        Assert.Equal(((long?)T, (long?)null), Times(Single(usage)));
    }

    /// <summary>A call whose first usage line has no attempts takes the start of a later line of it that has; the first measured start stays.</summary>
    [Fact]
    public void ALaterLineOfTheCallMeasuresItOnceAndTheFirstMeasuredStartIsKept()
    {
        var parser = Parser(SourceKind.CodeBuddyCode);
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.Say(T + 3000, "a1", "m1", WorkBuddyLines.Usage(1000, 30)));
        Assert.Equal(((long?)T, (long?)null), Times(_sink.Latest.Single()));

        Feed.Lines(parser, _sink, WorkBuddyLines.ModelUsage(T + 2990, "mu1", "m1", Raw("""[{"duration_ms":2000,"time_to_first_token_ms":700}]""")));
        Assert.Equal(((long?)(T + 990), (long?)(T + 1690), T + 3000), (_sink.Latest.Single().StartMs, _sink.Latest.Single().FirstMs, _sink.Latest.Single().EndMs));

        Feed.Lines(parser, _sink, WorkBuddyLines.ModelUsage(T + 3500, "mu2", "m1", Raw("""[{"duration_ms":100,"time_to_first_token_ms":50}]""")));
        Assert.Equal(((long?)(T + 990), (long?)(T + 1690), T + 3500), (_sink.Latest.Single().StartMs, _sink.Latest.Single().FirstMs, _sink.Latest.Single().EndMs));
        Assert.Equal([true, false, false], _sink.Emissions.Select(e => e.IsCompletion));
    }

    [Fact]
    public void AMeasuredCallKeepsItsStartWhenALaterLineHasNone()
    {
        var parser = Parser(SourceKind.WorkBuddy);
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.Say(T + 3000, "a1", "m1", Raw("""[{"duration_ms":1500,"time_to_first_token_ms":400}]""")),
            WorkBuddyLines.ModelUsage(T + 3010, "mu", "m1", WorkBuddyLines.Usage(1000, 31)));

        var response = _sink.Latest.Single();
        Assert.Equal(((long?)(T + 1500), (long?)(T + 1900), T + 3010, 31L), (response.StartMs, response.FirstMs, response.EndMs, response.Tokens.Output));
    }

    /// <summary>The measured start replaces the inferred one without changing the phase or the next call's inferred start.</summary>
    [Fact]
    public void AMeasuredCallLeavesThePhaseAndTheNextInferredStartAlone()
    {
        var parser = Parser(SourceKind.CodeBuddyCode);
        Feed.Lines(parser, _sink,
            WorkBuddyLines.User(T, "u1"),
            WorkBuddyLines.ToolCall(T + 3000, "c1", "m1", "call_1", Raw("""[{"duration_ms":2500,"time_to_first_token_ms":3000}]""")));
        Assert.Equal((AgentPhase.Tools, (long?)null), (parser.Phase, parser.InFlightStartMs));

        Feed.Lines(parser, _sink,
            WorkBuddyLines.ToolResult(T + 3100, "r1", "m1", "call_1"),
            WorkBuddyLines.Say(T + 6000, "a2", "m2", WorkBuddyLines.Usage(1100, 20)));

        Assert.Equal([((long?)(T + 500), (long?)null), ((long?)(T + 3100), (long?)null)], _sink.Latest.Select(Times));
        Assert.Equal(AgentPhase.Idle, parser.Phase);
    }

    /// <summary>A start before the 64-bit range saturates (METRICS 3.6); the first token is counted from it.</summary>
    [Fact]
    public void StartsSaturate()
    {
        var early = Respond("\"0001-01-01T00:00:00Z\"", """[{"duration_ms":9223372036854775807,"time_to_first_token_ms":1000}]""");
        Assert.Equal(((long?)long.MinValue, (long?)(long.MinValue + 1000)), (early.StartMs, early.FirstMs));

        var huge = Respond((T + 9000).ToString(System.Globalization.CultureInfo.InvariantCulture), """[{"duration_ms":1e300,"time_to_first_token_ms":99999999999999999999}]""");
        Assert.Equal(((long?)(T + 9000 - long.MaxValue), (long?)(T + 9000)), (huge.StartMs, huge.FirstMs));
    }

    /// <summary>A saturated start and a later end can lie farther apart than 64 bits hold: such a call is never measured (METRICS 3.3).</summary>
    [Fact]
    public void ASpanBeyondSixtyFourBitsIsNeverMeasured()
    {
        var parser = Parser(SourceKind.CodeBuddyCode);
        var say = WorkBuddyLines.Say(T, "a1", "m1", Raw("""[{"duration_ms":9223372036854775807,"time_to_first_token_ms":1000}]"""));
        Feed.Lines(parser, _sink,
            say.Replace("\"timestamp\":" + T, "\"timestamp\":\"0001-01-01T00:00:00Z\"", StringComparison.Ordinal),
            WorkBuddyLines.ModelUsage(T, "mu", "m1", WorkBuddyLines.Usage(1000, 30)).Replace("\"timestamp\":" + T, "\"timestamp\":1e19", StringComparison.Ordinal));

        var response = _sink.Latest.Single();
        Assert.Equal(((long?)long.MinValue, long.MaxValue), (response.StartMs, response.EndMs));
        Assert.False(SpeedMath.TryGetSpeedInterval(response, out _));
        Assert.Equal(1000, SpeedMath.LatencyMs(response));
        Assert.Null(SpeedMath.LatencyMs(response with { FirstMs = long.MaxValue }));
        Assert.Equal(0, PerfEvaluator.DurationMs(response));

        Assert.False(SpeedMath.TrySpan(long.MinValue, long.MaxValue, out _));
        Assert.False(SpeedMath.TrySpan(5, 4, out _));
        Assert.True(SpeedMath.TrySpan(long.MinValue, -1, out var span) && span == long.MaxValue);
        Assert.True(SpeedMath.TryGetSpeedInterval(response with { EndMs = long.MinValue + 100 }, out var widened));
        Assert.Equal((long.MinValue, long.MinValue + 100), (widened.StartMs, widened.EndMs));
    }

    [Fact]
    public void OnlyTheSourcesOfTheFormatHaveThisParser()
    {
        Assert.Equal(SourceKind.CodeBuddyCode, new WorkBuddyParser(Session, new Deduper(), SourceKind.CodeBuddyCode, Root).Source);
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkBuddyParser(Session, new Deduper(), SourceKind.QoderCli, Root));
        Assert.True(WorkBuddyPaths.WritesWorkBuddyFormat(SourceKind.CodeBuddyCode));
        Assert.False(WorkBuddyPaths.IsWorkBuddy(SourceKind.CodeBuddyCode));
    }

    private static string Raw(string attempts) => WorkBuddyLines.Usage(1000, 30) + ",\"rawUsage\":{\"prompt_tokens\":1000,\"model_calls\":" + attempts + "}";

    private static (long?, long?) Times(ParsedResponse response) => (response.StartMs, response.FirstMs);

    private static WorkBuddyParser Parser(SourceKind source) => new(Session, new Deduper(), source, Root);

    private ParsedResponse Respond(long endMs, string attempts, SourceKind source = SourceKind.CodeBuddyCode)
    {
        Feed.Lines(Parser(source), _sink, WorkBuddyLines.User(T, "u1"), WorkBuddyLines.Say(endMs, "a1", "m1", Raw(attempts)));
        return _sink.Latest.Single();
    }

    // The usage line alone, with a raw timestamp literal.
    private ParsedResponse Respond(string timestamp, string attempts)
    {
        var line = WorkBuddyLines.Say(T, "a1", "m1", Raw(attempts)).Replace("\"timestamp\":" + T, "\"timestamp\":" + timestamp, StringComparison.Ordinal);
        Feed.Lines(Parser(SourceKind.CodeBuddyCode), _sink, line);
        return _sink.Latest.Single();
    }

    private ParsedResponse Single(string usage)
    {
        Feed.Lines(Parser(SourceKind.CodeBuddyCode), _sink, WorkBuddyLines.User(T, "u1"), WorkBuddyLines.Say(T + 9000, "a1", "m1", usage));
        return _sink.Latest.Single();
    }
}
