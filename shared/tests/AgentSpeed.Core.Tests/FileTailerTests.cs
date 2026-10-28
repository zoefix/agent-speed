using System.Text;
using AgentSpeed.Core.IO;

namespace AgentSpeed.Core.Tests;

public sealed class FileTailerTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void PartialTrailingLineIsBufferedUntilNewline()
    {
        var path = _temp.WriteFile("a.jsonl", "first\nsec");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(["first"], lines.Take());

        Append(path, "ond");
        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());

        Append(path, "\nthird\n");
        var result = tailer.ReadAppended(lines);
        Assert.Equal(["second", "third"], lines.Take());
        Assert.False(result.WasReset);
        Assert.Equal(new FileInfo(path).Length, tailer.Offset);
    }

    [Fact]
    public void AppendedLinesAreReadIncrementally()
    {
        var path = _temp.WriteFile("a.jsonl", string.Empty);
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        var empty = tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());
        Assert.Equal(0, empty.BytesRead);

        Append(path, "one\n");
        Assert.Equal(4, tailer.ReadAppended(lines).BytesRead);
        Assert.Equal(["one"], lines.Take());

        Append(path, "two\nthree\n");
        tailer.ReadAppended(lines);
        Assert.Equal(["two", "three"], lines.Take());

        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());
    }

    [Fact]
    public void CarriageReturnsBeforeNewlinesAreStripped()
    {
        var path = _temp.WriteFile("a.jsonl", "x\r\ny\r\n\r\nz\r");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(["x", "y", ""], lines.Take());

        Append(path, "\n");
        tailer.ReadAppended(lines);
        Assert.Equal(["z"], lines.Take());
    }

    [Fact]
    public void ShrinkingFileResetsAndRereadsFromStart()
    {
        var path = _temp.WriteFile("a.jsonl", "line-one\nline-two\n");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        Assert.False(tailer.ReadAppended(lines).WasReset);
        Assert.Equal(["line-one", "line-two"], lines.Take());

        File.WriteAllText(path, "new\npart");
        var result = tailer.ReadAppended(lines);
        Assert.True(result.WasReset);
        Assert.Equal(["new"], lines.Take());

        Append(path, "ial\n");
        Assert.False(tailer.ReadAppended(lines).WasReset);
        Assert.Equal(["partial"], lines.Take());
    }

    [Fact]
    public void OversizedLineIsSkippedAndCounted()
    {
        var path = _temp.Combine("big.jsonl");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            stream.Write("before\n"u8);
            var chunk = new byte[1024 * 1024];
            Array.Fill(chunk, (byte)'a');
            for (var written = 0L; written <= FileTailer.MaxLineBytes; written += chunk.Length)
            {
                stream.Write(chunk);
            }
        }

        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        Assert.Equal(["before"], lines.Take());

        Append(path, "aaaa\nafter\n");
        tailer.ReadAppended(lines);
        Assert.Equal(["after"], lines.Take());
        Assert.Equal(1, tailer.OversizedLines);

        Append(path, "next\n");
        tailer.ReadAppended(lines);
        Assert.Equal(["next"], lines.Take());
        Assert.Equal(1, tailer.OversizedLines);
    }

    [Fact]
    public void WriterWithSharingCanAppendAndDeleteBetweenReads()
    {
        var path = _temp.Combine("shared.jsonl");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            writer.Write("a\n"u8);
            writer.Flush();
            tailer.ReadAppended(lines);
            Assert.Equal(["a"], lines.Take());

            writer.Write("b\n"u8);
            writer.Flush();
            tailer.ReadAppended(lines);
            Assert.Equal(["b"], lines.Take());

            File.Delete(path);
            Assert.False(File.Exists(path));
        }

        Assert.ThrowsAny<IOException>(() => tailer.ReadAppended(lines));
    }

    [Fact]
    public void TailerHoldsNoHandleBetweenReads()
    {
        var path = _temp.WriteFile("a.jsonl", "a\n");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.ReadAppended(lines);
        using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            exclusive.Seek(0, SeekOrigin.End);
            exclusive.Write("b\n"u8);
        }

        tailer.ReadAppended(lines);
        Assert.Equal(["a", "b"], lines.Take());
        File.Delete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void StartAtWithAlignmentDropsThePartialFirstLine()
    {
        var path = _temp.WriteFile("a.jsonl", "abc\ndef\nghi\n");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.StartAt(2, StartAlignment.SkipToNextLine);
        tailer.ReadAppended(lines);
        Assert.Equal(["def", "ghi"], lines.Take());
    }

    [Fact]
    public void StartAtWithAlignmentWaitsForTheFirstNewline()
    {
        var path = _temp.WriteFile("a.jsonl", "abcdef");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.StartAt(2, StartAlignment.SkipToNextLine);
        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());

        Append(path, "gh\nxyz\n");
        tailer.ReadAppended(lines);
        Assert.Equal(["xyz"], lines.Take());
    }

    [Fact]
    public void StartAtWithoutAlignmentKeepsTheFirstLine()
    {
        var path = _temp.WriteFile("a.jsonl", "abc\ndef\n");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.StartAt(4);
        tailer.ReadAppended(lines);
        Assert.Equal(["def"], lines.Take());

        var fromZero = new FileTailer(path);
        fromZero.StartAt(0, StartAlignment.SkipToNextLine);
        fromZero.ReadAppended(lines);
        Assert.Equal(["abc", "def"], lines.Take());
    }

    [Theory]
    [InlineData((int)StartAlignment.SkipPartialLine, 4, new[] { "def", "ghi" })]
    [InlineData((int)StartAlignment.SkipPartialLine, 5, new[] { "ghi" })]
    [InlineData((int)StartAlignment.SkipToNextLine, 4, new[] { "ghi" })]
    public void PartialLineAlignmentKeepsALineThatStartsAtTheOffset(int alignment, long offset, string[] expected)
    {
        var path = _temp.WriteFile("a.jsonl", "abc\ndef\n");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        tailer.StartAt(offset, (StartAlignment)alignment);
        tailer.ReadAppended(lines);
        Append(path, "ghi\n");
        tailer.ReadAppended(lines);

        Assert.Equal(expected, lines.Take());
    }

    [Fact]
    public void PrimeSkipsExistingContent()
    {
        var path = _temp.WriteFile("a.jsonl", "old-1\nold-2\n");
        var tailer = new FileTailer(path);
        var lines = new LineCollector();

        var primed = tailer.Prime(new FileInfo(path).Length);
        Assert.Equal(new FileInfo(path).Length, primed.Length);

        tailer.ReadAppended(lines);
        Assert.Empty(lines.Take());

        Append(path, "new\n");
        tailer.ReadAppended(lines);
        Assert.Equal(["new"], lines.Take());
    }

    private static void Append(string path, string text)
    {
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Write(Encoding.UTF8.GetBytes(text));
    }
}
