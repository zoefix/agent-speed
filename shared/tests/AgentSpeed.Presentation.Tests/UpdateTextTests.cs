using System.Globalization;
using AgentSpeed.Presentation.Localization;
using AgentSpeed.Presentation.Tests.TestSupport;
using AgentSpeed.Presentation.Updates;

namespace AgentSpeed.Presentation.Tests;

public class UpdateTextTests
{
    private static readonly StringTable Table = Localizer.LoadEmbeddedTable();

    private static readonly DateTimeOffset Now = new(2026, 10, 21, 15, 0, 0, TimeSpan.Zero);

    private static readonly ReleaseInfo Release = TestReleases.Release("v1.2.0");

    private static Localizer English() => new(Table, LanguageCodes.En);

    [Theory]
    [InlineData(UpdateFailure.Network, "update.failed.network")]
    [InlineData(UpdateFailure.RateLimited, "update.failed.network")]
    [InlineData(UpdateFailure.Checksum, "update.failed.checksum")]
    [InlineData(UpdateFailure.Permission, "update.failed.permission")]
    [InlineData(UpdateFailure.Invalid, "update.failed.invalid")]
    [InlineData((UpdateFailure)99, "update.failed.network")]
    public void FailureKeyMapsEachReason(UpdateFailure failure, string key)
    {
        Assert.Equal(key, UpdateText.FailureKey(failure));
        Assert.NotEqual(key, English()[key]);
    }

    [Theory]
    [InlineData(UpdateFailure.Network, "Couldn't reach GitHub")]
    [InlineData(UpdateFailure.RateLimited, "Couldn't reach GitHub")]
    [InlineData(UpdateFailure.Checksum, "The download didn't pass verification, so nothing was changed")]
    [InlineData(UpdateFailure.Permission, "No permission to write to the install location")]
    [InlineData(UpdateFailure.Invalid, "The downloaded package is not valid")]
    public void FailureReasonsInEnglish(UpdateFailure failure, string expected)
    {
        Assert.Equal(expected, UpdateText.FailureReason(failure, English()));
        Assert.Equal("Update failed: " + expected, UpdateText.InstallFailed(failure, English()));
    }

    public static TheoryData<UpdateStatus, string> StatusLines => new()
    {
        { UpdateStatus.Idle, string.Empty },
        { new UpdateStatus(UpdatePhase.Checking), "Checking for updates…" },
        { new UpdateStatus(UpdatePhase.UpToDate), "You're up to date" },
        { new UpdateStatus(UpdatePhase.Available, Release), "Agent Speed 1.2.0 is available" },
        { new UpdateStatus(UpdatePhase.Available), string.Empty },
        { new UpdateStatus(UpdatePhase.Downloading, Release, 0.42), "Downloading… 42%" },
        { new UpdateStatus(UpdatePhase.Downloading, Release), "Downloading… 0%" },
        { new UpdateStatus(UpdatePhase.Verifying, Release), "Verifying…" },
        { new UpdateStatus(UpdatePhase.Installing, Release), "Installing…" },
        { new UpdateStatus(UpdatePhase.Relaunching, Release), "Installing…" },
        { new UpdateStatus(UpdatePhase.CheckFailed, Failure: UpdateFailure.Network), "Couldn't reach GitHub" },
        { new UpdateStatus(UpdatePhase.CheckFailed, Failure: UpdateFailure.RateLimited), "Couldn't reach GitHub" },
        { new UpdateStatus(UpdatePhase.CheckFailed, Failure: UpdateFailure.Invalid), "The downloaded package is not valid" },
        { new UpdateStatus(UpdatePhase.CheckFailed), "Couldn't reach GitHub" },
        {
            new UpdateStatus(UpdatePhase.InstallFailed, Release, Failure: UpdateFailure.Checksum),
            "Update failed: The download didn't pass verification, so nothing was changed"
        },
        {
            new UpdateStatus(UpdatePhase.InstallFailed, Release, Failure: UpdateFailure.Permission),
            "Update failed: No permission to write to the install location"
        },
        { new UpdateStatus(UpdatePhase.InstallFailed, Release), "Update failed: The downloaded package is not valid" },
    };

    [Theory]
    [MemberData(nameof(StatusLines))]
    public void StatusLineInEnglish(UpdateStatus status, string expected)
    {
        Assert.Equal(expected, UpdateText.StatusLine(status, English()));
    }

    [Theory]
    [InlineData(LanguageCodes.En)]
    [InlineData(LanguageCodes.ZhHant)]
    [InlineData(LanguageCodes.ZhHans)]
    [InlineData(LanguageCodes.Ja)]
    public void StatusLineNeverShowsRawKeys(string language)
    {
        var localizer = new Localizer(Table, language);
        foreach (var phase in Enum.GetValues<UpdatePhase>())
        {
            foreach (var failure in Enum.GetValues<UpdateFailure>())
            {
                var line = UpdateText.StatusLine(new UpdateStatus(phase, Release, 0.5, failure), localizer);
                Assert.DoesNotContain("update.", line, StringComparison.Ordinal);
                Assert.Equal(phase == UpdatePhase.Idle, line.Length == 0);
            }
        }
    }

    [Fact]
    public void StatusLineRequiresStatus()
    {
        Assert.Throws<ArgumentNullException>(() => UpdateText.StatusLine(null!, English()));
    }

