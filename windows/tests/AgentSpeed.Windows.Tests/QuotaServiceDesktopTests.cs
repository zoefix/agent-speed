using AgentSpeed.Core;
using AgentSpeed.Core.Quota;
using AgentSpeed.Quota;

namespace AgentSpeed.Windows.Tests;

/// <summary>QUOTAS 6: Claude Desktop counts as signed in while it runs and its snapshot is at most 24 hours old.</summary>
public class QuotaServiceDesktopTests
{
    private const long Now = 1_800_000_000_000;

    private static QuotaSnapshot Desktop(long agoMinutes) =>
        new(SourceKind.ClaudeDesktop, Now - agoMinutes * 60_000, [new QuotaWindow(QuotaWindowKind.SevenDay, null, null, 40, null)]);

    /// <summary>
    /// Regression (2026-11-11): Desktop fetches its usage only while its window is in front, so after an hour in the
    /// background its snapshot is an hour old; the dashboard listed it, the 額度 card dropped it.
    /// </summary>
    [Theory]
    [InlineData(46)]
    [InlineData(66)]
    [InlineData(3 * 60)]
    [InlineData(23 * 60)]
    public void DesktopLeftInTheBackgroundStaysOnTheCard(long minutes) =>
        Assert.True(QuotaService.DesktopOnCard(true, Desktop(minutes), Now));

    [Fact]
    public void AStaleOrQuitDesktopLeavesTheCard()
    {
        Assert.False(QuotaService.DesktopOnCard(true, Desktop(25 * 60), Now));
        Assert.False(QuotaService.DesktopOnCard(false, Desktop(66), Now));
        Assert.False(QuotaService.DesktopOnCard(true, null, Now));
    }
}
