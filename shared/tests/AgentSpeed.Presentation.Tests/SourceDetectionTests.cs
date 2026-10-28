using AgentSpeed.Core;
using AgentSpeed.Presentation.Layout;

namespace AgentSpeed.Presentation.Tests;

public sealed class SourceDetectionTests
{
    [Theory]
    [InlineData(true, true, 0, 0, true)]
    [InlineData(false, true, 0, 0, true)]
    [InlineData(true, false, 0, 0, false)]
    [InlineData(false, false, 0, 0, false)]
    [InlineData(true, false, 2, 0, true)]
    [InlineData(true, false, 0, 5, true)]
    public void OnlyDetectedSourcesAreListed(bool enabled, bool available, int sessions, long requests, bool listed)
    {
        var status = new SourceStatus(
            SourceKind.DeepSeek, enabled, "/x/.dsh/sessions", available, sessions, Rate.Zero,
            new TokenTotals(Requests: requests), 0);

        Assert.Equal(listed, SourceDetection.IsDetected(status));
    }

    [Theory]
    [InlineData(true, true, 0, 0, 0, false)]
    [InlineData(true, true, 2, 0, 0, false)]
    [InlineData(true, true, 1, 120, 0, true)]
    [InlineData(false, true, 1, 120, 0, true)]
    [InlineData(true, false, 1, 120, 0, true)]
    [InlineData(true, true, 0, 0, 42, true)]
    public void OnlySourcesWithOutputTodayAreOnTheDashboard(bool enabled, bool available, long requests, long output, double live,
        bool listed)
    {
        var status = new SourceStatus(
            SourceKind.Codex, enabled, "/x/.codex", available, 3, new Rate(live, live * 2),
            new TokenTotals(Requests: requests, Input: output * 4, Output: output), 0);

        Assert.Equal(listed, SourceDetection.ProducedToday(status));
    }

    [Fact]
    public void NullStatusIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => SourceDetection.IsDetected(null!));
        Assert.Throws<ArgumentNullException>(() => SourceDetection.ProducedToday(null!));
    }
}
