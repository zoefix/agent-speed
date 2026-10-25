using AgentSpeed.Core.Quota;
using AgentSpeed.Presentation.Quota;

namespace AgentSpeed.Presentation.Tests;

public sealed class CodexQuotaDisplayTests
{
    [Theory]
    [InlineData(100, CodexQuotaSeverity.Normal)]
    [InlineData(30, CodexQuotaSeverity.Normal)]
    [InlineData(29, CodexQuotaSeverity.Caution)]
    [InlineData(10, CodexQuotaSeverity.Caution)]
    [InlineData(9, CodexQuotaSeverity.Critical)]
    [InlineData(0, CodexQuotaSeverity.Critical)]
    public void WarningBoundariesMatchTheDisplayedPercentage(int remaining, CodexQuotaSeverity severity)
    {
        Assert.Equal(severity, CodexQuotaDisplay.SeverityOf(remaining));
    }

    [Fact]
    public void ExpiredSnapshotIsMarkedStaleRatherThanResetToFull()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(100, 10080, now.AddSeconds(-1)), null, now.AddSeconds(-10));
        var display = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Ready, now, "zh-Hans");
        Assert.Equal("0% · 待更新", display.Value);
        Assert.Equal(CodexQuotaSeverity.Neutral, display.Severity);
    }

    [Fact]
    public void RefreshFailureRetainsAnExplicitlyStaleLastReading()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(54, 10080, now.AddDays(2)), null, now.AddMinutes(-1));
        var display = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Unavailable, now, "zh-Hans");
        Assert.Equal("46% · 待更新", display.Value);
        Assert.Contains("周额度: 46%", display.Tooltip);
        Assert.Equal(CodexQuotaSeverity.Neutral, display.Severity);
    }

    [Fact]
    public void MissingQuotaDoesNotDisplayAMadeUpPercentage()
    {
        var display = CodexQuotaDisplay.Create(null, CodexQuotaStatus.Unavailable, DateTimeOffset.UtcNow, "zh-Hans");
        Assert.DoesNotContain("%", display.Value);
    }

    [Fact]
    public void IdleQuotaRemainsValidAndTooltipOmitsRefreshInstructions()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(55, 10080, now.AddDays(2)), null, now.AddHours(-1));
        var display = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Ready, now, "zh-Hans");
        Assert.Equal("45%", display.Value);
        Assert.Equal(CodexQuotaSeverity.Normal, display.Severity);
        Assert.DoesNotContain("60 秒", display.Tooltip);
        Assert.DoesNotContain("自动刷新", display.Tooltip);
    }

    [Fact]
    public void CardExpiryUsesTheSelectedTimeZoneWithAnExplicitOffset()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(55, 10080, now.AddDays(2)), null, now)
        {
            AvailableResetCreditCount = 1,
            ResetCredits = [new(new DateTimeOffset(2026, 11, 7, 1, 2, 3, TimeSpan.Zero))],
        };
        var zone = TimeZoneInfo.CreateCustomTimeZone("Test+08", TimeSpan.FromHours(8), "Test+08", "Test+08");
        var display = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Ready, now, "zh-Hans", zone);
        Assert.Contains("重置卡：1 张", display.Tooltip);
        Assert.Contains("到期：2026-11-07 09:02:03 +08:00", display.Tooltip);
        var utc = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Ready, now, "en", TimeZoneInfo.Utc);
        Assert.Contains("Expires: 2026-11-07 01:02:03 +00:00", utc.Tooltip);
        Assert.DoesNotContain("青绿", display.Tooltip);
    }

    [Fact]
    public void PartialCardDetailsDoNotInventExpiryForTheOtherCards()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(55, 10080, null), null, now)
        {
            AvailableResetCreditCount = 3,
            ResetCredits = [new(null)],
        };
        var tooltip = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Ready, now, "zh-Hans").Tooltip;
        Assert.Contains("重置卡：3 张", tooltip);
        Assert.Contains("第 1 张到期：未提供到期时间", tooltip);
        Assert.Contains("另 2 张：未提供到期时间", tooltip);
    }

    [Theory]
    [InlineData("en", "Codex left", "Reset cards: 1")]
    [InlineData("zh-Hans", "Codex 剩余", "重置卡：1 张")]
    [InlineData("zh-Hant", "Codex 剩餘", "重置卡：1 張")]
    [InlineData("ja", "Codex 残り", "リセットカード：1 枚")]
    public void CapsuleAndDetailsFollowTheApplicationsLanguage(string language, string prefix, string cards)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(20, 300, now.AddHours(4)), null, now)
        {
            AvailableResetCreditCount = 1,
            ResetCredits = [new(now.AddDays(1))],
        };
        var display = CodexQuotaDisplay.Create(snapshot, CodexQuotaStatus.Ready, now, language);
        Assert.Equal(prefix, display.Prefix);
        Assert.Contains(cards, display.Tooltip);
        Assert.DoesNotContain("win.quota.", display.Tooltip);
        Assert.DoesNotContain("%@", display.Tooltip);
    }

    [Fact]
    public void ExistingProfilesLeaveAccountReadsOffUntilTheUserOptsIn()
    {
        Assert.False(AgentSpeed.Presentation.Settings.AppSettings.Default.CodexQuotaEnabled);
        Assert.False(AgentSpeed.Presentation.Settings.SettingsSerializer.Deserialize("{\"version\":7}").CodexQuotaEnabled);
        var settings = AgentSpeed.Presentation.Settings.AppSettings.Default with { CodexQuotaEnabled = true };
        var json = AgentSpeed.Presentation.Settings.SettingsSerializer.Serialize(settings);
        Assert.True(AgentSpeed.Presentation.Settings.SettingsSerializer.Deserialize(json).CodexQuotaEnabled);
    }
}
