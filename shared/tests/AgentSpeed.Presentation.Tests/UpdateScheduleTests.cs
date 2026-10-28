using AgentSpeed.Presentation.Tests.TestSupport;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class UpdateScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 12, 0, 0, TimeSpan.Zero);

    private static readonly SemanticVersion Current = TestReleases.Version("1.1.0");

    [Fact]
    public void IntervalsMatchSpecification()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), UpdateSchedule.LaunchDelay);
        Assert.Equal(TimeSpan.FromHours(6), UpdateSchedule.Interval);
        Assert.Equal(TimeSpan.FromMinutes(15), UpdateSchedule.MaxTimerStep);
        Assert.Equal(TimeSpan.FromSeconds(1), UpdateSchedule.MinTimerStep);
    }

    [Fact]
    public void FirstCheckWithoutPreviousCheckWaitsLaunchDelay()
    {
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstCheck(Now, null));
    }

    [Fact]
    public void FirstCheckAfterRecentCheckWaitsForTheInterval()
    {
        var last = Now.AddHours(-1);
        Assert.Equal(last.AddHours(6), UpdateSchedule.FirstCheck(Now, last));
        Assert.Equal(Now.AddHours(5), UpdateSchedule.FirstCheck(Now, last));
    }

    [Theory]
    [InlineData(7 * 3600, 30)]
    [InlineData(30 * 24 * 3600, 30)]
    [InlineData(6 * 3600, 30)]
    [InlineData((6 * 3600) - 10, 30)]
    [InlineData((6 * 3600) - 30, 30)]
    [InlineData((6 * 3600) - 31, 31)]
    [InlineData((6 * 3600) - 60, 60)]
    [InlineData(0, 6 * 3600)]
    public void FirstCheckIsNeverSoonerThanLaunchDelay(int secondsSinceLastCheck, int expectedSecondsFromNow)
    {
        var last = Now.AddSeconds(-secondsSinceLastCheck);
        Assert.Equal(Now.AddSeconds(expectedSecondsFromNow), UpdateSchedule.FirstCheck(Now, last));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    [InlineData(3 * 24 * 3600)]
    public void LastCheckInTheFutureIsIgnored(int secondsAhead)
    {
        Assert.Equal(Now.AddSeconds(30), UpdateSchedule.FirstCheck(Now, Now.AddSeconds(secondsAhead)));
    }

    [Fact]
    public void FirstCheckStaysWithinLaunchDelayAndInterval()
    {
        for (var minutes = -600; minutes <= 600; minutes += 7)
        {
            var due = UpdateSchedule.FirstCheck(Now, Now.AddMinutes(minutes));
            Assert.InRange(due, Now.AddSeconds(30), Now.AddHours(6));
        }
    }

    [Fact]
    public void FirstCheckComparesInstantsAcrossOffsets()
    {
        var last = new DateTimeOffset(2026, 10, 21, 20, 0, 0, TimeSpan.FromHours(9));
        Assert.Equal(Now.AddHours(5), UpdateSchedule.FirstCheck(Now, last));
    }

    [Fact]
    public void NextCheckIsOneIntervalAway()
    {
        Assert.Equal(Now.AddHours(6), UpdateSchedule.NextCheck(Now));
    }

    [Theory]
    [InlineData(-100, 1)]
    [InlineData(0, 1)]
    [InlineData(0.5, 1)]
    [InlineData(1, 1)]
    [InlineData(42.5, 42.5)]
    [InlineData(900, 900)]
    [InlineData(901, 900)]
    [InlineData(6 * 3600, 900)]
    public void TimerStepIsClamped(double secondsUntilDue, double expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), UpdateSchedule.TimerStep(Now, Now.AddSeconds(secondsUntilDue)));
    }

    [Fact]
    public void IsDueAtOrAfterTheDueTime()
    {
        Assert.False(UpdateSchedule.IsDue(Now, Now.AddTicks(1)));
        Assert.True(UpdateSchedule.IsDue(Now, Now));
        Assert.True(UpdateSchedule.IsDue(Now, Now.AddHours(-1)));
    }

    [Theory]
    [InlineData(true, "1.0.0", true)]
    [InlineData(true, "0.0.1", true)]
    [InlineData(true, "2.0.0-beta.1", true)]
    [InlineData(false, "1.0.0", false)]
    [InlineData(true, "0.0.0", false)]
    [InlineData(true, "0.0.0-dev", false)]
    [InlineData(false, "0.0.0", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "garbage", false)]
    public void AutomaticChecksNeedSettingAndReleaseBuild(bool setting, string? version, bool expected)
    {
        Assert.Equal(expected, UpdateSchedule.AutomaticChecksAllowed(setting, SemanticVersion.ParseOrNull(version)));
    }

    [Theory]
    [InlineData("1.2.0", "v1.2.0")]
    [InlineData("1.3.0", "v1.2.0")]
    [InlineData("2.0.0", "v1.9.9")]
    [InlineData("1.2.0", "v1.2.0-rc.1")]
    [InlineData("1.2.0", "1.2.0+build.7")]
    public void ReleaseThatIsNotNewerIsUpToDate(string current, string latestTag)
    {
        var release = TestReleases.Release(latestTag);
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(TestReleases.Version(current), release, null, manual: false));
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(TestReleases.Version(current), release, null, manual: true));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDraftOrPreReleaseIsUpToDate(bool manual)
    {
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(Current, null, null, manual));
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(Current, TestReleases.Release("v9.0.0", isDraft: true), null, manual));
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(Current, TestReleases.Release("v9.0.0", isPreRelease: true), null, manual));
    }

    [Theory]
    [InlineData("1.1.0", "v1.2.0")]
    [InlineData("1.2.0-beta.1", "v1.2.0")]
    [InlineData("0.0.0", "v0.0.1")]
    [InlineData("1.1.9", "1.2.0")]
    public void NewerReleaseIsAvailable(string current, string latestTag)
    {
        var release = TestReleases.Release(latestTag);
        Assert.Equal(UpdateOffer.Available, UpdateSchedule.Evaluate(TestReleases.Version(current), release, null, manual: false));
        Assert.Equal(UpdateOffer.Available, UpdateSchedule.Evaluate(TestReleases.Version(current), release, null, manual: true));
    }

    [Theory]
    [InlineData("v1.2.0")]
    [InlineData("1.2.0")]
    [InlineData("V1.2.0")]
    [InlineData(" v1.2.0 ")]
    [InlineData("v1.2.0+build.3")]
    public void SkippedVersionBlocksOnlyAutomaticChecks(string skipped)
    {
        var release = TestReleases.Release("v1.2.0");

        Assert.Equal(UpdateOffer.Skipped, UpdateSchedule.Evaluate(Current, release, skipped, manual: false));
        Assert.Equal(UpdateOffer.Available, UpdateSchedule.Evaluate(Current, release, skipped, manual: true));
        Assert.True(UpdateSchedule.IsSkipped(release, skipped));
    }

    [Theory]
    [InlineData("v1.1.0")]
    [InlineData("v1.2.1")]
    [InlineData("v1.2.0-rc.1")]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void OtherSkippedValuesDoNotBlock(string? skipped)
    {
        var release = TestReleases.Release("v1.2.0");

        Assert.Equal(UpdateOffer.Available, UpdateSchedule.Evaluate(Current, release, skipped, manual: false));
        Assert.False(UpdateSchedule.IsSkipped(release, skipped));
    }

    [Fact]
    public void SkippedReleaseThatIsNotNewerIsUpToDate()
    {
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(TestReleases.Version("1.2.0"), TestReleases.Release("v1.2.0"), "v1.2.0", manual: false));
    }

    [Fact]
    public void UnparseableStoredTagIsComparedByTagText()
    {
        var release = TestReleases.Release("v1.2.0") with { Tag = "release-2026-10" };

        Assert.True(UpdateSchedule.IsSameVersion(release, "release-2026-10"));
        Assert.True(UpdateSchedule.IsSameVersion(release, " RELEASE-2026-10 "));
        Assert.False(UpdateSchedule.IsSameVersion(release, "release-2026"));
        Assert.Equal(UpdateOffer.Skipped, UpdateSchedule.Evaluate(Current, release, "Release-2026-10", manual: false));
        Assert.True(UpdateSchedule.IsSameVersion(release, "v1.2.0"));
    }

    [Fact]
    public void IsSameVersionRequiresRelease()
    {
        Assert.Throws<ArgumentNullException>(() => UpdateSchedule.IsSameVersion(null!, "v1.2.0"));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("v1.1.0", true)]
    [InlineData("v1.2.1", true)]
    [InlineData("banana", true)]
    [InlineData("v1.2.0", false)]
    [InlineData("1.2.0", false)]
    [InlineData("1.2.0+meta", false)]
    public void NotifiesOncePerVersion(string? notified, bool expected)
    {
        var release = TestReleases.Release("v1.2.0");
        Assert.Equal(expected, UpdateSchedule.ShouldNotify(release, notified));
        Assert.Equal(!expected, UpdateSchedule.IsSameVersion(release, notified));
    }

    [Fact]
    public void PreReleaseTagIsNotOfferedEvenWithoutTheGitHubFlag()
    {
        var release = TestReleases.Release("v9.0.0-rc.1");
        Assert.False(release.IsOfferable);
        Assert.Equal(UpdateOffer.UpToDate, UpdateSchedule.Evaluate(Current, release, null, manual: true));
    }
}