    [Theory]
    [InlineData(0, "0%")]
    [InlineData(0.004, "0%")]
    [InlineData(0.425, "42%")]
    [InlineData(0.29, "29%")]
    [InlineData(0.57, "57%")]
    [InlineData(0.999, "99%")]
    [InlineData(1, "100%")]
    [InlineData(1.5, "100%")]
    [InlineData(-0.2, "0%")]
    [InlineData(double.NaN, "0%")]
    [InlineData(double.NegativeInfinity, "0%")]
    public void PercentIsFlooredAndClamped(double progress, string expected)
    {
        Assert.Equal(expected, UpdateText.Percent(progress, CultureInfo.InvariantCulture));
        Assert.Equal(expected, UpdateText.Percent(progress, English().Culture));
    }

    [Fact]
    public void DownloadingShowsPercent()
    {
        Assert.Equal("Downloading… 42%", UpdateText.Downloading(0.429, English()));
    }

    [Fact]
    public void ReleaseTexts()
    {
        var localizer = English();
        Assert.Equal("Update to 1.2.0…", UpdateText.MenuItem(Release, localizer));
        Assert.Equal("Update 1.2.0", UpdateText.Pill(Release, localizer));
        Assert.Equal("Agent Speed 1.2.0 is available", UpdateText.AvailableTitle(Release, localizer));
        Assert.Equal("Current version 1.0.0", UpdateText.Current("1.0.0", localizer));
        Assert.Equal("Agent Speed 2.0.0-rc.1 is available", UpdateText.AvailableTitle(TestReleases.Release("v2.0.0-rc.1"), localizer));
    }

    [Fact]
    public void LastCheckedNeverIsEmpty()
    {
        Assert.Equal(string.Empty, UpdateText.LastChecked(null, Now, English(), TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(0, "Last checked just now")]
    [InlineData(20, "Last checked just now")]
    [InlineData(59, "Last checked just now")]
    [InlineData(60, "Last checked 1m ago")]
    [InlineData(300, "Last checked 5m ago")]
    [InlineData(359, "Last checked 5m ago")]
    [InlineData(3599, "Last checked 59m ago")]
    [InlineData(-30, "Last checked just now")]
    [InlineData(-7200, "Last checked just now")]
    public void LastCheckedRecently(int secondsAgo, string expected)
    {
        Assert.Equal(expected, UpdateText.LastChecked(Now.AddSeconds(-secondsAgo), Now, English(), TimeZoneInfo.Utc));
    }

    [Fact]
    public void LastCheckedEarlierTodayShowsTimeOnly()
    {
        var localizer = English();
        var last = new DateTimeOffset(2026, 10, 21, 9, 15, 0, TimeSpan.Zero);

        var text = UpdateText.LastChecked(last, Now, localizer, TimeZoneInfo.Utc);

        Assert.Equal("Last checked " + last.ToString("t", localizer.Culture), text);
        Assert.Contains("9:15", text, StringComparison.Ordinal);
        Assert.DoesNotContain(last.ToString("d", localizer.Culture), text, StringComparison.Ordinal);
        Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LastCheckedYesterdayShowsDateAndTime()
    {
        var localizer = English();
        var last = new DateTimeOffset(2026, 10, 20, 18, 45, 0, TimeSpan.Zero);

        var text = UpdateText.LastChecked(last, Now, localizer, TimeZoneInfo.Utc);

        Assert.Equal("Last checked " + last.ToString("g", localizer.Culture), text);
        Assert.Contains(last.ToString("d", localizer.Culture), text, StringComparison.Ordinal);
        Assert.Contains(last.ToString("t", localizer.Culture), text, StringComparison.Ordinal);
        Assert.Contains("2026", text, StringComparison.Ordinal);
    }

    [Fact]
    public void LastCheckedUsesTheGivenTimeZone()
    {
        var localizer = English();
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("Test+09", TimeSpan.FromHours(9), "Test+09", "Test+09");
        var now = new DateTimeOffset(2026, 10, 21, 1, 0, 0, TimeSpan.Zero);
        var last = new DateTimeOffset(2026, 10, 20, 16, 30, 0, TimeSpan.Zero);
        var localLast = TimeZoneInfo.ConvertTime(last, tokyo);

        var inTokyo = UpdateText.LastChecked(last, now, localizer, tokyo);
        var inUtc = UpdateText.LastChecked(last, now, localizer, TimeZoneInfo.Utc);

        Assert.Equal("Last checked " + localLast.ToString("t", localizer.Culture), inTokyo);
        Assert.Contains("1:30", inTokyo, StringComparison.Ordinal);
        Assert.Equal("Last checked " + last.ToString("g", localizer.Culture), inUtc);
        Assert.Contains("4:30", inUtc, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UpdatePhase.Idle, false, false, false)]
    [InlineData(UpdatePhase.Checking, false, true, false)]
    [InlineData(UpdatePhase.UpToDate, false, false, false)]
    [InlineData(UpdatePhase.Available, false, false, false)]
    [InlineData(UpdatePhase.Downloading, true, true, false)]
    [InlineData(UpdatePhase.Verifying, true, true, false)]
    [InlineData(UpdatePhase.Installing, true, true, false)]
    [InlineData(UpdatePhase.Relaunching, true, true, false)]
    [InlineData(UpdatePhase.CheckFailed, false, false, true)]
    [InlineData(UpdatePhase.InstallFailed, false, false, true)]
    public void StatusFlags(UpdatePhase phase, bool installing, bool busy, bool failed)
    {
        var status = new UpdateStatus(phase);
        Assert.Equal(installing, status.IsInstalling);
        Assert.Equal(busy, status.IsBusy);
        Assert.Equal(failed, status.IsFailed);
    }
}
