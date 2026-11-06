using System.Text;
using AgentSpeed.Core.Remote;
using L = AgentSpeed.Core.Tests.Remote.RelayLines;

namespace AgentSpeed.Core.Tests.Remote;

/// <summary>The relay's stdout split into lines of at most 1 MiB (REMOTE 5.1).</summary>
public sealed class RemoteLineFramerTests
{
    [Fact]
    public void FramerCapsLinesAt1MiB()
    {
        var framer = new RemoteLineFramer();
        var big = Enumerable.Repeat((byte)'x', RemoteLineFramer.MaxLineBytes + 1).ToArray();
        var (lines, oversized) = framer.Consume("{\"t\":\"ready\"}\n{\"t\":"u8);
        Assert.Equal(["{\"t\":\"ready\"}"], Texts(lines));
        Assert.Equal(0, oversized);
        (lines, oversized) = framer.Consume([.. "\"ready\"}\n"u8, .. big, .. "\n{}\n"u8]);
        Assert.Equal(["{\"t\":\"ready\"}", "{}"], Texts(lines));
        Assert.Equal(1, oversized);
    }

    [Fact]
    public void LinesSplitAcrossChunksAreJoined()
    {
        var framer = new RemoteLineFramer();
        var line = Encoding.UTF8.GetBytes(L.Agent(a => a["project"] = "專案"));
        var collected = new List<byte[]>();
        for (var start = 0; start < line.Length; start += 7)
        {
            var (lines, oversized) = framer.Consume(line.AsSpan(start, Math.Min(7, line.Length - start)));
            Assert.Equal(0, oversized);
            collected.AddRange(lines);
        }

        Assert.Empty(collected);
        var (last, _) = framer.Consume("\n"u8);
        Assert.Equal(line, Assert.Single(last));
        var agent = Assert.IsType<RelayMessage.Agent>(L.Message(Encoding.UTF8.GetString(last[0]))).Value;
        Assert.Equal("專案", agent.Project);
    }

    [Fact]
    public void OversizedLineIsCountedOnceAndTheNextLineStillDecodes()
    {
        var framer = new RemoteLineFramer();
        var piece = Enumerable.Repeat((byte)'x', 600 * 1024).ToArray();
        var total = 0;
        var kept = new List<byte[]>();
        foreach (var chunk in new byte[][] { piece, piece, piece, [.. "yy\n"u8, .. Encoding.UTF8.GetBytes(L.Ready), .. "\n"u8] })
        {
            var (lines, oversized) = framer.Consume(chunk);
            total += oversized;
            kept.AddRange(lines);
        }

        Assert.Equal(1, total);
        var ready = Assert.Single(kept);
        Assert.Equal(new RelayMessage.Ready(), L.Message(Encoding.UTF8.GetString(ready)));
    }

    [Fact]
    public void ALineOfExactly1MiBIsKept()
    {
        var framer = new RemoteLineFramer();
        var half = Enumerable.Repeat((byte)'x', RemoteLineFramer.MaxLineBytes / 2).ToArray();
        Assert.Empty(framer.Consume(half).Lines);
        Assert.Empty(framer.Consume(half).Lines);
        var (lines, oversized) = framer.Consume("\n"u8);
        Assert.Equal(0, oversized);
        Assert.Equal(RemoteLineFramer.MaxLineBytes, Assert.Single(lines).Length);

        var (more, moreOversized) = framer.Consume([.. half, .. half, (byte)'x', (byte)'\n']);
        Assert.Empty(more);
        Assert.Equal(1, moreOversized);
    }

    [Fact]
    public void SeveralOversizedLinesInOneChunkCountEach()
    {
        var framer = new RemoteLineFramer();
        var big = Enumerable.Repeat((byte)'x', RemoteLineFramer.MaxLineBytes + 1).ToArray();
        var (lines, oversized) = framer.Consume([.. big, (byte)'\n', .. big, (byte)'\n', .. "{}\n"u8]);
        Assert.Equal(2, oversized);
        Assert.Equal(["{}"], Texts(lines));
    }

    [Fact]
    public void EmptyLinesAndAnIncompleteTailAreKeptForTheDecoder()
    {
        var framer = new RemoteLineFramer();
        var (lines, oversized) = framer.Consume("\n\n{\"t\":"u8);
        Assert.Equal(["", ""], Texts(lines));
        Assert.Equal(0, oversized);
        Assert.Equal(RemoteLineErrorKind.NotJson, L.Invalid("")?.Kind);
        Assert.Empty(framer.Consume([]).Lines);
        Assert.Equal(["{\"t\":\"ready\"}"], Texts(framer.Consume("\"ready\"}\n"u8).Lines));
    }

    private static List<string> Texts(IEnumerable<byte[]> lines) => [.. lines.Select(l => Encoding.UTF8.GetString(l))];
}
