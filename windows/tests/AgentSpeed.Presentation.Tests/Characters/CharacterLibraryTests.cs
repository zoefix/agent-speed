using System.Text;
using AgentSpeed.Core;
using AgentSpeed.Core.Network;
using AgentSpeed.Core.Pricing;
using AgentSpeed.Presentation.Character;
using AgentSpeed.Presentation.Characters;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Tests.Characters;

public class CharacterManifestTests
{
    [Fact]
    public void IdsAreLowercaseLettersDigitsAndDashes()
    {
        Assert.True(CharacterId.IsValid("claude"));
        Assert.True(CharacterId.IsValid("gpt-5"));
        Assert.False(CharacterId.IsValid("Claude"));
        Assert.False(CharacterId.IsValid("-x"));
        Assert.False(CharacterId.IsValid("../x"));
        Assert.False(CharacterId.IsValid(string.Empty));
        Assert.False(CharacterId.IsValid(new string('a', 65)));
    }

    [Fact]
    public void ValidAndBadManifests()
    {
        CharacterPackFixture.Manifest().Validate("1.7.0");
        Assert.Equal(CharacterPackErrorKind.Format,
            Assert.Throws<CharacterPackException>(() => (CharacterPackFixture.Manifest() with { Format = "other" }).Validate(null)).Kind);
        Assert.Equal(CharacterPackErrorKind.MissingIdle, Assert.Throws<CharacterPackException>(() =>
            CharacterPackFixture.Manifest(poses: new Dictionary<string, string> { ["boost"] = "poses/boost" }).Validate(null)).Kind);
        var unsafePath = Assert.Throws<CharacterPackException>(() =>
            CharacterPackFixture.Manifest(poses: new Dictionary<string, string> { ["idle"] = "../poses" }).Validate(null));
        Assert.Equal((CharacterPackErrorKind.UnsafePath, "../poses"), (unsafePath.Kind, unsafePath.Detail));
        Assert.Equal(CharacterPackErrorKind.NewerApp,
            Assert.Throws<CharacterPackException>(() => CharacterPackFixture.Manifest(minApp: "9.0.0").Validate("1.7.0")).Kind);
        CharacterPackFixture.Manifest(minApp: "9.0.0").Validate("0.0.0");
        Assert.Equal(CharacterPackErrorKind.InvalidId,
            Assert.Throws<CharacterPackException>(() => (CharacterPackFixture.Manifest() with { Accent = "red" }).Validate(null)).Kind);
    }

    [Fact]
    public void AMissingBandUsesTheNearestLowerOne()
    {
        var manifest = CharacterPackFixture.Manifest(poses: new Dictionary<string, string> { ["idle"] = "poses/idle", ["boost"] = "poses/boost" });
        Assert.Equal("poses/idle", manifest.PoseFor("idle"));
        Assert.Equal("poses/idle", manifest.PoseFor("cruise"));
        Assert.Equal("poses/boost", manifest.PoseFor("boost"));
        Assert.Equal("poses/boost", manifest.PoseFor("redline"));
    }

    [Fact]
    public void AccentsParseAndArtistLinksAreHttpsOnly()
    {
        var rgb = CharacterManifest.Rgb("#D97757");
        Assert.True(rgb is { } c && Math.Abs(c.R - 217.0 / 255) < 1e-9);
        Assert.Null(CharacterManifest.Rgb("D97757"));
        Assert.NotNull(new CharacterArtist("a", "https://example.com/x").Link);
        Assert.Null(new CharacterArtist("a", "http://example.com/x").Link);
        Assert.Null(new CharacterArtist("a", "javascript:alert(1)").Link);
    }
}

