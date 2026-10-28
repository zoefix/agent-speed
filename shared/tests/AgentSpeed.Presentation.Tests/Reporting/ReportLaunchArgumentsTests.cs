using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests.Reporting;

public class ReportLaunchArgumentsTests
{
    [Theory]
    [InlineData("http://127.0.0.1:8787")]
    [InlineData("http://127.0.0.1:8787/agent-speed")]
    [InlineData("https://127.0.0.1:8443")]
    [InlineData("http://localhost:8787")]
    [InlineData("http://LOCALHOST:8787")]
    [InlineData("http://[::1]:8787")]
    public void LoopbackUrlsAreAccepted(string url)
    {
        var args = LaunchArguments.Parse(["--report-e2e", url]);
        Assert.Equal(url, args.ReportE2E);
        Assert.False(args.EphemeralSettings);
    }

    [Theory]
    [InlineData("https://deng.codexradar.com")]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1.example.com:8787")]
    [InlineData("http://localhost.example.com")]
    [InlineData("http://user:pass@127.0.0.1:8787")]
    [InlineData("http://localhost:8787@evil.example.com")]
    [InlineData("ftp://127.0.0.1")]
    [InlineData("file:///C:/temp")]
    [InlineData("127.0.0.1:8787")]
    [InlineData("http://10.0.0.1:8787")]
    [InlineData("http://192.168.1.20:8787")]
    [InlineData("")]
    public void OtherUrlsAreRejected(string url)
    {
        Assert.Null(LaunchArguments.Parse(["--report-e2e", url]).ReportE2E);
    }

    [Fact]
    public void AMissingValueIsIgnored()
    {
        Assert.Null(LaunchArguments.Parse(["--report-e2e"]).ReportE2E);
        Assert.Null(LaunchArguments.Parse([]).ReportE2E);
        Assert.Null(LaunchArguments.None.ReportE2E);
    }

    [Fact]
    public void ARejectedValueDoesNotSwallowTheNextFlag()
    {
        var args = LaunchArguments.Parse(["--report-e2e", "--theme", "dark"]);
        Assert.Null(args.ReportE2E);
        Assert.Equal("dark", args.Theme);
    }

    [Fact]
    public void TheFlagIsCaseInsensitiveAndCombinesWithOthers()
    {
        var args = LaunchArguments.Parse(["--capture-dir", OperatingSystem.IsWindows() ? @"C:\AgentSpeedTest\shots" : "/tmp/shots",
            "--REPORT-E2E", "http://127.0.0.1:9911"]);
        Assert.Equal("http://127.0.0.1:9911", args.ReportE2E);
        Assert.NotNull(args.CaptureDir);
    }
}
