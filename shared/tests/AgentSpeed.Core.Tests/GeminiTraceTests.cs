using AgentSpeed.Core.Parsing;

namespace AgentSpeed.Core.Tests;

/// <summary>Phase checkpoints of parser-expected.json <c>gemini.traces</c>, replayed like the reference implementation.</summary>
public sealed class GeminiTraceTests
{
    public static TheoryData<string, int, string, long?> Traces()
    {
        var data = new TheoryData<string, int, string, long?>();
        foreach (var trace in ParserFixtureTests.Expected.GetProperty("gemini").GetProperty("traces").EnumerateArray())
        {
            data.Add(
                trace.GetProperty("file").GetString()!,
                trace.GetProperty("afterLine").GetInt32(),
                trace.GetProperty("phase").GetString()!,
                ParserFixtureTests.NullableLong(trace.GetProperty("inFlightStartMs")));
        }

        return data;
    }

    [Fact]
    public void EveryTraceIsListed() => Assert.Equal(26, Traces().Count);

    [Theory]
    [MemberData(nameof(Traces))]
    public void PhaseAfterLineMatchesTrace(string file, int afterLine, string phase, long? inFlightStartMs)
    {
        var deduper = new Deduper();
        foreach (var element in ParserFixtureTests.Expected.GetProperty("order").EnumerateArray())
        {
            var relative = element.GetString()!;
            if (relative == file)
            {
                break;
            }

            ParserFixtureTests.ParseFixture(relative, deduper, new RecordingSink());
        }

        var parser = ParserFixtureTests.ParseFixture(file, deduper, new RecordingSink(), afterLine);

        Assert.Equal(phase, ParserFixtureTests.PhaseName(parser.Phase));
        Assert.Equal(inFlightStartMs, parser.InFlightStartMs);
    }
}