public class CharacterCatalogTests
{
    private static string Entry(string id = "claude", string url = "https://github.com/zoefix/agent-speed/releases/download/characters/claude-1.zip",
        string? sha = null, long size = 10) =>
        $"{{\"id\":\"{id}\",\"packVersion\":1,\"name\":{{\"zh-Hant\":\"a\",\"zh-Hans\":\"a\",\"en\":\"a\",\"ja\":\"a\"}},\"vendors\":[\"anthropic\"],"
        + $"\"accent\":\"#D97757\",\"size\":{size},\"sha256\":\"{sha ?? new string('a', 64)}\",\"url\":\"{url}\"}}";

    [Fact]
    public void InvalidAndDuplicateEntriesAreDropped()
    {
        var json = "{\"format\":\"agent-speed-character-catalog\",\"version\":1,\"characters\":["
            + string.Join(",", Entry(), Entry(), Entry("evil", "https://example.com/x.zip"), Entry("short", sha: "abc"), Entry("huge", size: 300L << 20),
                Entry("plain", "http://github.com/x.zip"), Entry("gpt"), "5", "null") + "]}";
        var catalog = CharacterCatalog.Load(Encoding.UTF8.GetBytes(json));
        Assert.Equal(["claude", "gpt"], catalog.Characters.Select(c => c.Id));
        Assert.Equal(catalog.Characters.Select(c => c.Id), CharacterCatalog.Load(catalog.ToJson()).Characters.Select(c => c.Id));
    }

    [Fact]
    public void OtherFormatsAndOversizedCatalogsAreRefused()
    {
        Assert.Throws<CharacterPackException>(() => CharacterCatalog.Load("{\"format\":\"x\",\"version\":1,\"characters\":[]}"u8));
        Assert.Throws<CharacterPackException>(() => CharacterCatalog.Load("not json"u8));
        Assert.Equal(CharacterPackErrorKind.TooLarge,
            Assert.Throws<CharacterPackException>(() => CharacterCatalog.Load(new byte[CharacterCatalog.MaxCatalogBytes + 1])).Kind);
    }

    [Fact]
    public void OnlyReleaseHostsOverHttps()
    {
        Assert.True(CharacterCatalog.Permits(new Uri("https://github.com/a")));
        Assert.True(CharacterCatalog.Permits(new Uri("https://objects.githubusercontent.com/a")));
        Assert.True(CharacterCatalog.Permits(new Uri("https://release-assets.githubusercontent.com/a")));
        Assert.False(CharacterCatalog.Permits(new Uri("http://github.com/a")));
        Assert.False(CharacterCatalog.Permits(new Uri("https://raw.githubusercontent.com/a")));
        Assert.False(CharacterCatalog.Permits(new Uri("https://user@github.com/a")));
        Assert.False(CharacterCatalog.Permits(new Uri("https://github.com:8443/a")));
        Assert.False(CharacterCatalog.Permits(new Uri("https://github.com.evil.example/a")));
        Assert.False(CharacterCatalog.Permits(null));
    }
}

/// <summary>The archive checks of CHARACTERS 1 and 2: nothing outside the pack, no links, bounded sizes.</summary>
public class CharacterArchiveTests
{
    [Theory]
    [InlineData("manifest.json", true)]
    [InlineData("poses/idle/rig.json", true)]
    [InlineData("poses/", true)]
    [InlineData("../x", false)]
    [InlineData("poses/../../x", false)]
    [InlineData("/etc/passwd", false)]
    [InlineData("C:/Windows/x", false)]
    [InlineData("poses\\idle\\rig.json", false)]
    [InlineData("a//b", false)]
    [InlineData("./a", false)]
    [InlineData("~/a", false)]
    [InlineData("poses/CON", false)]
    [InlineData("poses/nul.png", false)]
    [InlineData("poses/x.png:stream", false)]
    [InlineData("poses/trailing.", false)]
    [InlineData("poses/a\u0001b", false)]
    [InlineData("", false)]
    public void PathsMustStayInsideThePack(string path, bool safe) => Assert.Equal(safe, CharacterArchive.IsSafe(path));

