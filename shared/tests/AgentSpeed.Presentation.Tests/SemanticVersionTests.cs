using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class SemanticVersionTests
{
    private static SemanticVersion Parse(string text)
    {
        Assert.True(SemanticVersion.TryParse(text, out var version), $"'{text}' should parse");
        return version;
    }

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, "")]
    [InlineData("v1.2.3", 1, 2, 3, "")]
    [InlineData("V1.2.3", 1, 2, 3, "")]
    [InlineData("  v10.20.30 \t\r\n", 10, 20, 30, "")]
    [InlineData("0.0.0", 0, 0, 0, "")]
    [InlineData("999999999.0.1", 999999999, 0, 1, "")]
    [InlineData("1.2.3-beta.1", 1, 2, 3, "beta.1")]
    [InlineData("1.2.3-rc-1.x-y.0", 1, 2, 3, "rc-1.x-y.0")]
    [InlineData("1.2.3-RC.1", 1, 2, 3, "RC.1")]
    [InlineData("1.2.3+build.5", 1, 2, 3, "")]
    [InlineData("1.2.3+build-7", 1, 2, 3, "")]
    [InlineData("v1.2.3-rc.1+sha.abc123", 1, 2, 3, "rc.1")]
    public void ParsesValidVersions(string text, int major, int minor, int patch, string preRelease)
    {
        var version = Parse(text);
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(preRelease, version.PreRelease);
        Assert.Equal(preRelease.Length > 0, version.IsPreRelease);
        Assert.Equal<SemanticVersion?>(version, SemanticVersion.ParseOrNull(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("1")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1..3")]
    [InlineData("1.2.")]
    [InlineData(".1.2")]
    [InlineData("1.-2.3")]
    [InlineData("-1.2.3")]
    [InlineData("+1.2.3")]
    [InlineData("a.b.c")]
    [InlineData("1.2.x")]
    [InlineData("vv1.2.3")]
    [InlineData("v 1.2.3")]
    [InlineData("1.2.3 beta")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-.beta")]
    [InlineData("1.2.3-beta.")]
    [InlineData("1.2.3-beta_1")]
    [InlineData("1.2.3-béta")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3+build..1")]
    [InlineData("1.2.3+a+b")]
    [InlineData("1234567890.0.0")]
    [InlineData("0.1234567890.0")]
    [InlineData("0.0.1234567890")]
    [InlineData("１.2.3")]
    [InlineData("١.٢.٣")]
    [InlineData("1.².3")]
    public void RejectsInvalidVersions(string? text)
    {
        Assert.False(SemanticVersion.TryParse(text, out var version));
        Assert.Equal(default(SemanticVersion), version);
        Assert.Null(SemanticVersion.ParseOrNull(text));
    }

    [Fact]
    public void RejectsInputLongerThanMaxLength()
    {
        var atLimit = "1.2.3-" + new string('a', SemanticVersion.MaxLength - 6);
        Assert.Equal(SemanticVersion.MaxLength, atLimit.Length);
        Assert.True(SemanticVersion.TryParse(atLimit, out _));
        Assert.True(SemanticVersion.TryParse("  " + atLimit + "  ", out _));
        Assert.False(SemanticVersion.TryParse(atLimit + "a", out _));
        Assert.False(SemanticVersion.TryParse("1.2.3+" + new string('b', SemanticVersion.MaxLength), out _));
        Assert.False(SemanticVersion.TryParse(new string('1', 100_000), out _));
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData(" V0.10.0-alpha-2 ", "0.10.0-alpha-2")]
    [InlineData("1.2.3-rc.1+build.9", "1.2.3-rc.1")]
    [InlineData("2.0.0+meta", "2.0.0")]
    public void ToStringIsCanonicalAndRoundTrips(string text, string expected)
    {
        var version = Parse(text);
        Assert.Equal(expected, version.ToString());
        Assert.Equal(version, Parse(version.ToString()));
    }

    [Theory]
    [InlineData("0.0.0", true)]
    [InlineData("v0.0.0-dev.1", true)]
    [InlineData("0.0.1", false)]
    [InlineData("0.1.0", false)]
    [InlineData("1.0.0", false)]
    public void IsDevelopmentMeansZeroVersion(string text, bool expected)
    {
        Assert.Equal(expected, Parse(text).IsDevelopment);
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("1.2.0", "1.10.0")]
    [InlineData("1.2.9", "1.2.10")]
    [InlineData("0.9.9", "1.0.0")]
    [InlineData("1.2.3-rc.1", "1.2.3")]
    [InlineData("1.2.3", "1.2.4-alpha")]
    [InlineData("1.2.3-alpha", "1.2.3-alpha.0")]
    [InlineData("1.2.3-1", "1.2.3-a")]
    [InlineData("1.2.3-999", "1.2.3-alpha")]
    [InlineData("1.2.3-9", "1.2.3-10")]
    [InlineData("1.2.3-a10", "1.2.3-a9")]
    [InlineData("1.2.3-Beta", "1.2.3-alpha")]
    [InlineData("1.2.3-alpha", "1.2.3-alpha-1")]
    [InlineData("1.2.3-rc.99999999999999999999", "1.2.3-rc.100000000000000000000")]
    public void LowerVersionHasLowerPrecedence(string lowerText, string higherText)
    {
        var lower = Parse(lowerText);
        var higher = Parse(higherText);

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(lower) > 0);
        Assert.True(lower < higher);
        Assert.True(lower <= higher);
        Assert.True(higher > lower);
        Assert.True(higher >= lower);
        Assert.False(lower > higher);
        Assert.False(lower >= higher);
        Assert.True(lower != higher);
        Assert.True(higher.IsNewerThan(lower));
        Assert.False(lower.IsNewerThan(higher));
    }

    [Fact]
    public void PrecedenceMatchesSemVerSpecificationExample()
    {
        string[] ordered =
        [
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        ];
        var versions = ordered.Select(Parse).ToArray();

        for (var i = 0; i < versions.Length; i++)
        {
            for (var j = 0; j < versions.Length; j++)
            {
                Assert.Equal(i.CompareTo(j), Math.Sign(versions[i].CompareTo(versions[j])));
            }
        }

        var shuffled = versions.Reverse().ToList();
        shuffled.Sort();
        Assert.Equal(versions, shuffled);
        Assert.Equal(versions[^1], versions.Max());
    }

    [Theory]
    [InlineData("v1.2.0", "1.2.0")]
    [InlineData("1.2.0+build.1", "1.2.0+build.2")]
    [InlineData("V1.2.0-rc.1+x", " 1.2.0-rc.1 ")]
    public void EqualVersionsIgnorePrefixWhitespaceAndMetadata(string a, string b)
    {
        var x = Parse(a);
        var y = Parse(b);

        Assert.Equal(x, y);
        Assert.True(x == y);
        Assert.False(x != y);
        Assert.True(x.Equals((object)y));
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
        Assert.Equal(0, x.CompareTo(y));
        Assert.True(x <= y);
        Assert.True(x >= y);
        Assert.False(x < y);
        Assert.False(x > y);
        Assert.False(x.IsNewerThan(y));
    }

    [Theory]
    [InlineData("1.2.0", "1.2.0-rc.1")]
    [InlineData("1.2.0", "1.2.1")]
    [InlineData("1.2.0-rc.1", "1.2.0-rc.2")]
    public void DifferentVersionsAreNotEqual(string a, string b)
    {
        Assert.NotEqual(Parse(a), Parse(b));
        Assert.True(Parse(a) != Parse(b));
    }

    [Fact]
    public void ConstructorValidatesArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticVersion(-1, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticVersion(0, -1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SemanticVersion(0, 0, -1));
        Assert.Throws<ArgumentException>(() => new SemanticVersion(1, 0, 0, "beta..1"));
        Assert.Throws<ArgumentException>(() => new SemanticVersion(1, 0, 0, "."));
        Assert.Throws<ArgumentException>(() => new SemanticVersion(1, 0, 0, "béta"));
        Assert.Throws<ArgumentException>(() => new SemanticVersion(1, 0, 0, "rc 1"));
        Assert.Throws<ArgumentException>(() => new SemanticVersion(1, 0, 0, "rc+1"));

        var version = new SemanticVersion(1, 2, 3, "rc.1");
        Assert.Equal("1.2.3-rc.1", version.ToString());
        Assert.Equal(Parse("v1.2.3-rc.1"), version);

        var empty = new SemanticVersion(1, 2, 3, string.Empty);
        Assert.False(empty.IsPreRelease);
        Assert.Equal(string.Empty, empty.PreRelease);
        Assert.Equal(new SemanticVersion(1, 2, 3), empty);
        Assert.Equal(new SemanticVersion(1, 2, 3, null), empty);
    }

    [Fact]
    public void DefaultIsTheZeroReleaseVersion()
    {
        SemanticVersion version = default;

        Assert.Equal(0, version.Major);
        Assert.Equal(0, version.Minor);
        Assert.Equal(0, version.Patch);
        Assert.Equal(string.Empty, version.PreRelease);
        Assert.False(version.IsPreRelease);
        Assert.True(version.IsDevelopment);
        Assert.Equal("0.0.0", version.ToString());
        Assert.Equal(new SemanticVersion(0, 0, 0), version);
        Assert.Equal(Parse("v0.0.0"), version);
        Assert.True(Parse("0.0.1") > version);
        Assert.True(Parse("0.0.0-alpha") < version);
    }
}
