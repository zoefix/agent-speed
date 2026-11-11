using System.Security.Cryptography;
using AgentSpeed.Core.Quota.Cookies;
using static AgentSpeed.Core.Tests.Quota.Cookies.BrowserStores;

namespace AgentSpeed.Core.Tests.Quota.Cookies;

/// <summary>
/// The reader against browser trees built in this test's own temporary folder (QUOTAS 9.10). No real browser store,
/// <c>Local State</c> or profile of the user is ever read: every reader gets the temporary roots.
/// </summary>
public sealed class BrowserCookieReaderTests : IDisposable
{
    private static readonly string[] Qoder = ["qoder.com", "qoder.com.cn"];
    private static readonly string[] WorkBuddy = ["workbuddy.cn"];

    private readonly TempDirectory _temp = new();
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public BrowserCookieReaderTests() => Directory.CreateDirectory(Copies);

    private string Local => _temp.Combine("Local");

    private string FirefoxRoot => _temp.Combine("Roaming", "Mozilla", "Firefox");

    private string Copies => _temp.Combine("copies");

    private string ChromeData => Path.Combine(Local, "Google", "Chrome", "User Data");

    private string EdgeData => Path.Combine(Local, "Microsoft", "Edge", "User Data");

    private string BraveData => Path.Combine(Local, "BraveSoftware", "Brave-Browser", "User Data");

    public void Dispose() => _temp.Dispose();

    [WindowsFact]
    public void NetworkCookiesDecryptThroughDpapiAndGcm()
    {
        LocalState(ChromeData, TestDpapi.Protect(_key));
        File.WriteAllText(Path.Combine(ChromeData, "Last Version"), "141.0.7390.55\n");
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Sealed(".qoder.com", "sid", Gcm("s3cret", _key, ".qoder.com"), NowS - 10),
            ChromeRow.Plain("qoder.com", "plain", "p1", NowS - 20, expiresS: NowS + 600),
            ChromeRow.Plain("old.qoder.com", "gone", "x", NowS - 5, expiresS: NowS - 60),
            ChromeRow.Plain("evilqoder.com", "evil", "y", NowS));
        var unwrapper = new CountingUnwrapper(DpapiKeyUnwrapper.Instance);
        var reader = Reader(unwrapper);

        var session = reader.Session(Qoder, NowMs, out var miss);