    [Fact]
    public void EntriesMustBeSafeAndHoldAManifest()
    {
        CharacterArchive.Validate([new("manifest.json"), new("poses/idle/rig.json")]);
        Assert.Equal((CharacterPackErrorKind.UnsafePath, "../x"), Kind(() => CharacterArchive.Validate([new("manifest.json"), new("../x")])));
        Assert.Equal((CharacterPackErrorKind.SymbolicLink, "link"), Kind(() => CharacterArchive.Validate([new("manifest.json"), new("link", 0, true)])));
        Assert.Equal((CharacterPackErrorKind.MissingFile, "manifest.json"), Kind(() => CharacterArchive.Validate([new("poses/idle/rig.json")])));
        Assert.Equal(CharacterPackErrorKind.TooLarge, Kind(() => CharacterArchive.Validate([new("manifest.json"), new("big", CharacterArchive.MaxEntrySize + 1)])).Item1);
        var many = Enumerable.Range(0, 10).Select(i => new CharacterArchiveEntry($"f{i}", CharacterArchive.MaxEntrySize)).Append(new("manifest.json")).ToArray();
        Assert.Equal(CharacterPackErrorKind.TooLarge, Kind(() => CharacterArchive.Validate(many)).Item1);
        Assert.Equal(CharacterPackErrorKind.UnsafePath, Kind(() => CharacterArchive.Validate([new("manifest.json"), new("a.png"), new("A.PNG")])).Item1);
        Assert.Equal(CharacterPackErrorKind.TooLarge, Kind(() => CharacterArchive.Validate([])).Item1);
    }

    [Fact]
    public void ZipSlipIsRefusedBeforeAnythingIsWritten()
    {
        using var fixture = new CharacterPackFixture();
        var zip = fixture.RawZip("slip.zip", ("manifest.json", "{}"), ("../escaped.txt", "x"));
        var target = Path.Combine(fixture.Root, "out");
        Assert.Throws<CharacterPackException>(() => CharacterArchive.Extract(zip, target));
        Assert.False(File.Exists(Path.Combine(fixture.Root, "escaped.txt")));
        Assert.False(Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any());
    }

    [Fact]
    public void AnArchiveWithoutAManifestIsRefused()
    {
        using var fixture = new CharacterPackFixture();
        var zip = fixture.RawZip("none.zip", ("poses/idle/rig.json", "{}"));
        Assert.Throws<CharacterPackException>(() => CharacterArchive.Extract(zip, Path.Combine(fixture.Root, "out")));
    }

    private static (CharacterPackErrorKind, string) Kind(Action action)
    {
        var error = Assert.Throws<CharacterPackException>(action);
        return (error.Kind, error.Detail);
    }
}

public class CharacterStoreTests
{
    [Fact]
    public void InstallListsReplacesAndRemoves()
    {
        using var fixture = new CharacterPackFixture();
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"), appVersion: "1.7.0");
        Assert.Empty(store.Installed());
        var unpacked = fixture.Pack(CharacterPackFixture.Manifest(), "incoming");
        var installed = store.Install(unpacked, "claude");
        Assert.Equal("claude", Path.GetFileName(installed.Folder));
        Assert.Equal(["claude"], store.Installed().Select(p => p.Id));
        Assert.False(Directory.Exists(unpacked));

        store.Install(fixture.Pack(CharacterPackFixture.Manifest(packVersion: 2), "incoming2"), "claude");
        Assert.Equal(2, store.Installed("claude")?.Manifest.PackVersion);

        store.Remove("claude");
        Assert.Empty(store.Installed());
        Assert.Empty(Directory.GetDirectories(store.Root));
        Assert.Throws<CharacterPackException>(() => store.Remove("../x"));
    }

    [Fact]
    public void APackWithAnotherIdIsRefused()
    {
        using var fixture = new CharacterPackFixture();
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var unpacked = fixture.Pack(CharacterPackFixture.Manifest(id: "gpt"), "incoming");
        Assert.Equal(CharacterPackErrorKind.Mismatch, Assert.Throws<CharacterPackException>(() => store.Install(unpacked, "claude")).Kind);
    }

