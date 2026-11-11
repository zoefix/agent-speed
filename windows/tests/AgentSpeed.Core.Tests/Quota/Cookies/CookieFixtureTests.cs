using System.Text;
using System.Text.Json;
using AgentSpeed.Core.Quota.Cookies;

namespace AgentSpeed.Core.Tests.Quota.Cookies;

/// <summary>The cookie cases of spec/fixtures/quota-cases.json (QUOTAS 10): decryption vectors, the header, the user agent.</summary>
public sealed class CookieFixtureTests
{
    private static readonly Lazy<JsonElement> Vendors = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(FixtureLocator.PathOf("quota-cases.json"))).RootElement.GetProperty("vendors").Clone());

    private static JsonElement Cookies => Vendors.Value.GetProperty("cookies");

    private static string Text(JsonElement item, string name) => item.GetProperty(name).GetString()!;

    [Theory]
    [InlineData("macV10")]
    [InlineData("macV10HostHash")]
    [InlineData("linuxV10")]
    [InlineData("linuxV11HostHash")]
    public void ChromeCbcVectorsDecryptAsTheReference(string name)
    {
        var vector = Cookies.GetProperty(name);
        var iterations = vector.GetProperty("iterations").GetInt32();
        var meta = vector.GetProperty("metaVersion").GetInt32();
        var blob = Convert.FromHexString(Text(vector, "blob"));
        var expected = Text(vector, "expected");
        Assert.True(ChromeCookieCrypto.IsV10OrV11(blob));

        var key = ChromeCookieCrypto.CbcKey(Encoding.UTF8.GetBytes(Text(vector, "password")), iterations);
        Assert.NotNull(key);
        Assert.Equal(expected, ChromeCookieCrypto.DecryptCbc(blob, key, meta));

        var wrong = ChromeCookieCrypto.CbcKey("nope"u8, iterations);
        Assert.NotEqual(expected, ChromeCookieCrypto.DecryptCbc(blob, wrong, meta));
        Assert.Null(ChromeCookieCrypto.DecryptCbc(blob[..^1], key, meta));
    }

    [Fact]
    public void CbcKeysNeedIterations()
    {
        Assert.Equal(1003, ChromeCookieCrypto.MacIterations);
        Assert.Equal(1, ChromeCookieCrypto.LinuxIterations);
        Assert.Null(ChromeCookieCrypto.CbcKey("peanuts"u8, 0));
        Assert.Equal(16, ChromeCookieCrypto.CbcKey("peanuts"u8, 1)!.Length);
    }

    [Fact]
    public void WindowsGcmVectorDecryptsAsTheReference()
    {
        var vector = Cookies.GetProperty("windowsGcm");
        var nonce = Convert.FromHexString(Text(vector, "nonce"));
        var sealedBytes = Convert.FromHexString(Text(vector, "ciphertextAndTag"));
        var blobText = Text(vector, "blob");
        Assert.StartsWith("v10", blobText, StringComparison.Ordinal);
        Assert.Equal(nonce, Convert.FromHexString(blobText[3..]));
        byte[] blob = [.. "v10"u8, .. nonce, .. sealedBytes];
        var key = Convert.FromHexString(Text(vector, "key"));
        var plaintext = Text(vector, "plaintext");

        Assert.Equal(plaintext, ChromeCookieCrypto.DecryptGcm(blob, key, 23));
        byte[] v11 = [.. "v11"u8, .. nonce, .. sealedBytes];
        Assert.Equal(plaintext, ChromeCookieCrypto.DecryptGcm(v11, key, 23));

        var tampered = blob.ToArray();
        tampered[^1] ^= 1;
        Assert.Null(ChromeCookieCrypto.DecryptGcm(tampered, key, 23));
        Assert.Null(ChromeCookieCrypto.DecryptGcm([.. "v20"u8, .. nonce, .. sealedBytes], key, 23));
        Assert.Null(ChromeCookieCrypto.DecryptGcm(blob, new byte[32], 23));
        Assert.Null(ChromeCookieCrypto.DecryptGcm(blob, key[..16], 23));
        Assert.Null(ChromeCookieCrypto.DecryptGcm(blob[..20], key, 23));
        // From version 24 the first 32 bytes are the host hash; this plaintext is shorter than that.
        Assert.Null(ChromeCookieCrypto.DecryptGcm(blob, key, 24));
    }

    [Fact]
    public void CookieHeaderJoinsAsTheReference()
    {
        var item = Vendors.Value.GetProperty("cookieHeader");
        var rows = item.GetProperty("rows").EnumerateArray().Select(row => new BrowserCookie
        {
            Host = row[0].GetString()!,
            Name = row[1].GetString()!,
            Value = row[2].GetString()!,
            ExpiresS = row[3].GetInt64(),
        }).ToList();
        var site = Text(item, "site");
        var nowS = item.GetProperty("nowS").GetInt64();

        Assert.Equal(Text(item, "expected"), CookieHeader.Join(rows, site, nowS));
        Assert.Null(CookieHeader.Join([], "qoder.com", 0));
    }

    [Theory]
    [InlineData("a", "b\r\nX-Evil: 1")]
    [InlineData("a", "tab\there")]
    [InlineData("a", "del\u007f")]
    [InlineData("a", "café")]
    [InlineData("a;b", "v")]
    [InlineData("a=b", "v")]
    [InlineData("a\nb", "v")]
    [InlineData("", "v")]
    public void UnsafeCookiesAreNeverSent(string name, string value)
    {
        var cookie = new BrowserCookie { Host = "qoder.com", Name = name, Value = value };
        Assert.False(CookieHeader.IsSendable(cookie));
        Assert.Null(CookieHeader.Join([cookie], "qoder.com", 0));
    }

    [Theory]
    [InlineData("qoder.com", true)]
    [InlineData(".qoder.com", true)]
    [InlineData("api.qoder.com", true)]
    [InlineData(".api.qoder.com", true)]
    [InlineData("evilqoder.com", false)]
    [InlineData("qoder.com.cn", false)]
    [InlineData("qoder.co", false)]
    public void HostsMatchTheSiteAndItsSubdomains(string host, bool matches) =>
        Assert.Equal(matches, CookieHeader.Matches(host, "qoder.com"));

    [Fact]
    public void LikePatternsEscapeTheSite()
    {
        Assert.Equal(("a_b%.cn", ".a_b%.cn", @"%.a\_b\%.cn"), CookieStoreCopy.HostArguments("a_b%.cn"));
    }

    [Fact]
    public void UserAgentsFollowTheBrowser()
    {
        Assert.Equal(Vendors.Value.GetProperty("userAgent").GetString(), BrowserUserAgent.Chrome("Macintosh; Intel Mac OS X 10_15_7", 142));
        Assert.Equal(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/142.0.0.0 Safari/537.36",
            BrowserUserAgent.Chrome(BrowserUserAgent.WindowsToken, BrowserUserAgent.DefaultChromeMajor));
        Assert.Equal(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:144.0) Gecko/20100101 Firefox/144.0",
            BrowserUserAgent.Firefox(BrowserUserAgent.WindowsToken, BrowserUserAgent.DefaultFirefoxVersion));
    }

    [Fact]
    public void ChromeTimesAreMicrosecondsSince1601()
    {
        Assert.Equal(0, ChromeCookieStore.Seconds(0));
        Assert.Equal(BrowserStores.NowS, ChromeCookieStore.Seconds(BrowserStores.ChromeTime(BrowserStores.NowS)));
        Assert.Equal(1, ChromeCookieStore.Seconds(5));
        Assert.Equal(BrowserStores.NowMs, ChromeCookieStore.Milliseconds(BrowserStores.ChromeTime(BrowserStores.NowS)));
    }

    [Fact]
    public void NothingHoldingACookieShowsIt()
    {
        var session = new BrowserSession("qoder.com", "sid=s3cret", "UA", BrowserKind.Edge);
        Assert.DoesNotContain("s3cret", session.ToString(), StringComparison.Ordinal);
        var cookie = new BrowserCookie { Host = "qoder.com", Name = "sid", Value = "s3cret" };
        Assert.DoesNotContain("s3cret", cookie.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("s3cret", cookie.WithValue("s3cret").ToString(), StringComparison.Ordinal);
    }
}
