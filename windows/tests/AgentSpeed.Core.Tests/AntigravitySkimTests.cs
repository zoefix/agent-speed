using AgentSpeed.Core.Antigravity;

namespace AgentSpeed.Core.Tests;

/// <summary>Skimming a <c>gen_metadata</c> row too large to load (METRICS 4.8).</summary>
public sealed class AntigravitySkimTests
{
    /// <summary>A row as agy writes it: step indices first, then a chat model whose prompt comes before the names.</summary>
    private static byte[] AgyGeneration(int prompt) =>
        Pb.Message(
            Pb.Field(2, Pb.Message(Pb.Varint(5), Pb.Varint(6))),
            Pb.Field(4, "exec-7f21"),
            Pb.Field(1, Pb.Message(
                Pb.Field(1, "(system prompt omitted)"),
                Pb.Field(2, Pb.Field(1, new byte[prompt])),
                Pb.Field(3, 1026),
                Pb.Field(19, "gemini-4-hydrogen-high"),
                Pb.Field(21, "Gemini 4 Hydrogen (High)"))));

    [Fact]
    public void ALargeRowIsSkimmedForItsStepsAndModelWithoutReadingItsPrompt()
    {
        var blob = AgyGeneration(3 << 20);
        var read = 0;
        var generation = AntigravityDecoder.SkimGeneration(7, blob.Length, (offset, size) =>
        {
            read += size;
            return blob[offset..(offset + size)];
        });

        Assert.Equal([5UL, 6UL], generation.StepIndices.Order());
        Assert.Equal(("gemini-4-hydrogen-high", "Gemini 4 Hydrogen (High)"), (generation.Model, generation.Label));
        Assert.True(read < 1024, $"read {read} bytes");
    }

    [Fact]
    public void ASkimStopsAfterItsReadLimit()
    {
        var chat = Enumerable.Range(0, AntigravityDecoder.MaxSkimReads + 1).Select(_ => Pb.Field(2, "m")).Append(Pb.Field(19, "gemini-4-neon"));
        var blob = Pb.Message(Pb.Field(2, Pb.Message(Pb.Varint(1))), Pb.Field(1, Pb.Message([.. chat])));

        Assert.Throws<ProtoFormatException>(() =>
            AntigravityDecoder.SkimGeneration(0, blob.Length, (offset, size) => blob[offset..(offset + size)]));
    }
}