    [Fact]
    public void LinksInsideAPackAreRefused()
    {
        using var fixture = new CharacterPackFixture();
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var unpacked = fixture.Pack(CharacterPackFixture.Manifest(), "incoming");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(unpacked, "escape"), Path.GetTempPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var error = Assert.Throws<CharacterPackException>(() => store.Install(unpacked, "claude"));
        Assert.Equal((CharacterPackErrorKind.SymbolicLink, "escape"), (error.Kind, error.Detail));
        Assert.Empty(store.Installed());
    }

    [Fact]
    public void DevelopmentPacksAreListedFirstAndHideInstalledOnes()
    {
        using var fixture = new CharacterPackFixture();
        var dev = new CharacterPackFixture(Path.Combine(fixture.Root, "dev"));
        var installedRoot = new CharacterPackFixture(Path.Combine(fixture.Root, "Characters"));
        dev.Pack(CharacterPackFixture.Manifest(id: "claude", packVersion: 9));
        installedRoot.Pack(CharacterPackFixture.Manifest(id: "claude"));
        installedRoot.Pack(CharacterPackFixture.Manifest(id: "gpt"));
        var broken = installedRoot.Pack(CharacterPackFixture.Manifest(id: "broken", poses: new Dictionary<string, string> { ["idle"] = "missing" }), "broken");
        Directory.Delete(Path.Combine(broken, "missing"), recursive: true);
        var store = CharacterStore.Standard(fixture.Root, null, name => name == CharacterStore.DevelopmentVariable ? dev.Root : null);
        var packs = store.Installed();
        Assert.Equal(["claude", "gpt"], packs.Select(p => p.Id));
        Assert.True(packs[0].IsDevelopment);
        Assert.Equal(9, packs[0].Manifest.PackVersion);
    }

    [Fact]
    public void AFolderNamedDifferentlyFromItsIdIsIgnored()
    {
        using var fixture = new CharacterPackFixture();
        fixture.Pack(CharacterPackFixture.Manifest(id: "gpt"), "claude");
        Assert.Empty(new CharacterStore(fixture.Root).Installed());
    }
}

