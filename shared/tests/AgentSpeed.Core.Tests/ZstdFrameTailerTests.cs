using System.Text;
using AgentSpeed.Core.IO;
using AgentSpeed.Core.Parsing;
using static AgentSpeed.Core.Tests.ZstdFrames;

namespace AgentSpeed.Core.Tests;

public sealed class ZstdFrameTailerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void EveryFrameIsDecodedAndLinesMayCrossFrameBoundaries()
    {
        var path = Write(_temp.Combine("s", "session.v4.jsonl.zstd"), Lines("one", "two"), Text("thr"), Text("ee\nfour\n"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        var result = tailer.ReadAppended(lines);

        Assert.Equal(["one", "two", "three", "four"], lines.Take());
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
        Assert.Equal(new FileInfo(path).Length, result.BytesRead);
        Assert.Equal(0, lines.Malformed);
    }

    [Fact]
    public void FramesWithAContentSizeAreDecodedToo()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), Frame("sized\n"u8, contentSize: true), Lines("plain"));
        var lines = new LineCollector();

        new ZstdFrameTailer(path).ReadAppended(lines);

        Assert.Equal(["sized", "plain"], lines.Take());
    }

    [Fact]
    public void IncompleteTrailingFrameWaitsForItsRemainingBytes()
    {
        var second = Lines("second", "third");
        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("first"), second[..5]);
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(["first"], lines.Take());
        var frameStart = tailer.Offset;
        Assert.Equal(new FileInfo(path).Length - 5, frameStart);

        Append(path, second[5..^1]);
        Assert.Equal(0, tailer.ReadAppended(lines).BytesRead);
        Assert.Empty(lines.Take());
        Assert.Equal(frameStart, tailer.Offset);

        Append(path, second[^1..]);
        tailer.ReadAppended(lines);
        Assert.Equal(["second", "third"], lines.Take());
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
        Assert.Equal(0, lines.Malformed);
    }

    [Fact]
    public void LineWithoutNewlineIsBufferedUntilALaterFrameEndsIt()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), Text("{\"a\":"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());

        Append(path, Text("1}\n"));
        tailer.ReadAppended(lines);
        Assert.Equal(["{\"a\":1}"], lines.Take());
    }

    [Fact]
    public void CorruptFrameIsSkippedAndCountsAsOneMalformedLine()
    {
        var corrupt = Lines("this frame is damaged beyond repair");
        corrupt[^6] ^= 0xFF;
        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("before"), corrupt, Lines("after"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);

        Assert.Equal(["before", "after"], lines.Take());
        Assert.Equal(1, lines.Malformed);
        Assert.Equal(1, tailer.MalformedFrames);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void BytesThatAreNotAFrameCountOnceAndTheStreamResynchronizes()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("before"), "garbage that is no frame at all"u8.ToArray(), Lines("after"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(["before", "after"], lines.Take());
        Assert.Equal(1, lines.Malformed);

        Append(path, Lines("later"));
        tailer.ReadAppended(lines);
        Assert.Equal(["later"], lines.Take());
        Assert.Equal(1, lines.Malformed);
    }

    [Theory]
    [InlineData((byte)0x0)]
    [InlineData((byte)0xF)]
    public void ResynchronizationAlsoStopsAtASkippableFrame(byte variant)
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), "not a frame"u8.ToArray(), Skippable(12, variant), Lines("after"));
        var lines = new LineCollector();

        new ZstdFrameTailer(path).ReadAppended(lines);

        Assert.Equal(["after"], lines.Take());
        Assert.Equal(1, lines.Malformed);
    }

    [Fact]
    public void WithoutAMagicYetTheReaderWaitsAndCountsOnlyOnce()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), "no magic here"u8.ToArray());
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Append(path, "still nothing"u8.ToArray());
        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());
        Assert.Equal(1, lines.Malformed);
        Assert.True(tailer.Offset < new FileInfo(path).Length);

        Append(path, Lines("found"));
        tailer.ReadAppended(lines);
        Assert.Equal(["found"], lines.Take());
        Assert.Equal(1, lines.Malformed);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void ResynchronizationKeepsAMagicNumberSplitAcrossAppends()
    {
        var frame = Lines("next");
        var path = Write(_temp.Combine("s.jsonl.zstd"), "junk"u8.ToArray(), frame[..2]);
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());

        Append(path, frame[2..]);
        tailer.ReadAppended(lines);
        Assert.Equal(["next"], lines.Take());
        Assert.Equal(1, lines.Malformed);
    }

    [Fact]
    public void SkippableFramesAreSkippedSilently()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), Skippable(16), Lines("a"), Skippable(0, variant: 0xF), Lines("b"));
        var lines = new LineCollector();

        new ZstdFrameTailer(path).ReadAppended(lines);

        Assert.Equal(["a", "b"], lines.Take());
        Assert.Equal(0, lines.Malformed);
    }

    [Fact]
    public void IncompleteSkippableFrameWaits()
    {
        var skippable = Skippable(32);
        var path = Write(_temp.Combine("s.jsonl.zstd"), skippable[..20]);
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(0, tailer.Offset);

        Append(path, skippable[20..], Lines("x"));
        tailer.ReadAppended(lines);
        Assert.Equal(["x"], lines.Take());
        Assert.Equal(0, lines.Malformed);
    }

    [Fact]
    public void FrameOverTheOutputCapIsMalformedAndOneAtTheCapIsNot()
    {
        var atCap = new byte[ZstdFrameTailer.MaxFrameOutputBytes];
        Array.Fill(atCap, (byte)'a');
        for (var i = 1024 * 1024 - 1; i < atCap.Length; i += 1024 * 1024)
        {
            atCap[i] = (byte)'\n';
        }

        var overCap = new byte[atCap.Length + 1];
        atCap.CopyTo(overCap, 0);
        overCap[^1] = (byte)'\n';
        var path = Write(_temp.Combine("s.jsonl.zstd"), Frame(overCap), Frame(atCap), Lines("tail"));
        var counter = new CountingSink();

        new ZstdFrameTailer(path).ReadAppended(counter);

        Assert.Equal(1, counter.Malformed);
        Assert.Equal(64 + 1, counter.Lines);
        Assert.Equal("tail", counter.Last);
    }

    [Fact]
    public void FrameLargerThanTheReadWindowIsBufferedWhole()
    {
        var random = new Random(7);
        var builder = new StringBuilder();
        while (builder.Length < 3 * 1024 * 1024)
        {
            builder.Append(Convert.ToBase64String(RandomBytes(random, 3000))).Append('\n');
        }

        var big = Text(builder.ToString());
        Assert.True(big.Length > 2 * 1024 * 1024);
        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("first"), big[..(big.Length / 2)]);
        var tailer = new ZstdFrameTailer(path);
        var counter = new CountingSink();

        tailer.ReadAppended(counter);
        Assert.Equal(1, counter.Lines);

        Append(path, big[(big.Length / 2)..], Lines("last"));
        tailer.ReadAppended(counter);
        Assert.Equal(builder.ToString().Count(c => c == '\n') + 2, counter.Lines);
        Assert.Equal("last", counter.Last);
        Assert.Equal(0, counter.Malformed);
    }

    [Fact]
    public void StartingAtTheEndReadsOnlyFramesAppendedLater()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("old-1"), Lines("old-2"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        tailer.StartAt(new FileInfo(path).Length, StartAlignment.SkipPartialLine);
        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());

        Append(path, Lines("new-1"), Lines("new-2"));
        tailer.ReadAppended(lines);
        Assert.Equal(["new-1", "new-2"], lines.Take());
    }

    [Fact]
    public void ShrinkingFileResetsAndRereadsFromTheStart()
    {
        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("a"), Lines("b"), Lines("c"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        Assert.Equal(["a", "b", "c"], lines.Take());

        Write(path, Lines("fresh"));
        var result = tailer.ReadAppended(lines);

        Assert.True(result.WasReset);
        Assert.Equal(["fresh"], lines.Take());
    }

    [Fact]
    public void ReplacedFileResetsWhenItsIdentityChanges()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        var path = Write(_temp.Combine("s.jsonl.zstd"), Lines("a"));
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();
        tailer.ReadAppended(lines);
        lines.Take();

        var replacement = Write(_temp.Combine("next.jsonl.zstd"), Lines("x"), Lines("y"));
        File.SetCreationTimeUtc(replacement, File.GetCreationTimeUtc(path).AddMinutes(1));
        File.Move(replacement, path, overwrite: true);
        var result = tailer.ReadAppended(lines);

        Assert.True(result.WasReset);
        Assert.Equal(["x", "y"], lines.Take());
    }

    [Fact]
    public void PlainSessionFilesUseTheLineTailer()
    {
        Assert.IsType<ZstdFrameTailer>(TranscriptTailer.For("/x/session.v4.jsonl.zstd"));
        Assert.IsType<FileTailer>(TranscriptTailer.For("/x/session.v4.jsonl"));
        Assert.IsType<FileTailer>(TranscriptTailer.For("/x/rollout.jsonl"));
    }

    [Fact]
    public void OpenFileStaysWritableAndDeletableWhileRead()
    {
        var path = _temp.Combine("shared.jsonl.zstd");
        var tailer = new ZstdFrameTailer(path);
        var lines = new LineCollector();

        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write(Lines("a"));
            writer.Flush();
            tailer.ReadAppended(lines);
            Assert.Equal(["a"], lines.Take());

            writer.Write(Lines("b"));
            writer.Flush();
            tailer.ReadAppended(lines);
            Assert.Equal(["b"], lines.Take());

            File.Delete(path);
            Assert.False(File.Exists(path));
        }
    }

    private static byte[] RandomBytes(Random random, int count)
    {
        var bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }

    private sealed class CountingSink : ILineSink
    {
        public int Lines { get; private set; }

        public int Malformed { get; private set; }

        public string? Last { get; private set; }

        public void OnLine(ReadOnlySpan<byte> line)
        {
            Lines++;
            Last = line.Length < 64 ? Encoding.UTF8.GetString(line) : null;
        }

        public void OnMalformed() => Malformed++;
    }
}
