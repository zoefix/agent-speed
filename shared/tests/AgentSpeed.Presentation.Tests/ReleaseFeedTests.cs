using System.Text;
using System.Text.Json.Nodes;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class ReleaseFeedTests
{
    private const string Download = "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/";

    private const string LatestJson = """
        {
          "url": "https://api.github.com/repos/zoefix/agent-speed/releases/181234567",
          "assets_url": "https://api.github.com/repos/zoefix/agent-speed/releases/181234567/assets",
          "upload_url": "https://uploads.github.com/repos/zoefix/agent-speed/releases/181234567/assets{?name,label}",
          "html_url": "https://github.com/zoefix/agent-speed/releases/tag/v1.2.0",
          "id": 181234567,
          "author": {
            "login": "github-actions[bot]",
            "id": 41898282,
            "node_id": "MDM6Qm90NDE4OTgyODI=",
            "type": "Bot",
            "site_admin": false
          },
          "node_id": "RE_kwDOMZ7nT84Kzz0H",
          "tag_name": "v1.2.0",
          "target_commitish": "main",
          "name": "Agent Speed v1.2.0",
          "draft": false,
          "immutable": false,
          "prerelease": false,
          "created_at": "2026-10-01T11:58:03Z",
          "updated_at": "2026-10-01T12:00:41Z",
          "published_at": "2026-10-01T12:00:00Z",
          "assets": [
            {
              "url": "https://api.github.com/repos/zoefix/agent-speed/releases/assets/1",
              "id": 1,
              "node_id": "RA_kwDOMZ7nT84A",
              "name": "AgentSpeed-macOS.zip",
              "label": "",
              "uploader": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" },
              "content_type": "application/zip",
              "state": "uploaded",
              "size": 41234567,
              "digest": "sha256:0000000000000000000000000000000000000000000000000000000000000000",
              "download_count": 12,
              "created_at": "2026-10-01T11:59:00Z",
              "updated_at": "2026-10-01T11:59:10Z",
              "browser_download_url": "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-macOS.zip"
            },
            {
              "url": "https://api.github.com/repos/zoefix/agent-speed/releases/assets/2",
              "id": 2,
              "name": "AgentSpeed-Windows-x64.zip",
              "label": null,
              "uploader": { "login": "github-actions[bot]", "id": 41898282, "type": "Bot" },
              "content_type": "application/zip",
              "state": "uploaded",
              "size": 73400320,
              "download_count": 34,
              "browser_download_url": "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-Windows-x64.zip"
            },
            {
              "id": 3,
              "name": "AgentSpeed-Windows-arm64.zip",
              "uploader": { "login": "github-actions[bot]" },
              "size": 70123456,
              "browser_download_url": "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/AgentSpeed-Windows-arm64.zip"
            },
            {
              "id": 4,
              "name": "SHA256SUMS.txt",
              "content_type": "text/plain",
              "size": 297,
              "browser_download_url": "https://github.com/zoefix/agent-speed/releases/download/v1.2.0/SHA256SUMS.txt"
            }
          ],
          "tarball_url": "https://api.github.com/repos/zoefix/agent-speed/tarball/v1.2.0",
          "zipball_url": "https://api.github.com/repos/zoefix/agent-speed/zipball/v1.2.0",
          "body": "## What's Changed\r\n* Faster gauge by @zoefix in https://github.com/zoefix/agent-speed/pull/12\r\n\r\n**Full Changelog**: https://github.com/zoefix/agent-speed/compare/v1.1.0...v1.2.0",
          "reactions": { "url": "https://api.github.com/x", "total_count": 3, "+1": 2, "heart": 1 },
          "mentions_count": 1
        }
        """;

    private static JsonObject Latest() => JsonNode.Parse(LatestJson)!.AsObject();

    private static ReleaseInfo? Parse(JsonNode node) => ReleaseFeed.Parse(node.ToJsonString());

    private static JsonObject Release(string tag, bool draft = false, bool prerelease = false, string? name = null) => new()
    {
        ["tag_name"] = tag,
        ["name"] = name ?? tag,
        ["draft"] = draft,
        ["prerelease"] = prerelease,
        ["assets"] = new JsonArray(),
    };

    private static JsonObject GoodAsset(string name = "AgentSpeed-Windows-x64.zip") => new()
    {
        ["name"] = name,
        ["size"] = 1234,
        ["browser_download_url"] = Download + name,
    };

    private static void AssertInvalid(Action parse)
    {
        var exception = Assert.Throws<UpdateException>(parse);
        Assert.Equal(UpdateFailure.Invalid, exception.Failure);
    }

    [Fact]
    public void ParsesGitHubLatestRelease()
    {
        var release = ReleaseFeed.Parse(LatestJson);

        Assert.NotNull(release);
        Assert.Equal("v1.2.0", release.Tag);
        Assert.Equal(new SemanticVersion(1, 2, 0), release.Version);
        Assert.Equal("1.2.0", release.DisplayVersion);
        Assert.Equal("Agent Speed v1.2.0", release.Name);
        Assert.StartsWith("## What's Changed\r\n* Faster gauge", release.Notes, StringComparison.Ordinal);
        Assert.EndsWith("compare/v1.1.0...v1.2.0", release.Notes, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://github.com/zoefix/agent-speed/releases/tag/v1.2.0"), release.PageUrl);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), release.PublishedAt);
        Assert.Equal(TimeSpan.Zero, release.PublishedAt!.Value.Offset);
        Assert.False(release.IsDraft);
        Assert.False(release.IsPreRelease);
        Assert.True(release.IsOfferable);

        Assert.Equal(
            ["AgentSpeed-macOS.zip", "AgentSpeed-Windows-x64.zip", "AgentSpeed-Windows-arm64.zip", "SHA256SUMS.txt"],
            release.Assets.Select(a => a.Name));
        var x64 = release.FindAsset("AgentSpeed-Windows-x64.zip");
        Assert.NotNull(x64);
        Assert.Equal(73400320, x64.Size);
        Assert.Equal(new Uri(Download + "AgentSpeed-Windows-x64.zip"), x64.DownloadUrl);
        Assert.Equal(297, release.FindAsset("SHA256SUMS.txt")!.Size);
        Assert.Null(release.FindAsset("sha256sums.txt"));
        Assert.Null(release.FindAsset("AgentSpeed-Windows-x64"));
    }

    [Fact]
    public void Utf8OverloadMatchesStringOverload()
    {
        var fromBytes = ReleaseFeed.Parse(Encoding.UTF8.GetBytes(LatestJson));
        var fromString = ReleaseFeed.Parse(LatestJson);

        Assert.NotNull(fromBytes);
        Assert.Equal(fromString!.Tag, fromBytes.Tag);
        Assert.Equal(fromString.Assets.ToArray(), fromBytes.Assets.ToArray());
        Assert.Throws<ArgumentNullException>(() => ReleaseFeed.Parse((string)null!));
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void DraftsAndPreReleasesAreNeverOffered(string flag)
    {
        var json = Latest();
        json[flag] = true;
        Assert.Null(Parse(json));
    }

    [Theory]
    [InlineData("v1.3.0-rc.1")]
    [InlineData("v2.0.0-beta")]
    public void PreReleaseTagsAreNeverOfferedEvenWithoutTheFlag(string tag)
    {
        var json = Latest();
        json["tag_name"] = tag;
        Assert.Null(Parse(json));
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("v1.2")]
    [InlineData("release-1.2.0")]
    [InlineData("")]
    [InlineData("   ")]
    public void UnparseableTagIsNotOffered(string tag)
    {
        var json = Latest();
        json["tag_name"] = tag;
        Assert.Null(Parse(json));
    }

    [Fact]
    public void MissingOrNonStringTagIsNotOffered()
    {
        var missing = Latest();
        missing.Remove("tag_name");
        Assert.Null(Parse(missing));

        var number = Latest();
        number["tag_name"] = 120;
        Assert.Null(Parse(number));
    }

    [Fact]
    public void TagIsTrimmed()
    {
        var json = Latest();
        json["tag_name"] = "  v1.2.0 ";
        Assert.Equal("v1.2.0", Parse(json)!.Tag);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNameFallsBackToTag(string? name)
    {
        var json = Latest();
        json["name"] = name;
        Assert.Equal("v1.2.0", Parse(json)!.Name);

        json.Remove("name");
        Assert.Equal("v1.2.0", Parse(json)!.Name);
    }

    [Fact]
    public void NameIsTrimmedAndLengthLimited()
    {
        var json = Latest();
        json["name"] = "  Big release  ";
        Assert.Equal("Big release", Parse(json)!.Name);

        json["name"] = new string('n', ReleaseFeed.MaxNameLength + 50);
        Assert.Equal(ReleaseFeed.MaxNameLength, Parse(json)!.Name.Length);
    }

    public static TheoryData<string> InvalidAssets =>
    [
        """{ "name": "a.zip", "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": -1, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": "1234", "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": 12.5, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": 1e30, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": null, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "size": 10, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "", "size": 10, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "   ", "size": 10, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": 7, "size": 10, "browser_download_url": "https://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": 10 }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": 5 }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "/zoefix/agent-speed/releases/download/v1.2.0/a.zip" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "releases/download/a.zip" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "ftp://github.com/a.zip" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "file:///etc/passwd" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "javascript:alert(1)" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "https://user:pass@github.com/a.zip" }""",
        """{ "name": "a.zip", "size": 10, "browser_download_url": "https://token@github.com/a.zip" }""",
        "\"AgentSpeed-Windows-x64.zip\"",
        "42",
        "null",
        "[]",
    ];

    [Theory]
    [MemberData(nameof(InvalidAssets))]
    public void InvalidAssetsAreSkipped(string assetJson)
    {
        var json = Latest();
        json["assets"] = new JsonArray(JsonNode.Parse(assetJson), GoodAsset());

        var release = Parse(json);

        var asset = Assert.Single(release!.Assets);
        Assert.Equal("AgentSpeed-Windows-x64.zip", asset.Name);
        Assert.Equal(1234, asset.Size);
    }

    [Fact]
    public void AssetLimitsAreEnforced()
    {
        var json = Latest();
        json["assets"] = new JsonArray(
            GoodAsset(new string('n', ReleaseFeed.MaxAssetNameLength + 1)),
            new JsonObject
            {
                ["name"] = "long-url.zip",
                ["size"] = 1,
                ["browser_download_url"] = Download + new string('u', ReleaseFeed.MaxUrlLength),
            },
            GoodAsset(new string('n', ReleaseFeed.MaxAssetNameLength)));
        Assert.Equal(new string('n', ReleaseFeed.MaxAssetNameLength), Assert.Single(Parse(json)!.Assets).Name);

        var many = new JsonArray();
        for (var i = 0; i < ReleaseFeed.MaxAssets + 50; i++)
        {
            many.Add(GoodAsset($"asset-{i}.zip"));
        }

        json["assets"] = many;
        var assets = Parse(json)!.Assets;
        Assert.Equal(ReleaseFeed.MaxAssets, assets.Length);
        Assert.Equal("asset-0.zip", assets[0].Name);
    }

    [Fact]
    public void FeedKeepsZeroSizeAndHttpAssetsForThePlanToJudge()
    {
        var json = Latest();
        json["assets"] = new JsonArray(
            new JsonObject { ["name"] = "empty.zip", ["size"] = 0, ["browser_download_url"] = Download + "empty.zip" },
            new JsonObject { ["name"] = "plain.zip", ["size"] = 5, ["browser_download_url"] = "http://github.com/plain.zip" });

        var assets = Parse(json)!.Assets;

        Assert.Equal(["empty.zip", "plain.zip"], assets.Select(a => a.Name));
        Assert.Equal(0, assets[0].Size);
        Assert.Equal("http", assets[1].DownloadUrl.Scheme);
    }

    [Fact]
    public void MissingOrNonArrayAssetsYieldNoAssets()
    {
        var missing = Latest();
        missing.Remove("assets");
        Assert.Empty(Parse(missing)!.Assets);

        var wrongType = Latest();
        wrongType["assets"] = new JsonObject { ["name"] = "a.zip" };
        Assert.Empty(Parse(wrongType)!.Assets);
    }

    [Fact]
    public void ArrayRootPicksHighestOfferableVersion()
    {
        var json = new JsonArray(
            42,
            "text",
            null,
            new JsonArray(),
            Release("nightly"),
            Release("v9.0.0", draft: true),
            Release("v8.0.0", prerelease: true),
            Release("v1.1.0"),
            Release("v1.3.0", name: "first 1.3.0"),
            Release("1.3.0", name: "second 1.3.0"),
            Release("v1.3.0-rc.1"),
            Release("v1.2.0"));

        var release = Parse(json);

        Assert.NotNull(release);
        Assert.Equal("v1.3.0", release.Tag);
        Assert.Equal("first 1.3.0", release.Name);
    }

    [Fact]
    public void ArrayRootWithoutOfferableReleasesYieldsNull()
    {
        Assert.Null(ReleaseFeed.Parse("[]"));
        Assert.Null(Parse(new JsonArray(Release("v2.0.0", draft: true), Release("v3.0.0", prerelease: true), Release("x"))));
    }

    [Fact]
    public void ArrayRootReadsAtMostMaxReleases()
    {
        var json = new JsonArray();
        for (var i = 0; i < ReleaseFeed.MaxReleases; i++)
        {
            json.Add(Release("v1.0.0"));
        }

        json.Add(Release("v9.9.9"));

        Assert.Equal("v1.0.0", Parse(json)!.Tag);
    }

    [Theory]
    [InlineData("\"v1.2.0\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("true")]
    public void NonObjectOrArrayRootThrows(string json)
    {
        AssertInvalid(() => ReleaseFeed.Parse(json));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{")]
    [InlineData("{\"tag_name\": }")]
    [InlineData("{\"tag_name\": \"v1.2.0\",}")]
    [InlineData("[{\"tag_name\": \"v1.2.0\"},]")]
    [InlineData("{\"tag_name\": \"v1.2.0\"} // comment")]
    [InlineData("{} {}")]
    [InlineData("{'tag_name': 'v1.2.0'}")]
    [InlineData("{\"size\": NaN}")]
    [InlineData("<html>rate limited</html>")]
    public void MalformedJsonThrows(string json)
    {
        var exception = Assert.Throws<UpdateException>(() => ReleaseFeed.Parse(json));
        Assert.Equal(UpdateFailure.Invalid, exception.Failure);
        Assert.NotNull(exception.InnerException);
    }

    [Fact]
    public void ExcessiveNestingThrows()
    {
        var json = "{\"tag_name\": \"v1.2.0\", \"x\": " + new string('[', 40) + new string(']', 40) + "}";
        AssertInvalid(() => ReleaseFeed.Parse(json));
    }

    [Theory]
    [InlineData("tag_name")]
    [InlineData("name")]
    [InlineData("body")]
    public void InvalidUtf8Throws(string field)
    {
        byte[] json = [.. Encoding.UTF8.GetBytes($"{{\"tag_name\": \"v1.2.0\", \"{field}\": \"x"), 0xC3, 0x28, .. "\"}"u8];
        AssertInvalid(() => ReleaseFeed.Parse(json));
    }

    [Fact]
    public void InvalidUtf8InIgnoredFieldsIsHarmless()
    {
        byte[] json = [.. "{\"tag_name\": \"v1.2.0\", \"label\": \"x"u8, 0xC3, 0x28, .. "\"}"u8];
        Assert.Equal("v1.2.0", ReleaseFeed.Parse(json)!.Tag);
    }

    [Fact]
    public void OversizeFeedThrows()
    {
        const string head = "{\"tag_name\": \"v1.2.0\"}";
        var atLimit = Encoding.UTF8.GetBytes(head + new string(' ', ReleaseFeed.MaxFeedBytes - head.Length));
        Assert.Equal(ReleaseFeed.MaxFeedBytes, atLimit.Length);
        Assert.Equal("v1.2.0", ReleaseFeed.Parse(atLimit)!.Tag);

        var overLimit = Encoding.UTF8.GetBytes(head + new string(' ', ReleaseFeed.MaxFeedBytes - head.Length + 1));
        AssertInvalid(() => ReleaseFeed.Parse(overLimit));

        var json = Latest();
        json["body"] = new string('a', ReleaseFeed.MaxFeedBytes);
        AssertInvalid(() => Parse(json));
    }

    [Fact]
    public void NotesAreTruncatedToMaxLength()
    {
        var json = Latest();
        json["body"] = new string('a', ReleaseFeed.MaxNotesLength + 10_000);
        Assert.Equal(ReleaseFeed.MaxNotesLength, Parse(json)!.Notes.Length);

        json["body"] = new string('a', ReleaseFeed.MaxNotesLength - 1) + "\U0001F680 tail";
        var notes = Parse(json)!.Notes;
        Assert.Equal(ReleaseFeed.MaxNotesLength - 1, notes.Length);
        Assert.False(char.IsHighSurrogate(notes[^1]));
    }

    [Fact]
    public void MissingOrNonStringBodyYieldsEmptyNotes()
    {
        var missing = Latest();
        missing.Remove("body");
        Assert.Equal(string.Empty, Parse(missing)!.Notes);

        var wrongType = Latest();
        wrongType["body"] = 3;
        Assert.Equal(string.Empty, Parse(wrongType)!.Notes);
    }

    [Theory]
    [InlineData("2026-10-01T12:00:00Z")]
    [InlineData("2026-10-01T14:00:00+02:00")]
    [InlineData("2026-10-01T07:00:00-05:00")]
    [InlineData("2026-10-01T12:00:00")]
    public void PublishedAtIsUtc(string text)
    {
        var json = Latest();
        json["published_at"] = text;

        var published = Parse(json)!.PublishedAt;

        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), published);
        Assert.Equal(TimeSpan.Zero, published!.Value.Offset);
    }

    [Fact]
    public void InvalidPublishedAtIsNull()
    {
        var json = Latest();
        json["published_at"] = "yesterday";
        Assert.Null(Parse(json)!.PublishedAt);

        json["published_at"] = 1_759_320_000;
        Assert.Null(Parse(json)!.PublishedAt);

        json.Remove("published_at");
        Assert.Null(Parse(json)!.PublishedAt);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@github.com/zoefix/agent-speed/releases/tag/v1.2.0")]
    [InlineData("/zoefix/agent-speed/releases/tag/v1.2.0")]
    [InlineData("")]
    public void UnusablePageUrlIsDropped(string url)
    {
        var json = Latest();
        json["html_url"] = url;

        var release = Parse(json);

        Assert.NotNull(release);
        Assert.Null(release.PageUrl);
    }
}