        Assert.NotNull(session);
        Assert.Equal(BrowserSessionMiss.None, miss);
        Assert.Equal("sid=s3cret; plain=p1", session.Cookie);
        Assert.Equal("qoder.com", session.Site);
        Assert.Equal(BrowserKind.Chrome, session.Browser);
        Assert.Equal(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/141.0.0.0 Safari/537.36",
            session.UserAgent);
        Assert.Equal([BrowserKind.Chrome], unwrapper.Asked);
        AssertNoCopyLeft();
    }

    [Fact]
    public void TheOlderCookiesLayoutAndTheDefaultVersion()
    {
        LocalState(EdgeData, [1, 2, 3]);
        Chrome(Path.Combine(EdgeData, "Default", "Cookies"), 23,
            ChromeRow.Sealed("www.workbuddy.cn", "wb", Gcm("edge-session", _key), NowS - 30),
            ChromeRow.Plain("workbuddy.cn", "lang", "zh", NowS - 40));
        var unwrapper = new FixedUnwrapper(_key);

        var session = Reader(unwrapper).Session(WorkBuddy, NowMs, out var miss);

        Assert.Equal(BrowserSessionMiss.None, miss);
        Assert.Equal("wb=edge-session; lang=zh", session?.Cookie);
        Assert.Equal(BrowserKind.Edge, session?.Browser);
        Assert.Contains("Chrome/142.0.0.0", session?.UserAgent, StringComparison.Ordinal);
        Assert.Equal([BrowserKind.Edge], unwrapper.Asked);
        Assert.All(unwrapper.HandedOut, key => Assert.All(key, b => Assert.Equal(0, b)));
        AssertNoCopyLeft();
    }

    [Fact]
    public void TheMostRecentlyUsedProfileWinsBeforeAnythingIsDecrypted()
    {
        LocalState(ChromeData, [1]);
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Sealed("qoder.com", "old", Gcm("o", _key, "qoder.com"), NowS - 3600));
        Chrome(Path.Combine(ChromeData, "Profile 2", "Network", "Cookies"), 24,
            ChromeRow.Plain("qoder.com", "mid", "m", NowS - 1800));
        Chrome(Path.Combine(ChromeData, "Profile 10", "Network", "Cookies"), 24,
            ChromeRow.Plain("qoder.com", "older", "o", NowS - 7200));
        Directory.CreateDirectory(Path.Combine(ChromeData, "Guest Profile"));
        var unwrapper = new FixedUnwrapper(_key);

        var session = Reader(unwrapper).Session(Qoder, NowMs, out _);

        Assert.Equal("mid=m", session?.Cookie);
        Assert.Empty(unwrapper.Asked);
    }

    [Fact]
    public void EqualTimesKeepTheBrowserOrder()
    {
        Chrome(Path.Combine(EdgeData, "Default", "Network", "Cookies"), 24, ChromeRow.Plain("qoder.com", "e", "1", NowS - 60));
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24, ChromeRow.Plain("qoder.com", "c", "1", NowS - 60));
        WriteProfilesIni("[Profile0]\nPath=Profiles/a.default\nIsRelative=1\n");
        using (Firefox(Path.Combine(FirefoxRoot, "Profiles", "a.default", "cookies.sqlite"), new FirefoxRow("qoder.com", "f", "1", 0, NowS - 60)))
        {
            Assert.Equal(BrowserKind.Chrome, Reader(new FixedUnwrapper(null)).Session(Qoder, NowMs, out _)?.Browser);
        }
    }

    [Fact]
    public void ExpiredRowsAreNoCandidate()
    {
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Plain("qoder.com", "stale", "s", NowS - 1, expiresS: NowS - 100),
            ChromeRow.Plain("qoder.com", "boundary", "b", NowS - 1, expiresS: NowS));
        Chrome(Path.Combine(EdgeData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Plain(".qoder.com", "live", "l", NowS - 900, expiresS: NowS + 1));

        var session = Reader(new FixedUnwrapper(null)).Session(Qoder, NowMs, out _);

        Assert.Equal(BrowserKind.Edge, session?.Browser);
        Assert.Equal("live=l", session?.Cookie);
    }

    [Fact]
    public void UnsafeRowsAreLeftOutOfTheHeader()
    {
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Plain("qoder.com", "a", "1", NowS - 10),
            ChromeRow.Plain("qoder.com", "b", "x\r\nX-Evil: 1", NowS - 10),
            ChromeRow.Plain("qoder.com", "c;d", "2", NowS - 10),
            ChromeRow.Plain("qoder.com", string.Empty, "3", NowS - 10));
        Chrome(Path.Combine(ChromeData, "Profile 1", "Network", "Cookies"), 24,
            ChromeRow.Plain("qoder.com.cn", "z", "\u0001", NowS));
        var reader = Reader(new FixedUnwrapper(null));

        Assert.Equal("a=1", reader.Session(Qoder, NowMs, out _)?.Cookie);
        Assert.Null(reader.Session(["qoder.com.cn"], NowMs, out var miss));
        Assert.Equal(BrowserSessionMiss.NoSession, miss);
    }

    [Fact]
    public void AppBoundCookiesAreProtectedAndAnOlderProfileServes()
    {
        LocalState(ChromeData, [1]);
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Sealed("qoder.com", "sid", Gcm("bound", _key, "qoder.com", prefix: "v20"), NowS - 10));
        Chrome(Path.Combine(ChromeData, "Profile 1", "Network", "Cookies"), 24,
            ChromeRow.Sealed("qoder.com", "sid", Gcm("bound", _key, "qoder.com", prefix: "v20"), NowS - 20),
            ChromeRow.Sealed("qoder.com", "old", Gcm("legacy", _key, "qoder.com"), NowS - 20));
        var unwrapper = new FixedUnwrapper(_key);
        var reader = Reader(unwrapper);

        Assert.Null(reader.Session(Qoder, NowMs, out var miss));
        Assert.Equal(BrowserSessionMiss.Protected, miss);
        Assert.Empty(unwrapper.Asked);

        WriteProfilesIni("[Profile0]\nPath=Profiles/a.default\n");
        using (Firefox(Path.Combine(FirefoxRoot, "Profiles", "a.default", "cookies.sqlite"), new FirefoxRow(".qoder.com", "ff", "1", 0, NowS - 3600)))
        {
            var session = reader.Session(Qoder, NowMs, out miss);
            Assert.Equal(BrowserSessionMiss.None, miss);
            Assert.Equal(BrowserKind.Firefox, session?.Browser);
            Assert.Equal("ff=1", session?.Cookie);
        }

        AssertNoCopyLeft();
    }

    [WindowsFact]
    public void AStoreTheBrowserHoldsIsSkippedAsLocked()
    {
        var store = Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24, ChromeRow.Plain("qoder.com", "c", "1", NowS - 10));
        var reader = Reader(new FixedUnwrapper(null));
        using (new FileStream(store, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(reader.Session(Qoder, NowMs, out var miss));
            Assert.Equal(BrowserSessionMiss.Locked, miss);
            AssertNoCopyLeft();

            WriteProfilesIni("[Profile0]\nPath=Profiles/a.default\n");
            using (Firefox(Path.Combine(FirefoxRoot, "Profiles", "a.default", "cookies.sqlite"), new FirefoxRow("qoder.com", "f", "1", 0, NowS - 3600)))
            {
                Assert.Equal("f=1", reader.Session(Qoder, NowMs, out miss)?.Cookie);
                Assert.Equal(BrowserSessionMiss.None, miss);
            }
        }

        Assert.Equal("c=1", reader.Session(Qoder, NowMs, out _)?.Cookie);
        AssertNoCopyLeft();
    }

    [Fact]
    public void FirefoxProfilesComeFromProfilesIni()
    {
        var elsewhere = _temp.Combine("Elsewhere", "xyz.custom");
        var outside = _temp.Combine("Outside", "evil");
        WriteProfilesIni(string.Join("\r\n",
            "[General]", "StartWithLastProfile=1", "",
            "[Profile0]", "Name=default-release", "IsRelative=1", "Path=Profiles/abc.default-release", "",
            "[Profile1]", "Name=custom", "IsRelative=0", "Path=" + elsewhere, "",
            "[Profile2]", "Name=escape", "IsRelative=1", "Path=../../../Outside/evil", "",
            "[Install308046B0AF4A39CB]", "Default=Profiles/abc.default-release"));
        Assert.Equal(
            [Path.Combine(FirefoxRoot, "Profiles", "abc.default-release"), elsewhere],
            FirefoxCookieStore.Profiles(FirefoxRoot));
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "compatibility.ini"), "[Compatibility]\r\nLastVersion=145.0.1_20251101000000/20251101000000\r\n");

        using var relative = Firefox(Path.Combine(FirefoxRoot, "Profiles", "abc.default-release", "cookies.sqlite"),
            new FirefoxRow(".qoder.com.cn", "rel", "r", NowS + 99, NowS - 300));
        using var absolute = Firefox(Path.Combine(elsewhere, "cookies.sqlite"),
            new FirefoxRow("qoder.com.cn", "abs", "a", (NowS + 99) * 1000, NowS - 100),
            new FirefoxRow("qoder.com.cn", "expired", "e", (NowS - 1) * 1000, NowS - 1),
            new FirefoxRow("qoder.com.cn", "expiredSeconds", "e", NowS - 1, NowS - 1));
        using var escaped = Firefox(Path.Combine(outside, "cookies.sqlite"), new FirefoxRow("qoder.com.cn", "evil", "x", 0, NowS));

        var session = Reader(new FixedUnwrapper(null)).Session(Qoder, NowMs, out var miss);

        Assert.Equal(BrowserSessionMiss.None, miss);
        Assert.Equal(BrowserKind.Firefox, session?.Browser);
        Assert.Equal("qoder.com.cn", session?.Site);
        Assert.Equal("abs=a", session?.Cookie);
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:145.0.1) Gecko/20100101 Firefox/145.0.1", session?.UserAgent);
        AssertNoCopyLeft();
    }

    [Fact]
    public void ARefusedKeyIsNotTriedAgainUntilReset()
    {
        LocalState(ChromeData, [9, 9]);
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 23,
            ChromeRow.Sealed("www.workbuddy.cn", "wb", Gcm("v", _key), NowS));
        var unwrapper = new FixedUnwrapper(null);
        var reader = Reader(unwrapper);

        Assert.Null(reader.Session(WorkBuddy, NowMs, out var miss));
        Assert.Equal(BrowserSessionMiss.Protected, miss);
        Assert.Null(reader.Session(WorkBuddy, NowMs, out miss));
        Assert.Equal(BrowserSessionMiss.Protected, miss);
        Assert.Equal([BrowserKind.Chrome], unwrapper.Asked);

        reader.ResetRefusals();
        unwrapper.Key = _key;
        Assert.Equal("wb=v", reader.Session(WorkBuddy, NowMs, out miss)?.Cookie);
        Assert.Equal(BrowserSessionMiss.None, miss);
        Assert.Equal([BrowserKind.Chrome, BrowserKind.Chrome], unwrapper.Asked);
    }

    [Fact]
    public void AMissingOrWrongKeyIsProtected()
    {
        Chrome(Path.Combine(BraveData, "Default", "Network", "Cookies"), 24,
            ChromeRow.Sealed("qoder.com", "sid", Gcm("b", _key, "qoder.com"), NowS));
        var unwrapper = new FixedUnwrapper(_key);

        Assert.Null(Reader(unwrapper).Session(Qoder, NowMs, out var miss));
        Assert.Equal(BrowserSessionMiss.Protected, miss);
        Assert.Empty(unwrapper.Asked);

        LocalState(BraveData, [7]);
        unwrapper.Key = RandomNumberGenerator.GetBytes(32);
        Assert.Null(Reader(unwrapper).Session(Qoder, NowMs, out miss));
        Assert.Equal(BrowserSessionMiss.Protected, miss);
        Assert.Equal([BrowserKind.Brave], unwrapper.Asked);

        unwrapper.Key = RandomNumberGenerator.GetBytes(16);
        Assert.Null(Reader(unwrapper).Session(Qoder, NowMs, out miss));
        Assert.Equal(BrowserSessionMiss.Protected, miss);
        Assert.All(unwrapper.HandedOut, key => Assert.All(key, b => Assert.Equal(0, b)));
    }

    [Fact]
    public void RowsAreCappedPerStore()
    {
        var rows = Enumerable.Range(0, 300).Select(i => ChromeRow.Plain("qoder.com", "n" + i, "v", NowS - i)).ToArray();
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24, rows);

        var header = Reader(new FixedUnwrapper(null)).Session(Qoder, NowMs, out _)?.Cookie;

        Assert.Equal(ChromeCookieStore.MaxRows, header?.Split("; ").Length);
    }

    [Fact]
    public void NothingOutsideTheRootsIsRead()
    {
        Chrome(Path.Combine(ChromeData, "Default", "Network", "Cookies"), 24, ChromeRow.Plain("qoder.com", "c", "1", NowS));
        var unwrapper = new FixedUnwrapper(null);

        Assert.Null(new BrowserCookieReader(new BrowserRoots("Local", "Roaming"), unwrapper, Copies).Session(Qoder, NowMs, out var miss));
        Assert.Equal(BrowserSessionMiss.NoSession, miss);
        Assert.Null(new BrowserCookieReader(new BrowserRoots(string.Empty, string.Empty), unwrapper, Copies).Session(Qoder, NowMs, out miss));
        Assert.Equal(BrowserSessionMiss.NoSession, miss);
        Assert.Null(Reader(unwrapper).Session(["", "QODER.COM", "%", ".qoder.com"], NowMs, out miss));
        Assert.Equal(BrowserSessionMiss.NoSession, miss);
        Assert.Null(Reader(unwrapper).Session([], NowMs, out miss));
        Assert.Equal(BrowserSessionMiss.NoSession, miss);
    }

    [Fact]
    public void ACorruptStoreIsSkipped()
    {
        var store = Path.Combine(ChromeData, "Default", "Network", "Cookies");
        Directory.CreateDirectory(Path.GetDirectoryName(store)!);
        File.WriteAllText(store, "not a database");
        Chrome(Path.Combine(ChromeData, "Profile 1", "Network", "Cookies"), 24, ChromeRow.Plain("qoder.com", "ok", "1", NowS - 100));

        Assert.Equal("ok=1", Reader(new FixedUnwrapper(null)).Session(Qoder, NowMs, out _)?.Cookie);
        AssertNoCopyLeft();
    }

    [Fact]
    public void StaleCopiesOfAnEarlierRunAreRemoved()
    {
        var stale = Directory.CreateDirectory(Path.Combine(Copies, CookieStoreCopy.FolderPrefix + "stale"));
        File.WriteAllText(Path.Combine(stale.FullName, "store.sqlite"), "x");
        stale.CreationTimeUtc = DateTime.UtcNow.AddHours(-1);
        var fresh = Directory.CreateDirectory(Path.Combine(Copies, CookieStoreCopy.FolderPrefix + "fresh"));
        var other = Directory.CreateDirectory(Path.Combine(Copies, "other"));
        other.CreationTimeUtc = DateTime.UtcNow.AddHours(-1);

        Reader(new FixedUnwrapper(null)).Session(Qoder, NowMs, out _);

        Assert.False(Directory.Exists(stale.FullName));
        Assert.True(Directory.Exists(fresh.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }

    private BrowserCookieReader Reader(IBrowserKeyUnwrapper unwrapper) => new(new BrowserRoots(Local, FirefoxRoot), unwrapper, Copies);

    private void WriteProfilesIni(string text)
    {
        Directory.CreateDirectory(FirefoxRoot);
        File.WriteAllText(Path.Combine(FirefoxRoot, "profiles.ini"), text);
    }

    private void AssertNoCopyLeft() => Assert.Empty(Directory.EnumerateFileSystemEntries(Copies));
}
