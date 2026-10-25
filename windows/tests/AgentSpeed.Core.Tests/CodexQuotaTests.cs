using System.Text.Json;
using AgentSpeed.Core.Quota;

namespace AgentSpeed.Core.Tests;

public sealed class CodexQuotaTests
{
    private static CodexQuotaSnapshot Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CodexQuotaSnapshot.Parse(document.RootElement, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void UsesCodexBucketRatherThanAnotherModelsLimit()
    {
        var snapshot = Parse("""{"rateLimits":{"limitId":"other","primary":{"usedPercent":99}},"rateLimitsByLimitId":{"codex":{"primary":{"usedPercent":54,"windowDurationMins":10080,"resetsAt":1793407923},"secondary":null},"other":{"primary":{"usedPercent":99}}}}""");
        Assert.Equal(46, snapshot.LimitingWindow.RemainingPercent);
        Assert.Equal(10080, snapshot.Primary!.DurationMinutes);
        Assert.Equal(1793407923, snapshot.Primary.ResetsAt!.Value.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(54, 46)]
    [InlineData(90.1, 9)]
    [InlineData(100, 0)]
    [InlineData(105, 0)]
    [InlineData(-1, 100)]
    public void ComputesRemainingConservatively(double used, int expected)
    {
        Assert.Equal(expected, new CodexQuotaWindow(used, 10080, null).RemainingPercent);
    }

    [Fact]
    public void ShowsTheMoreConstrainedOfTwoWindows()
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":5,"windowDurationMins":300},"secondary":{"usedPercent":80,"windowDurationMins":10080}}}""");
        Assert.Equal(20, snapshot.LimitingWindow.RemainingPercent);
        Assert.Equal(2, snapshot.Windows.Count());
    }

    [Fact]
    public void KeepsAuthoritativeCardCountAndOnlyAvailableCardDetails()
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":55}},"rateLimitResetCredits":{"availableCount":3,"credits":[{"status":"available","expiresAt":1794007900},{"status":"consumed","expiresAt":1794007800}]}}""");
        Assert.Equal(3, snapshot.AvailableResetCreditCount);
        Assert.Single(snapshot.ResetCredits);
        Assert.Equal(1794007900, snapshot.ResetCredits[0].ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void MissingCardDataRemainsUnknownRatherThanZero()
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":55}},"rateLimitResetCredits":null}""");
        Assert.Null(snapshot.AvailableResetCreditCount);
        Assert.Empty(snapshot.ResetCredits);
    }

    [Fact]
    public void CardsWithoutExpiryKeepAnUnknownExpiry()
    {
        var snapshot = Parse("""{"rateLimits":{"primary":{"usedPercent":55}},"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":null}]}}""");
        Assert.Equal(1, snapshot.AvailableResetCreditCount);
        Assert.Null(Assert.Single(snapshot.ResetCredits).ExpiresAt);
    }

    [Fact]
    public void ResponseMetadataKeepsThePreviouslyReadResetCards()
    {
        var previous = Parse("""{"rateLimits":{"primary":{"usedPercent":54}},"rateLimitResetCredits":{"availableCount":1,"credits":[{"status":"available","expiresAt":1794007900}]}}""");
        using var document = JsonDocument.Parse("""{"limit_id":"codex","primary":{"used_percent":55,"window_minutes":10080,"resets_at":1793407923},"secondary":null}""");
        var response = CodexQuotaSnapshot.FromTranscript(document.RootElement)!.WithResetCreditsFrom(previous);
        Assert.Equal(45, response.LimitingWindow.RemainingPercent);
        Assert.Equal(1, response.AvailableResetCreditCount);
        Assert.Equal(1794007900, Assert.Single(response.ResetCredits).ExpiresAt!.Value.ToUnixTimeSeconds());
    }

    [Fact]
    public void AnUnrelatedResponseQuotaBucketIsIgnored()
    {
        using var document = JsonDocument.Parse("""{"limit_id":"other","primary":{"used_percent":90}}""");
        Assert.Null(CodexQuotaSnapshot.FromTranscript(document.RootElement));
    }

    [Fact]
    public void KnownExpiryIsAppliedOnceWithoutDiscardingUnknownCards()
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new CodexQuotaSnapshot(new(55, 10080, now.AddDays(2)), null, now)
        {
            AvailableResetCreditCount = 3,
            ResetCredits = [new(now.AddSeconds(-1)), new(null)],
        };
        var expired = snapshot.WithoutExpiredResetCredits(now);
        Assert.Equal(2, expired.AvailableResetCreditCount);
        Assert.Single(expired.ResetCredits);
        Assert.Equal(2, expired.WithoutExpiredResetCredits(now).AvailableResetCreditCount);
        Assert.Null(expired.NextResetCreditExpiry(now));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"rateLimits\":{\"primary\":null,\"secondary\":null}}")]
    [InlineData("{\"rateLimits\":{\"primary\":{\"windowDurationMins\":300}}}")]
    [InlineData("{\"rateLimits\":{\"limitId\":\"other\",\"primary\":{\"usedPercent\":50}}}")]
    public void MissingQuotaIsNeverTreatedAsFullOrEmpty(string json)
    {
        Assert.Throws<InvalidDataException>(() => Parse(json));
    }
}