public class CharacterInstallerTests
{
    [Fact]
    public async Task AVerifiedPackIsInstalled()
    {
        using var fixture = new CharacterPackFixture();
        var (archive, size, sha) = fixture.Zip();
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var installer = new CharacterInstaller(store, new MapFetcher(new Dictionary<string, string> { ["claude"] = archive }, 0));
        var pack = await installer.InstallAsync(CharacterPackFixture.Entry("claude", size, sha), null, CancellationToken.None);
        Assert.Equal("claude", pack.Id);
        Assert.Equal(["claude"], store.Installed().Select(p => p.Id));
        Assert.Equal(["claude"], Directory.GetDirectories(store.Root).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AChecksumMismatchInstallsNothing()
    {
        using var fixture = new CharacterPackFixture();
        var (archive, size, _) = fixture.Zip();
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var installer = new CharacterInstaller(store, new MapFetcher(new Dictionary<string, string> { ["claude"] = archive }, 0));
        var error = await Assert.ThrowsAsync<CharacterInstallException>(() =>
            installer.InstallAsync(CharacterPackFixture.Entry("claude", size, new string('0', 64)), null, CancellationToken.None));
        Assert.Equal(CharacterInstallFailure.Checksum, error.Failure);
        Assert.Empty(store.Installed());
        Assert.Empty(Directory.GetDirectories(store.Root));
    }

    [Fact]
    public async Task AWrongSizeIsRefusedBeforeUnpacking()
    {
        using var fixture = new CharacterPackFixture();
        var (archive, size, sha) = fixture.Zip();
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var installer = new CharacterInstaller(store, new MapFetcher(new Dictionary<string, string> { ["claude"] = archive }, 0));
        await Assert.ThrowsAsync<CharacterInstallException>(() =>
            installer.InstallAsync(CharacterPackFixture.Entry("claude", size + 1, sha), null, CancellationToken.None));
        Assert.Empty(store.Installed());
    }

    [Fact]
    public async Task APackUnderAnotherIdIsRefused()
    {
        using var fixture = new CharacterPackFixture();
        var (archive, size, sha) = fixture.Zip("gpt");
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var installer = new CharacterInstaller(store, new MapFetcher(new Dictionary<string, string> { ["claude"] = archive }, 0));
        var error = await Assert.ThrowsAsync<CharacterPackException>(() =>
            installer.InstallAsync(CharacterPackFixture.Entry("claude", size, sha), null, CancellationToken.None));
        Assert.Equal(CharacterPackErrorKind.Mismatch, error.Kind);
        Assert.Empty(store.Installed());
    }

    [Fact]
    public async Task AVerifiedArchiveWithAnEscapingEntryInstallsNothing()
    {
        using var fixture = new CharacterPackFixture();
        var archive = fixture.RawZip("claude-1.zip", ("manifest.json", Encoding.UTF8.GetString(CharacterPackFixture.Manifest().ToJson())),
            ("../../outside.txt", "x"));
        var store = new CharacterStore(Path.Combine(fixture.Root, "Characters"));
        var installer = new CharacterInstaller(store, new MapFetcher(new Dictionary<string, string> { ["claude"] = archive }, 0));
        await Assert.ThrowsAsync<CharacterPackException>(() => installer.InstallAsync(
            CharacterPackFixture.Entry("claude", new FileInfo(archive).Length, CharacterInstaller.Sha256Hex(archive)), null, CancellationToken.None));
        Assert.Empty(store.Installed());
        Assert.False(File.Exists(Path.Combine(fixture.Root, "outside.txt")));
    }

    [Fact]
    public async Task TheHttpFetcherRefusesHostsOutsideTheRelease()
    {
        using var fetcher = new HttpCharacterPackFetcher("1.0.0");
        var error = await Assert.ThrowsAsync<CharacterInstallException>(() =>
            fetcher.FetchAsync(new Uri("https://example.com/claude-1.zip"), Path.GetTempFileName(), 10, null, CancellationToken.None));
        Assert.Equal(CharacterInstallFailure.DisallowedUrl, error.Failure);
    }
}

public class CharacterLibraryTests
{
    private static (CharacterLibrary Library, MapFetcher Fetcher, CharacterPackFixture Fixture, List<IReadOnlyList<string>> Saved) Run(
        Func<bool>? network = null, int fetchDelayMs = 20)
    {
        var fixture = new CharacterPackFixture();
        var vendors = new Dictionary<string, string[]> { ["claude"] = ["anthropic"], ["gpt"] = ["openai"], ["kimi"] = ["moonshot"] };
        var entries = new List<CharacterCatalog.Entry>();
        var archives = new Dictionary<string, string>();
        foreach (var (id, v) in vendors)
        {
            var (archive, size, sha) = fixture.Zip(id, v);
            entries.Add(CharacterPackFixture.Entry(id, size, sha, v));
            archives[id] = archive;
        }

        var catalog = new CharacterCatalog(entries);
        var fetcher = new MapFetcher(archives, fetchDelayMs);
        var saved = new List<IReadOnlyList<string>>();
        var library = new CharacterLibrary(new CharacterStore(Path.Combine(fixture.Root, "Characters")),
            new FixedTransport(new HttpFetchResponse(200, catalog.ToJson())), fetcher, "1.7.0", saveSetAside: s => saved.Add(s));
        if (network is not null)
        {
            library.NetworkEnabled = network;
        }

        return (library, fetcher, fixture, saved);
    }

    [Fact]
    public async Task TheCatalogLoadsAndDownloadsInstall()
    {
        var (library, _, fixture, _) = Run();
        using (fixture)
        {
            library.RefreshForList();
            await LibraryWait.Settle(library);
            Assert.Equal(["claude", "gpt", "kimi"], library.Catalog?.Characters.Select(c => c.Id));
            library.DownloadPack(library.Catalog!.Find("gpt")!);
            await LibraryWait.Settle(library);
            Assert.Equal(["gpt"], library.Installed.Select(p => p.Id));
            Assert.Equal(["gpt"], library.Packs.Select(p => p.Id));
            Assert.Empty(library.Errors);
            Assert.False(library.HasUpdate("gpt"));
            library.Remove("gpt");
            Assert.Empty(library.Installed);
        }
    }

    [Fact]
    public async Task SwitchingDownloadsEveryPackOneAtATimeLiveVendorFirst()
    {
        var (library, fetcher, fixture, _) = Run();
        using (fixture)
        {
            library.DownloadAll(["moonshot"]);
            await LibraryWait.Settle(library);
            Assert.Equal(["kimi", "claude", "gpt"], fetcher.Order);
            Assert.Equal(1, fetcher.MostAtOnce);
            Assert.Equal(["claude", "gpt", "kimi"], library.Installed.Select(p => p.Id).Order());
            Assert.Equal((0, 0, (string?)null), (library.AutoTotal, library.AutoQueue.Count, library.AutoCurrent));
        }
    }

    [Fact]
    public async Task RemovedPacksStayAsideUntilDownloadedByHand()
    {
        var (library, fetcher, fixture, saved) = Run();
        using (fixture)
        {
            library.DownloadAll([]);
            await LibraryWait.Settle(library);
            library.Remove("gpt");
            Assert.Equal(["gpt"], saved[^1]);
            library.DownloadAll([]);
            await LibraryWait.Settle(library);
            Assert.Null(library.InstalledPack("gpt"));
            Assert.Equal(3, fetcher.Order.Count);
            var again = new CharacterLibrary(library.Store, new FixedTransport(new HttpFetchResponse(500)), fetcher, "1.7.0", saved[^1]);
            Assert.Equal(["gpt"], again.SetAside);
            library.DownloadPack(library.Catalog!.Find("gpt")!);
            await LibraryWait.Settle(library);
            Assert.NotNull(library.InstalledPack("gpt"));
            Assert.Empty(library.SetAside);
            Assert.Empty(saved[^1]);
        }
    }

    [Fact]
    public async Task StopSetsAsideWhatIsLeft()
    {
        // The first download stays in flight until Stop cancels it, so the run is always stopped mid-download.
        var (library, fetcher, fixture, _) = Run(fetchDelayMs: 30_000);
        using (fixture)
        {
            library.RefreshForList();
            await LibraryWait.Until(() => library.Catalog is not null);
            library.DownloadAll([]);
            await LibraryWait.Until(() => library.AutoCurrent is not null && fetcher.Order.Count == 1);
            library.StopAutomatic();
            await LibraryWait.Settle(library);
            Assert.Equal(new[] { "claude", "gpt", "kimi" }.Where(id => library.InstalledPack(id) is null).ToHashSet(), library.SetAside);
            Assert.True(library.Installed.Count <= 1);
            Assert.Single(fetcher.Order);
        }
    }

    [Fact]
    public async Task NothingRunsWhileNetworkFeaturesAreOff()
    {
        var (library, fetcher, fixture, _) = Run(() => false);
        using (fixture)
        {
            library.RefreshForList();
            library.DownloadAll([]);
            await LibraryWait.Settle(library);
            Assert.Empty(fetcher.Order);
            Assert.Equal(CharacterLibrary.CatalogState.Idle, library.CatalogStatus);
        }
    }

    [Fact]
    public async Task AFailedCheckSkipsThePackForTheRest()
    {
        var fixture = new CharacterPackFixture();
        using (fixture)
        {
            var good = fixture.Zip("claude");
            var bad = fixture.Zip("gpt");
            var catalog = new CharacterCatalog([
                CharacterPackFixture.Entry("gpt", bad.Size, new string('0', 64), ["openai"]),
                CharacterPackFixture.Entry("claude", good.Size, good.Sha256),
            ]);
            var fetcher = new MapFetcher(new Dictionary<string, string> { ["claude"] = good.Archive, ["gpt"] = bad.Archive });
            var library = new CharacterLibrary(new CharacterStore(Path.Combine(fixture.Root, "Characters")),
                new FixedTransport(new HttpFetchResponse(200, catalog.ToJson())), fetcher, "1.7.0");
            library.DownloadAll([]);
            await LibraryWait.Settle(library);
            Assert.Equal(["gpt", "claude"], fetcher.Order);
            Assert.Equal("characters.error.checksum", library.Errors["gpt"]);
            Assert.Equal(["claude"], library.Installed.Select(p => p.Id));
        }
    }

    [Fact]
    public void DownloadErrorsMapToMessages()
    {
        Assert.Equal("characters.error.checksum", CharacterLibrary.ErrorKey(new CharacterInstallException(CharacterInstallFailure.Checksum, "x")));
        Assert.Equal("characters.error.newerApp", CharacterLibrary.ErrorKey(new CharacterPackException(CharacterPackErrorKind.NewerApp, "9.0.0")));
        Assert.Equal("characters.error.invalid", CharacterLibrary.ErrorKey(new CharacterPackException(CharacterPackErrorKind.Mismatch)));
        Assert.Null(CharacterLibrary.ErrorKey(new OperationCanceledException()));
        Assert.Equal("characters.error.network", CharacterLibrary.ErrorKey(new CharacterInstallException(CharacterInstallFailure.Network, "x")));
    }

    /// <summary>
    /// <c>AGENT_SPEED_LIVE_CHARACTERS=1</c>: the app's own catalog fetch and download path against the published
    /// <c>characters</c> release, into a temporary folder (<c>AGENT_SPEED_LIVE_IDS</c> picks the packs, default kimi).
    /// </summary>
    [Fact]
    public async Task DownloadsAndInstallsFromTheRelease()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_LIVE_CHARACTERS") != "1")
        {
            return;
        }

        var root = Path.Combine(Environment.GetEnvironmentVariable("AGENT_SPEED_LIVE_DIR") ?? Path.GetTempPath(), "character-live-" + Guid.NewGuid().ToString("N"));
        using var transport = new HttpClientTransport();
        using var fetcher = new HttpCharacterPackFetcher("1.6.3");
        var library = new CharacterLibrary(new CharacterStore(root), transport, fetcher, "1.6.3");
        library.RefreshCatalog(TimeSpan.Zero);
        await LibraryWait.Until(() => library.CatalogStatus != CharacterLibrary.CatalogState.Loading, 30_000);
        var catalog = Assert.IsType<CharacterCatalog>(library.Catalog);
        Assert.Equal(17, catalog.Characters.Count);
        foreach (var id in (Environment.GetEnvironmentVariable("AGENT_SPEED_LIVE_IDS") ?? "kimi").Split(','))
        {
            library.DownloadPack(catalog.Find(id)!);
            await LibraryWait.Until(() => !library.Downloads.ContainsKey(id), 120_000);
            Assert.False(library.Errors.ContainsKey(id), id + ": " + library.Errors.GetValueOrDefault(id));
            var installed = Assert.IsType<InstalledCharacter>(library.InstalledPack(id));
            var pack = CharacterPack.Load(installed.Folder);
            Assert.True(pack.Poses.Count >= 3, id + " poses");
        }
    }
}

public class CharacterAutoDownloadTests
{
    private static readonly CharacterCatalog Catalog = new(
        new[] { ("claude", "anthropic"), ("deepseek", "deepseek"), ("gemini", "google"), ("gpt", "openai"), ("kimi", "moonshot") }
            .Select(p => CharacterPackFixture.Entry(p.Item1, 10, new string('0', 64), [p.Item2])).ToArray());

