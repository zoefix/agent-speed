using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AgentSpeed.Presentation.Tests.TestSupport;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class UpdatePackageTests
{
    private const string X64 = "AgentSpeed-Windows-x64.zip";
    private const string Arm64 = "AgentSpeed-Windows-arm64.zip";
    private const string Sums = "SHA256SUMS.txt";

    private static void AssertInvalid(Action action)
    {
        var exception = Assert.Throws<UpdateException>(action);
        Assert.Equal(UpdateFailure.Invalid, exception.Failure);
    }

    private static ReleaseInfo WithAssets(params ReleaseAsset[] assets) => TestReleases.Release(assets: assets);

    private static PackageEntry Exe(long length = 1000) => new(UpdatePackage.ExecutableName, length);

    private static string CreateZip(TempDirectory temp, params (string Name, byte[]? Content)[] entries)
    {
        var path = temp.Combine("package.zip");
        using (var stream = File.Create(path))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                if (content is not null)
                {
                    using var data = entry.Open();
                    data.Write(content);
                }
            }
        }

        return path;
    }

    private static string CreateOutput(TempDirectory temp) => Directory.CreateDirectory(temp.Combine("out")).FullName;

    private static string[] Names(string directory) =>
        Directory.GetFileSystemEntries(directory).Select(p => Path.GetFileName(p)).Order(StringComparer.Ordinal).ToArray();

    /// <summary>Rewrites the declared uncompressed size of an entry in both its local and central headers.</summary>
    private static void SetDeclaredLength(string zipPath, string entryName, uint length)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var name = Encoding.UTF8.GetBytes(entryName);
        var patched = 0;
        for (var i = 0; i + 46 <= bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            if (signature == 0x04034b50 && NameAt(bytes, i + 26, i + 30, name))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 22), length);
                patched++;
            }
            else if (signature == 0x02014b50 && NameAt(bytes, i + 28, i + 46, name))
            {
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i + 24), length);
                patched++;
            }
        }

        Assert.Equal(2, patched);
        File.WriteAllBytes(zipPath, bytes);

        static bool NameAt(byte[] bytes, int lengthOffset, int nameOffset, byte[] name) =>
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(lengthOffset)) == name.Length
            && nameOffset + name.Length <= bytes.Length
            && bytes.AsSpan(nameOffset, name.Length).SequenceEqual(name);
    }

    [Theory]
    [InlineData(Architecture.X64, X64)]
    [InlineData(Architecture.Arm64, Arm64)]
    [InlineData(Architecture.X86, null)]
    [InlineData(Architecture.Arm, null)]
    [InlineData(Architecture.Wasm, null)]
    [InlineData(Architecture.S390x, null)]
    public void AssetNameForArchitecture(Architecture architecture, string? expected)
    {
        Assert.Equal(expected, UpdatePackage.AssetNameFor(architecture));
    }

    [Theory]
    [InlineData(Architecture.X64, X64)]
    [InlineData(Architecture.Arm64, Arm64)]
    public void PlanPicksPackageAndChecksums(Architecture architecture, string packageName)
    {
        var release = TestReleases.Release();

        var plan = UpdatePackage.Plan(release, architecture, UpdateSource.GitHub);

        Assert.Same(release, plan.Release);
        Assert.Equal(packageName, plan.Package.Name);
        Assert.Same(release.FindAsset(packageName), plan.Package);
        Assert.Equal(Sums, plan.Checksums.Name);
        Assert.Same(release.FindAsset(Sums), plan.Checksums);
    }

    [Fact]
    public void PlanAcceptsSizesAtTheLimits()
    {
        var release = WithAssets(TestReleases.Asset(X64, UpdatePackage.MaxAssetBytes), TestReleases.Asset(Sums, UpdatePackage.MaxChecksumsBytes));
        var plan = UpdatePackage.Plan(release, Architecture.X64, UpdateSource.GitHub);
        Assert.Equal(UpdatePackage.MaxAssetBytes, plan.Package.Size);
        Assert.Equal(300L * 1024 * 1024, UpdatePackage.MaxAssetBytes);
    }

    public static TheoryData<ReleaseAsset[]> UnusableAssets =>
    [
        [TestReleases.Asset(Arm64), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64)],
        [TestReleases.Asset("agentspeed-windows-x64.zip"), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64), TestReleases.Asset("sha256sums.txt")],
        [TestReleases.Asset(X64, 0), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64, -5), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64, UpdatePackage.MaxAssetBytes + 1), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64), TestReleases.Asset(Sums, 0)],
        [TestReleases.Asset(X64), TestReleases.Asset(Sums, UpdatePackage.MaxChecksumsBytes + 1)],
        [TestReleases.Asset(X64, url: "https://evil.example/AgentSpeed-Windows-x64.zip"), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64, url: "http://github.com/AgentSpeed-Windows-x64.zip"), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64, url: "https://github.com.evil.example/AgentSpeed-Windows-x64.zip"), TestReleases.Asset(Sums)],
        [TestReleases.Asset(X64), TestReleases.Asset(Sums, url: "https://evil.example/SHA256SUMS.txt")],
    ];

    [Theory]
    [MemberData(nameof(UnusableAssets))]
    public void PlanRejectsUnusableAssets(ReleaseAsset[] assets)
    {
        AssertInvalid(() => UpdatePackage.Plan(WithAssets(assets), Architecture.X64, UpdateSource.GitHub));
    }

    [Fact]
    public void OverrideSourceAllowsItsOwnHosts()
    {
        var source = UpdateSource.FromOverride("http://localhost:8080/latest.json", out _);
        var release = WithAssets(
            TestReleases.Asset(X64, url: "http://localhost:8080/AgentSpeed-Windows-x64.zip"),
            TestReleases.Asset(Sums, url: "http://localhost:8080/SHA256SUMS.txt"));

        var plan = UpdatePackage.Plan(release, Architecture.X64, source);
        Assert.Equal(X64, plan.Package.Name);
        AssertInvalid(() => UpdatePackage.Plan(release, Architecture.X64, UpdateSource.GitHub));

        var file = WithAssets(TestReleases.Asset(X64, url: "file:///tmp/AgentSpeed-Windows-x64.zip"), TestReleases.Asset(Sums));
        AssertInvalid(() => UpdatePackage.Plan(file, Architecture.X64, source));
    }

    [Fact]
    public void PlanRejectsDraftsPreReleasesAndUnsupportedArchitectures()
    {
        AssertInvalid(() => UpdatePackage.Plan(TestReleases.Release(isDraft: true), Architecture.X64, UpdateSource.GitHub));
        AssertInvalid(() => UpdatePackage.Plan(TestReleases.Release(isPreRelease: true), Architecture.X64, UpdateSource.GitHub));
        AssertInvalid(() => UpdatePackage.Plan(TestReleases.Release(), Architecture.X86, UpdateSource.GitHub));
        AssertInvalid(() => UpdatePackage.Plan(TestReleases.Release(), Architecture.Arm, UpdateSource.GitHub));
        Assert.Throws<ArgumentNullException>(() => UpdatePackage.Plan(null!, Architecture.X64, UpdateSource.GitHub));
        Assert.Throws<ArgumentNullException>(() => UpdatePackage.Plan(TestReleases.Release(), Architecture.X64, null!));
    }

    [Theory]
    [InlineData("AgentSpeed.exe")]
    [InlineData("docs/readme.txt")]
    [InlineData("docs\\readme.txt")]
    [InlineData("dir/")]
    [InlineData("dir\\")]
    [InlineData("a/b/c/d.txt")]
    [InlineData(".hidden")]
    [InlineData("..a")]
    [InlineData("a..b")]
    [InlineData(" leading-space.txt")]
    [InlineData("na\u00efve \u65e5\u672c.txt")]
    public void SafeEntryNames(string name)
    {
        Assert.True(UpdatePackage.IsSafeEntryName(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("\\")]
    [InlineData("/abs")]
    [InlineData("/etc/passwd")]
    [InlineData("\\abs")]
    [InlineData("//server/share/x")]
    [InlineData("\\\\server\\share\\x")]
    [InlineData("\\\\?\\C:\\x")]
    [InlineData("C:\\x")]
    [InlineData("C:/x")]
    [InlineData("C:x")]
    [InlineData("C:")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("../x")]
    [InlineData("..\\x")]
    [InlineData("a/../b")]
    [InlineData("a\\..\\b")]
    [InlineData("a/..")]
    [InlineData("a/../../x")]
    [InlineData("./x")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("dir//")]
    [InlineData("x.")]
    [InlineData("...")]
    [InlineData("x ")]
    [InlineData("dir./x")]
    [InlineData("dir /x")]
    [InlineData("a:b")]
    [InlineData("AgentSpeed.exe:Zone.Identifier")]
    [InlineData("AgentSpeed.exe::$DATA")]
    [InlineData("a\0b")]
    [InlineData("a\nb")]
    [InlineData("a\tb")]
    [InlineData("a\u001fb")]
    [InlineData("a\u007fb")]
    [InlineData("a*b")]
    [InlineData("a?b")]
    [InlineData("a<b")]
    [InlineData("a>b")]
    [InlineData("a|b")]
    [InlineData("a\"b")]
    public void UnsafeEntryNames(string? name)
    {
        Assert.False(UpdatePackage.IsSafeEntryName(name));
    }

    [Fact]
    public void EntryNameLengthIsLimited()
    {
        Assert.True(UpdatePackage.IsSafeEntryName(new string('a', UpdatePackage.MaxEntryNameLength)));
        Assert.False(UpdatePackage.IsSafeEntryName(new string('a', UpdatePackage.MaxEntryNameLength + 1)));
        Assert.Equal(260, UpdatePackage.MaxEntryNameLength);
    }

    [Fact]
    public void ValidateEntriesReturnsTheRootExecutable()
    {
        var found = UpdatePackage.ValidateEntries([new("docs/", 0), new("docs/readme.txt", 10), Exe(5000), new("sub/AgentSpeed.exe", 7)]);
        Assert.Equal(Exe(5000), found);
    }

    [Theory]
    [InlineData("agentspeed.exe")]
    [InlineData("AGENTSPEED.EXE")]
    public void ExecutableNameIsCaseInsensitive(string name)
    {
        Assert.Equal(new PackageEntry(name, 42), UpdatePackage.ValidateEntries([new(name, 42)]));
    }

    public static TheoryData<PackageEntry[]> WithoutRootExecutable =>
    [
        [],
        [new("readme.txt", 10)],
        [new("sub/AgentSpeed.exe", 10)],
        [new("sub\\AgentSpeed.exe", 10)],
        [new("AgentSpeed.exe/", 0)],
        [new("AgentSpeed.exe.bak", 10)],
        [new("AgentSpeed.ex", 10)],
    ];

    [Theory]
    [MemberData(nameof(WithoutRootExecutable))]
    public void ValidateEntriesRequiresRootExecutable(PackageEntry[] entries)
    {
        AssertInvalid(() => UpdatePackage.ValidateEntries(entries));
    }

    [Fact]
    public void ValidateEntriesRejectsDuplicateExecutable()
    {
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(), Exe()]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(), new("agentspeed.EXE", 1000)]));
    }

    [Fact]
    public void ValidateEntriesLimitsEntryCount()
    {
        var atLimit = Enumerable.Range(0, UpdatePackage.MaxEntries - 1).Select(i => new PackageEntry($"f{i}.txt", 1)).Append(Exe()).ToArray();
        Assert.Equal(Exe(), UpdatePackage.ValidateEntries(atLimit));

        AssertInvalid(() => UpdatePackage.ValidateEntries([.. atLimit, new("one-more.txt", 1)]));
    }

    [Fact]
    public void ValidateEntriesLimitsSizes()
    {
        Assert.Equal(Exe(UpdatePackage.MaxExecutableBytes), UpdatePackage.ValidateEntries([Exe(UpdatePackage.MaxExecutableBytes)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(UpdatePackage.MaxExecutableBytes + 1)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(), new("big.bin", UpdatePackage.MaxExecutableBytes + 1)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(-1)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(), new("negative.bin", -1)]));

        var max = UpdatePackage.MaxExecutableBytes;
        var rest = UpdatePackage.MaxTotalUncompressedBytes - (2 * max);
        Assert.Equal(Exe(max), UpdatePackage.ValidateEntries([Exe(max), new("a.bin", max), new("b.bin", rest)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(max), new("a.bin", max), new("b.bin", rest + 1)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([new("a.bin", max), new("b.bin", max), new("c.bin", max), Exe()]));
    }

    [Fact]
    public void ValidateEntriesRejectsEmptyExecutable()
    {
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(0)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([new("readme.txt", 10), Exe(0)]));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("AgentSpeed.exe:evil")]
    public void ValidateEntriesRejectsAnyUnsafeEntry(string name)
    {
        AssertInvalid(() => UpdatePackage.ValidateEntries([Exe(), new(name, 1)]));
        AssertInvalid(() => UpdatePackage.ValidateEntries([new(name, 1), Exe()]));
        Assert.Throws<ArgumentNullException>(() => UpdatePackage.ValidateEntries(null!));
    }

    [Fact]
    public void ExtractsOnlyTheExecutable()
    {
        using var temp = new TempDirectory();
        var exe = RandomNumberGenerator.GetBytes(300_000);
        var zip = CreateZip(
            temp,
            ("docs/", null),
            ("docs/readme.txt", "read me"u8.ToArray()),
            ("AgentSpeed.exe", exe),
            ("LICENSE.txt", "license"u8.ToArray()),
            ("sub/AgentSpeed.exe", "not this one"u8.ToArray()));
        var output = CreateOutput(temp);

        var path = UpdatePackage.ExtractExecutable(zip, output);

        Assert.Equal(Path.Combine(output, UpdatePackage.ExecutableName), path);
        Assert.Equal(exe, File.ReadAllBytes(path));
        Assert.Equal([UpdatePackage.ExecutableName], Names(output));
        Assert.Equal(["out", "package.zip"], Names(temp.Path));
    }

    [Fact]
    public void ExtractsExecutableUnderTheFixedName()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, ("agentspeed.EXE", "MZ payload"u8.ToArray()));
        var output = CreateOutput(temp);

        var path = UpdatePackage.ExtractExecutable(zip, output);

        Assert.Equal(UpdatePackage.ExecutableName, Path.GetFileName(path));
        Assert.Equal("MZ payload"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Single(Directory.GetFileSystemEntries(output));
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("..\\evil.txt")]
    [InlineData("docs/../../evil.txt")]
    [InlineData("/evil.txt")]
    [InlineData("\\evil.txt")]
    [InlineData("C:\\evil.txt")]
    [InlineData("AgentSpeed.exe:evil")]
    [InlineData("docs/evil?.txt")]
    public void UnsafeEntryRejectsTheWholePackage(string unsafeName)
    {
        foreach (var unsafeFirst in new[] { true, false })
        {
            using var temp = new TempDirectory();
            (string, byte[]?) exe = ("AgentSpeed.exe", "MZ payload"u8.ToArray());
            (string, byte[]?) evil = (unsafeName, "evil"u8.ToArray());
            var zip = unsafeFirst ? CreateZip(temp, evil, exe) : CreateZip(temp, exe, evil);
            var output = CreateOutput(temp);

            AssertInvalid(() => UpdatePackage.ExtractExecutable(zip, output));

            Assert.Empty(Directory.GetFileSystemEntries(output));
            Assert.Equal(["out", "package.zip"], Names(temp.Path));
        }
    }

    [Fact]
    public void PackageWithoutValidExecutableWritesNothing()
    {
        using var temp = new TempDirectory();
        var output = CreateOutput(temp);

        var missing = CreateZip(temp, ("readme.txt", "x"u8.ToArray()), ("sub/AgentSpeed.exe", "MZ"u8.ToArray()));
        AssertInvalid(() => UpdatePackage.ExtractExecutable(missing, output));

        File.Delete(missing);
        var empty = CreateZip(temp, ("AgentSpeed.exe", Array.Empty<byte>()));
        AssertInvalid(() => UpdatePackage.ExtractExecutable(empty, output));

        File.Delete(empty);
        var duplicate = CreateZip(temp, ("AgentSpeed.exe", "MZ one"u8.ToArray()), ("agentspeed.exe", "MZ two"u8.ToArray()));
        AssertInvalid(() => UpdatePackage.ExtractExecutable(duplicate, output));

        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public void UnreadableArchiveIsInvalid()
    {
        using var temp = new TempDirectory();
        var output = CreateOutput(temp);
        var valid = File.ReadAllBytes(CreateZip(temp, ("AgentSpeed.exe", RandomNumberGenerator.GetBytes(10_000))));
        byte[][] corrupt =
        [
            [],
            "this is not a zip archive"u8.ToArray(),
            valid[..(valid.Length / 2)],
            [.. valid[..^22], .. new byte[22]],
        ];

        foreach (var bytes in corrupt)
        {
            var path = temp.Combine("corrupt.zip");
            File.WriteAllBytes(path, bytes);
            AssertInvalid(() => UpdatePackage.ExtractExecutable(path, output));
            Assert.Empty(Directory.GetFileSystemEntries(output));
        }

        AssertInvalid(() => UpdatePackage.ExtractExecutable(temp.Combine("missing.zip"), output));
    }

    [Fact]
    public void ExtractionNeverWritesMoreThanDeclared()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, ("AgentSpeed.exe", new byte[200_000]));
        SetDeclaredLength(zip, "AgentSpeed.exe", 1000);
        var output = CreateOutput(temp);

        string? path = null;
        var exception = Record.Exception(() => path = UpdatePackage.ExtractExecutable(zip, output));

        if (exception is null)
        {
            Assert.Equal(1000, new FileInfo(path!).Length);
        }
        else
        {
            Assert.Equal(UpdateFailure.Invalid, Assert.IsType<UpdateException>(exception).Failure);
            Assert.Empty(Directory.GetFileSystemEntries(output));
        }
    }

    [Fact]
    public void ExecutableShorterThanDeclaredIsRejected()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, ("AgentSpeed.exe", new byte[1000]));
        SetDeclaredLength(zip, "AgentSpeed.exe", 200_000);
        var output = CreateOutput(temp);

        AssertInvalid(() => UpdatePackage.ExtractExecutable(zip, output));

        Assert.Empty(Directory.GetFileSystemEntries(output));
    }

    [Fact]
    public void ExistingExecutableIsNeitherDeletedNorOverwritten()
    {
        using var temp = new TempDirectory();
        var output = CreateOutput(temp);
        var existing = Path.Combine(output, UpdatePackage.ExecutableName);
        File.WriteAllBytes(existing, "old build"u8.ToArray());

        var valid = CreateZip(temp, ("AgentSpeed.exe", "new build"u8.ToArray()));
        AssertInvalid(() => UpdatePackage.ExtractExecutable(valid, output));
        Assert.Equal("old build"u8.ToArray(), File.ReadAllBytes(existing));

        File.Delete(valid);
        var traversal = CreateZip(temp, ("AgentSpeed.exe", "new build"u8.ToArray()), ("../evil.txt", "evil"u8.ToArray()));
        AssertInvalid(() => UpdatePackage.ExtractExecutable(traversal, output));
        Assert.Equal("old build"u8.ToArray(), File.ReadAllBytes(existing));

        var corrupt = temp.Combine("corrupt.zip");
        File.WriteAllBytes(corrupt, "garbage"u8.ToArray());
        AssertInvalid(() => UpdatePackage.ExtractExecutable(corrupt, output));
        Assert.Equal("old build"u8.ToArray(), File.ReadAllBytes(existing));
        Assert.Equal([UpdatePackage.ExecutableName], Names(output));
    }

    [Fact]
    public void MissingDestinationIsInvalid()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, ("AgentSpeed.exe", "MZ"u8.ToArray()));
        var output = temp.Combine("missing");

        AssertInvalid(() => UpdatePackage.ExtractExecutable(zip, output));

        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public void ExtractValidatesArguments()
    {
        Assert.Throws<ArgumentException>(() => UpdatePackage.ExtractExecutable(string.Empty, Path.GetTempPath()));
        Assert.Throws<ArgumentException>(() => UpdatePackage.ExtractExecutable("package.zip", string.Empty));
        Assert.Throws<ArgumentNullException>(() => UpdatePackage.ExtractExecutable(null!, Path.GetTempPath()));
        Assert.Throws<ArgumentNullException>(() => UpdatePackage.ExtractExecutable("package.zip", null!));
    }

    /// <summary>Clears the "language encoding" (UTF-8) flag, bit 11, in every local and central header.</summary>
    private static void ClearUtf8Flags(string zipPath)
    {
        var bytes = File.ReadAllBytes(zipPath);
        var cleared = 0;
        for (var i = 0; i + 10 <= bytes.Length; i++)
        {
            var signature = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i));
            var flagsOffset = signature switch { 0x04034b50 => i + 6, 0x02014b50 => i + 8, _ => -1 };
            if (flagsOffset < 0)
            {
                continue;
            }

            var flags = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(flagsOffset));
            if ((flags & 0x0800) != 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(flagsOffset), (ushort)(flags & ~0x0800));
                cleared++;
            }
        }

        Assert.True(cleared >= 2, "the archive should have had UTF-8 flagged names");
        File.WriteAllBytes(zipPath, bytes);
    }

    [Fact]
    public void EntryNamesWithoutTheUtf8FlagAreStillDecodedAsUtf8()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, ("AgentSpeed.exe", "MZ new"u8.ToArray()), ("文件/說明 ü.txt", "notes"u8.ToArray()));
        ClearUtf8Flags(zip);

        Assert.Contains(UpdatePackage.ReadEntries(zip), e => e.FullName == "文件/說明 ü.txt");
        var output = CreateOutput(temp);
        var executable = UpdatePackage.ExtractExecutable(zip, output);
        Assert.Equal("MZ new"u8.ToArray(), File.ReadAllBytes(executable));
        Assert.Equal([UpdatePackage.ExecutableName], Names(output));
    }

    [Fact]
    public void UnflaggedNonAsciiTraversalIsStillRejected()
    {
        using var temp = new TempDirectory();
        var zip = CreateZip(temp, ("AgentSpeed.exe", "MZ"u8.ToArray()), ("ü/../../évil.txt", "x"u8.ToArray()));
        ClearUtf8Flags(zip);
        var output = CreateOutput(temp);

        AssertInvalid(() => UpdatePackage.ExtractExecutable(zip, output));
        Assert.Empty(Names(output));
    }
}
