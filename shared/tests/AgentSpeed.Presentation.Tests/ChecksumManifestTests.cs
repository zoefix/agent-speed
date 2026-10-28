using System.Security.Cryptography;
using AgentSpeed.Presentation.Tests.TestSupport;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class ChecksumManifestTests
{
    private const string Name = "AgentSpeed-Windows-x64.zip";

    private static readonly byte[] Digest = SHA256.HashData("windows package"u8);
    private static readonly string Hex = Convert.ToHexStringLower(Digest);
    private static readonly string OtherHex = Convert.ToHexStringLower(SHA256.HashData("other"u8));

    private static string Manifest(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void FindsTheLineForTheAsset()
    {
        var text = Manifest(
            $"{OtherHex}  AgentSpeed-macOS.zip",
            $"{Hex}  {Name}",
            $"{OtherHex}  AgentSpeed-Windows-arm64.zip");

        Assert.Equal(Digest, ChecksumManifest.Find(text, Name));
        Assert.Equal(SHA256.HashData("other"u8), ChecksumManifest.Find(text, "AgentSpeed-macOS.zip"));
    }

    public static TheoryData<string> AcceptedLines =>
    [
        $"{Hex}  {Name}",
        $"{Hex} *{Name}",
        $"{Hex}  ./{Name}",
        $"{Hex} *./{Name}",
        $"{Hex}  {Name}\r",
        $"{Hex}  {Name}   ",
        $"{Hex.ToUpperInvariant()}  {Name}",
        $"{Hex} {Name}",
        $"{Hex}\t{Name}",
    ];

    [Theory]
    [MemberData(nameof(AcceptedLines))]
    public void AcceptsSha256SumFormats(string line)
    {
        Assert.Equal(Digest, ChecksumManifest.Find(Manifest(line), Name));
        Assert.Equal(Digest, ChecksumManifest.Find(line, Name));
    }

    [Fact]
    public void AcceptsCrlfManifest()
    {
        var text = $"{OtherHex}  AgentSpeed-macOS.zip\r\n{Hex}  {Name}\r\n";
        Assert.Equal(Digest, ChecksumManifest.Find(text, Name));
    }

    [Fact]
    public void IgnoresBlankAndGarbageLines()
    {
        var text = Manifest(
            string.Empty,
            "   ",
            "# SHA-256 checksums",
            "garbage",
            new string('z', 64) + "  " + Name,
            Hex[..63] + "  " + Name,
            Hex + "a  " + Name,
            "  " + Hex + "  " + Name,
            Hex + "  ",
            Hex + "  *",
            Hex + "  ./",
            $"{Hex}  {Name}",
            "\u0000\u0001");

        Assert.Equal(Digest, ChecksumManifest.Find(text, Name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("not a checksum file")]
    public void MissingNameYieldsNull(string text)
    {
        Assert.Null(ChecksumManifest.Find(text, Name));
        Assert.Null(ChecksumManifest.Find(Manifest($"{Hex}  AgentSpeed-macOS.zip"), Name));
    }

    [Theory]
    [InlineData("agentspeed-windows-x64.zip")]
    [InlineData("AgentSpeed-Windows-x64.ZIP")]
    [InlineData("AgentSpeed-Windows-x64")]
    [InlineData("Windows-x64.zip")]
    [InlineData("AgentSpeed-Windows-x64.zip.sig")]
    [InlineData(" AgentSpeed-Windows-x64.zip")]
    [InlineData("*AgentSpeed-Windows-x64.zip")]
    public void NameMustMatchExactly(string requested)
    {
        Assert.Null(ChecksumManifest.Find(Manifest($"{Hex}  {Name}"), requested));
    }

    [Theory]
    [InlineData("dist/AgentSpeed-Windows-x64.zip")]
    [InlineData("AgentSpeed-Windows-x64.zip.sig")]
    [InlineData("old-AgentSpeed-Windows-x64.zip")]
    [InlineData("agentspeed-windows-x64.zip")]
    public void ListedNameMustMatchExactly(string listed)
    {
        Assert.Null(ChecksumManifest.Find(Manifest($"{Hex}  {listed}"), Name));
    }

    [Fact]
    public void IdenticalDuplicatesAreAccepted()
    {
        var text = Manifest($"{Hex}  {Name}", $"{Hex.ToUpperInvariant()} *{Name}", $"{Hex}  ./{Name}");
        Assert.Equal(Digest, ChecksumManifest.Find(text, Name));
    }

    [Fact]
    public void ConflictingDuplicatesYieldNull()
    {
        Assert.Null(ChecksumManifest.Find(Manifest($"{Hex}  {Name}", $"{OtherHex}  {Name}"), Name));
        Assert.Null(ChecksumManifest.Find(Manifest($"{OtherHex}  {Name}", $"{Hex}  {Name}", $"{Hex}  {Name}"), Name));
        Assert.Null(ChecksumManifest.Find(Manifest($"{Hex}  {Name}", $"{OtherHex}  ./{Name}"), Name));
    }

    [Fact]
    public void OversizeManifestYieldsNull()
    {
        var padding = new string('#', ChecksumManifest.MaxBytes);
        Assert.Null(ChecksumManifest.Find(Manifest($"{Hex}  {Name}", padding), Name));
    }

    [Fact]
    public void FindValidatesArguments()
    {
        Assert.Throws<ArgumentNullException>(() => ChecksumManifest.Find(null!, Name));
        Assert.Throws<ArgumentNullException>(() => ChecksumManifest.Find(string.Empty, null!));
        Assert.Throws<ArgumentException>(() => ChecksumManifest.Find(string.Empty, string.Empty));
    }

    [Fact]
    public void TryParseLineSplitsHashAndName()
    {
        Assert.True(ChecksumManifest.TryParseLine($"{Hex} *./{Name}\r", out var hash, out var name));
        Assert.Equal(Digest, hash);
        Assert.Equal(Name, name);

        Assert.True(ChecksumManifest.TryParseLine($"{Hex}  file with spaces.txt", out _, out var spaced));
        Assert.Equal("file with spaces.txt", spaced);

        Assert.False(ChecksumManifest.TryParseLine(Hex, out hash, out name));
        Assert.Empty(hash);
        Assert.Equal(string.Empty, name);
    }

    [Fact]
    public void MatchesComparesFullDigests()
    {
        var copy = (byte[])Digest.Clone();
        Assert.True(ChecksumManifest.Matches(Digest, copy));

        copy[^1] ^= 1;
        Assert.False(ChecksumManifest.Matches(Digest, copy));

        copy = (byte[])Digest.Clone();
        copy[0] ^= 0x80;
        Assert.False(ChecksumManifest.Matches(Digest, copy));
    }

    [Fact]
    public void MatchesRejectsWrongLengths()
    {
        Assert.False(ChecksumManifest.Matches(Digest.AsSpan(0, 31), Digest.AsSpan(0, 31)));
        Assert.False(ChecksumManifest.Matches(Digest, Digest.AsSpan(0, 31)));
        Assert.False(ChecksumManifest.Matches(Digest.AsSpan(0, 31), Digest));
        Assert.False(ChecksumManifest.Matches([.. Digest, 0], [.. Digest, 0]));
        Assert.False(ChecksumManifest.Matches([], []));
        Assert.False(ChecksumManifest.Matches(SHA512.HashData("x"u8), SHA512.HashData("x"u8)));
    }

    [Fact]
    public async Task ComputeAsyncHashesTheFile()
    {
        using var temp = new TempDirectory();
        var content = RandomNumberGenerator.GetBytes(300_000);
        var path = temp.Combine("AgentSpeed-Windows-x64.zip");
        await File.WriteAllBytesAsync(path, content);

        var actual = await ChecksumManifest.ComputeAsync(path);

        Assert.Equal(SHA256.HashData(content), actual);
        var manifest = Manifest($"{Convert.ToHexString(actual)}  {Name}");
        Assert.True(ChecksumManifest.Matches(ChecksumManifest.Find(manifest, Name), actual));
        Assert.False(ChecksumManifest.Matches(Digest, actual));
    }

    [Fact]
    public async Task ComputeAsyncDetectsTampering()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("package.zip");
        await File.WriteAllBytesAsync(path, "original"u8.ToArray());
        var expected = await ChecksumManifest.ComputeAsync(path);

        await File.WriteAllBytesAsync(path, "originaL"u8.ToArray());
        var actual = await ChecksumManifest.ComputeAsync(path);

        Assert.False(ChecksumManifest.Matches(expected, actual));
    }

    [Fact]
    public async Task ComputeAsyncHashesEmptyFile()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("empty");
        await File.WriteAllBytesAsync(path, []);

        Assert.Equal(SHA256.HashData(ReadOnlySpan<byte>.Empty), await ChecksumManifest.ComputeAsync(path));
    }

    [Fact]
    public async Task ComputeAsyncFailsForMissingFileAndCancellation()
    {
        using var temp = new TempDirectory();
        await Assert.ThrowsAsync<FileNotFoundException>(() => ChecksumManifest.ComputeAsync(temp.Combine("missing.zip")));

        var path = temp.Combine("package.zip");
        await File.WriteAllBytesAsync(path, new byte[1024]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ChecksumManifest.ComputeAsync(path, cancelled.Token));
    }
}