    [Fact]
    public void LiveVendorsComeFirstThenCatalogOrder() =>
        Assert.Equal(["gpt", "claude", "deepseek", "gemini", "kimi"],
            CharacterAutoDownload.Queue(Catalog, new HashSet<string>(), new HashSet<string>(), null, ["openai", "anthropic"]));

    [Fact]
    public void InstalledSetAsideAndSkippedPacksStayOut() =>
        Assert.Equal(["deepseek", "gpt"], CharacterAutoDownload.Queue(Catalog, new HashSet<string> { "claude" }, new HashSet<string> { "gemini" },
            new HashSet<string> { "kimi" }, ["anthropic"]));

    [Fact]
    public void VendorsAreOrderedByLiveRate()
    {
        var reading = new VendorReading(new Dictionary<string, double> { ["openai"] = 40, ["anthropic"] = 120, ["google"] = 0, ["moonshot"] = 40 },
            null, "moonshot");
        Assert.Equal(["anthropic", "moonshot", "openai"], CharacterAutoDownload.Vendors(reading));
    }
}

public class VendorReadingTests
{
    private static AgentStatus Agent(SourceKind source, string? model, double rate) =>
        new(Guid.NewGuid().ToString(), source, "p", model, AgentPhase.Generating, false, new Rate(rate, rate), null, DateTimeOffset.UnixEpoch);

