using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class LaunchArgumentsTests
{
    [Fact]
    public void NoArgumentsYieldNone()
    {
        Assert.Same(LaunchArguments.None, LaunchArguments.Parse(null));
        Assert.Same(LaunchArguments.None, LaunchArguments.Parse([]));
        Assert.Equal(new LaunchArguments(false, null, null, false), LaunchArguments.None);
    }

    [Fact]
    public void ParsesRelaunchAfterUpdate()
    {
        var args = LaunchArguments.Parse(["--after-update", "1.0.0", "--wait-pid", "1234"]);
        Assert.Equal(new LaunchArguments(true, "1.0.0", 1234, false), args);
    }

    [Fact]
    public void FlagsAreCaseInsensitive()
    {
        var args = LaunchArguments.Parse(["--AFTER-UPDATE", "1.0.0", "--Wait-Pid", "77", "--Check-Updates-Now"]);
        Assert.Equal(new LaunchArguments(true, "1.0.0", 77, true), args);
    }

    [Theory]
    [InlineData("v1.0.0", "1.0.0")]
    [InlineData("1.0.0-rc.2+build.5", "1.0.0-rc.2")]
    [InlineData(" 2.3.4 ", "2.3.4")]
    public void PreviousVersionIsNormalized(string value, string expected)
    {
        Assert.Equal(expected, LaunchArguments.Parse(["--after-update", value]).PreviousVersion);
    }

    [Fact]
    public void AfterUpdateWithoutVersionDoesNotConsumeTheNextFlag()
    {
        var args = LaunchArguments.Parse(["--after-update", "--wait-pid", "1234"]);
        Assert.Equal(new LaunchArguments(true, null, 1234, false), args);

        Assert.Equal(new LaunchArguments(true, null, null, false), LaunchArguments.Parse(["--after-update"]));
        Assert.Equal(new LaunchArguments(true, null, null, true), LaunchArguments.Parse(["--after-update", "--check-updates-now"]));
    }

    [Fact]
    public void InvalidPreviousVersionIsNotConsumed()
    {
        var args = LaunchArguments.Parse(["--after-update", "banana", "--wait-pid", "9"]);
        Assert.Equal(new LaunchArguments(true, null, 9, false), args);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("0")]
    [InlineData("abc")]
    [InlineData("99999999999")]
    [InlineData("2147483648")]
    [InlineData("12.5")]
    [InlineData("+12")]
    [InlineData(" 12")]
    [InlineData("1e3")]
    [InlineData("0x10")]
    [InlineData("\uff11\uff12")]
    [InlineData("")]
    public void InvalidPidIsIgnored(string value)
    {
        var args = LaunchArguments.Parse(["--wait-pid", value, "--check-updates-now"]);
        Assert.Null(args.WaitPid);
        Assert.True(args.CheckUpdatesNow);
        Assert.False(args.AfterUpdate);
    }

    [Fact]
    public void WaitPidAtTheEndIsIgnored()
    {
        Assert.Null(LaunchArguments.Parse(["--wait-pid"]).WaitPid);
        Assert.Equal(int.MaxValue, LaunchArguments.Parse(["--wait-pid", "2147483647"]).WaitPid);
    }

    [Fact]
    public void CheckUpdatesNow()
    {
        Assert.Equal(new LaunchArguments(false, null, null, true), LaunchArguments.Parse(["--check-updates-now"]));
    }

    [Fact]
    public void PreviewHistory()
    {
        var args = LaunchArguments.Parse(["--Preview-History", "--check-updates-now"]);
        Assert.True(args.PreviewHistory);
        Assert.Equal(new LaunchArguments(false, null, null, true) { PreviewHistory = true }, args);
        Assert.False(LaunchArguments.None.PreviewHistory);
        Assert.False(LaunchArguments.Parse(["--check-updates-now"]).PreviewHistory);
    }

    [Fact]
    public void DemoSpeedTakesACommaSeparatedList()
    {
        var args = LaunchArguments.Parse(["--demo-speed", "0, 24,96.5,400", "--preview-history"]);
        Assert.Equal("0,24,96.5,400", args.DemoSpeeds);
        Assert.Equal([0, 24, 96.5, 400], args.DemoSpeedValues);
        Assert.True(args.PreviewHistory);
        Assert.Null(LaunchArguments.None.DemoSpeeds);
        Assert.Empty(LaunchArguments.None.DemoSpeedValues);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1,,2")]
    [InlineData("NaN")]
    [InlineData("1e12")]
    [InlineData("")]
    public void InvalidDemoSpeedsAreNotConsumed(string value)
    {
        var args = LaunchArguments.Parse(["--demo-speed", value, "--check-updates-now"]);
        Assert.Null(args.DemoSpeeds);
        Assert.True(args.CheckUpdatesNow);
        Assert.Null(LaunchArguments.Parse(["--demo-speed"]).DemoSpeeds);
    }

    [Fact]
    public void UnknownArgumentsAreIgnored()
    {
        var args = LaunchArguments.Parse(["--verbose", "C:\\file.txt", "--after-update=1.0.0", "-after-update", "--wait-pid=5", "--after-update", "1.2.0", "extra"]);
        Assert.Equal(new LaunchArguments(true, "1.2.0", null, false), args);
        Assert.Equal(LaunchArguments.None, LaunchArguments.Parse(["--help", "1234", "1.0.0"]));
    }

    [Fact]
    public void ForRelaunchProducesExactArguments()
    {
        Assert.Equal(["--after-update", "1.2.0", "--wait-pid", "4321"], LaunchArguments.ForRelaunch("1.2.0", 4321));
    }

    [Theory]
    [InlineData("1.2.0", 4321)]
    [InlineData("0.9.1-beta.3", 1)]
    [InlineData("10.20.30", int.MaxValue)]
    public void ForRelaunchRoundTripsThroughParse(string previous, int pid)
    {
        var args = LaunchArguments.Parse(LaunchArguments.ForRelaunch(previous, pid));
        Assert.Equal(new LaunchArguments(true, previous, pid, false), args);
    }

    [Theory]
    [InlineData(new[] { "--hero-demo" }, "tour")]
    [InlineData(new[] { "--hero-demo", "startups" }, "startups")]
    [InlineData(new[] { "--HERO-DEMO", "idle" }, "idle")]
    [InlineData(new[] { "--hero-demo", "--reduce-motion" }, "tour")]
    [InlineData(new[] { "--hero-demo", "banana" }, "tour")]
    public void ParsesTheHeroDemo(string[] args, string scenario)
    {
        Assert.Equal(scenario, LaunchArguments.Parse(args).HeroDemo);
    }

    [Fact]
    public void ParsesTheHeroDevelopmentFlags()
    {
        var args = LaunchArguments.Parse(["--hero-glow", "strokes", "--reduce-motion", "--no-hero", "--hero-demo", "--wait-pid", "5"]);
        Assert.Equal("strokes", args.HeroGlow);
        Assert.True(args.ReduceMotion);
        Assert.True(args.NoHero);
        Assert.Equal("tour", args.HeroDemo);
        Assert.Equal(5, args.WaitPid);

        var plain = LaunchArguments.Parse(["--hero-glow", "neon"]);
        Assert.Null(plain.HeroGlow);
        Assert.False(plain.ReduceMotion);
        Assert.False(plain.NoHero);
        Assert.Null(plain.HeroDemo);
    }

    [Fact]
    public void ParsesTheOrbDevelopmentFlags()
    {
        var args = LaunchArguments.Parse(["--orb-demo", "multi", "--orb-style", "liquid", "--orb-count", "8", "--orb-size", "240",
            "--orb-tour", "--theme", "light", "--effects", "max", "--capture-dir", @"C:\shots"]);
        Assert.Equal("multi", args.OrbDemo);
        Assert.Equal("liquid", args.OrbStyle);
        Assert.Equal(8, args.OrbCount);
        Assert.Equal(240, args.OrbSize);
        Assert.True(args.OrbTour);
        Assert.Equal("light", args.Theme);
        Assert.Equal("max", args.Effects);
        Assert.True(args.EphemeralSettings);
    }

    [Theory]
    [InlineData("--orb-count", "9")]
    [InlineData("--orb-count", "0")]
    [InlineData("--orb-size", "999")]
    [InlineData("--orb-style", "plasma")]
    [InlineData("--theme", "sepia")]
    [InlineData("--capture-dir", "relative\\path")]
    public void RejectsInvalidOrbFlagValues(string flag, string value)
    {
        var args = LaunchArguments.Parse([flag, value]);
        Assert.Null(args.OrbCount);
        Assert.Null(args.OrbSize);
        Assert.Null(args.OrbStyle);
        Assert.Null(args.Theme);
        Assert.Null(args.CaptureDir);
        Assert.False(args.EphemeralSettings);
    }

    [Theory]
    [InlineData("names")]
    [InlineData("names-cluster")]
    [InlineData("names-crowd")]
    public void ParsesTheNamePlateDemos(string demo)
    {
        var args = LaunchArguments.Parse(["--orb-demo", demo]);
        Assert.Equal(demo, args.OrbDemo);
        Assert.True(args.EphemeralSettings);
    }

    [Fact]
    public void OrbDemoDefaultsToStylesAndNormalRunsKeepTheirSettings()
    {
        Assert.Equal("styles", LaunchArguments.Parse(["--orb-demo"]).OrbDemo);
        Assert.False(LaunchArguments.Parse(["--demo-speed", "100"]).EphemeralSettings);
    }
}