    [Fact]
    public void RatesSumPerVendorAndTheFastestModelIsNamed()
    {
        var reading = VendorReading.Of([
            Agent(SourceKind.ClaudeCode, "claude-opus-4-5", 40), Agent(SourceKind.ClaudeCode, "claude-sonnet-4-5", 30),
            Agent(SourceKind.Codex, "gpt-5.1-codex", 55), Agent(SourceKind.Omp, null, 9),
        ], PriceBook.Bundled);
        Assert.Equal(70, reading.Rates["anthropic"]);
        Assert.Equal(55, reading.Rates["openai"]);
        Assert.Equal(2, reading.Rates.Count);
        Assert.Equal("openai", reading.FastestVendor);
        Assert.NotNull(reading.FastestModel);
        Assert.NotEqual("gpt-5.1-codex", reading.FastestModel);
    }

    [Theory]
    [InlineData("doubao-seed-1.6", "bytedance")]
    [InlineData("seed-oss-36b", "bytedance")]
    [InlineData("bytedance/UI-TARS-1.5-7B", "bytedance")]
    [InlineData("meta-llama/Llama-3.3-70B-Instruct", "meta")]
    [InlineData("llama3.2:latest", "meta")]
    [InlineData("sonar-pro", "perplexity")]
    [InlineData("pplx-70b-online", "perplexity")]
    [InlineData("perplexity/r1-1776", "perplexity")]
    [InlineData("ERNIE-4.5-Turbo", "baidu")]
    [InlineData("hunyuan-t1-latest", "tencent")]
    [InlineData("rwkv-7-world", "rwkv")]
    [InlineData("codellama-13b", null)]
    [InlineData("seedream", null)]
    [InlineData("jev", null)]
    [InlineData(null, null)]
    [InlineData("", null)]
    public void ModelFamiliesNameTheirVendor(string? model, string? vendor) => Assert.Equal(vendor, CharacterVendors.Family(model));

    [Fact]
    public void FamiliesAndSourcesFillInWithoutPrices()
    {
        var reading = VendorReading.Of([Agent(SourceKind.Omp, "meta-llama/llama-4-maverick", 20), Agent(SourceKind.Gemini, "ernie-x1", 8)], null);
        Assert.Equal(new Dictionary<string, double> { ["meta"] = 20, ["baidu"] = 8 }, reading.Rates);
        Assert.Equal("meta", reading.FastestVendor);
        Assert.Equal("google", CharacterVendors.Resolve("some-new-model", SourceKind.Gemini, null).Vendor);
    }
}
