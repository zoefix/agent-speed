using AgentSpeed.Core;
using AgentSpeed.Presentation.Gauge;
using AgentSpeed.Presentation.Heat;
using AgentSpeed.Presentation.Orb;

namespace AgentSpeed.Presentation.Tests;

public class SpeedTitleTests
{
    private const double R = 400;

    public static TheoryData<SpeedTitle, double> ThresholdsAt400 => new()
    {
        { SpeedTitle.Walk, 0.5 },
        { SpeedTitle.Bicycle, 8 },
        { SpeedTitle.EBike, 12 },
        { SpeedTitle.Motorcycle, 16 },
        { SpeedTitle.Car, 24 },
        { SpeedTitle.SportsCar, 32 },
        { SpeedTitle.BulletTrain, 48 },
        { SpeedTitle.RaceCar, 68 },
        { SpeedTitle.Maglev, 96 },
        { SpeedTitle.Jetliner, 136 },
        { SpeedTitle.FighterJet, 200 },
        { SpeedTitle.Hypersonic, 280 },
        { SpeedTitle.Rocket, 400 },
    };

    private static void Hold(TitleTracker tracker, double value, double from, double to, double redline = R)
    {
        for (var t = from; t <= to + 1e-9; t += 0.05)
        {
            tracker.Update(value, redline, t);
        }
    }

    [Theory]
    [MemberData(nameof(ThresholdsAt400))]
    public void ThresholdsAtTheDefaultRedline(SpeedTitle title, double expected)
    {
        Assert.Equal(expected, SpeedTitles.Threshold(title, R), 9);
        Assert.Equal(title, SpeedTitles.Raw(expected, R));
        Assert.Equal(title - 1, SpeedTitles.Raw(expected * 0.999, R));
    }

    [Fact]
    public void LadderHasThirteenRungsInClimbingOrderWithTheirKeys()
    {
        Assert.Equal(13, SpeedTitles.Ladder.Count);
        Assert.Equal(Enum.GetValues<SpeedTitle>().Skip(1), SpeedTitles.Ladder.Select(s => s.Title));
        Assert.Equal("tier.idle", SpeedTitles.Key(SpeedTitle.Idle));
        Assert.Equal("title.walk", SpeedTitles.Key(SpeedTitle.Walk));
        Assert.Equal("title.sportsCar", SpeedTitles.Key(SpeedTitle.SportsCar));
        Assert.Equal("title.bulletTrain", SpeedTitles.Key(SpeedTitle.BulletTrain));
        Assert.Equal("title.fighterJet", SpeedTitles.Key(SpeedTitle.FighterJet));
        Assert.Equal("title.rocket", SpeedTitles.Key(SpeedTitle.Rocket));
        Assert.Equal(0, SpeedTitles.Threshold(SpeedTitle.Idle, R));
        Assert.Throws<ArgumentOutOfRangeException>(() => SpeedTitles.Key((SpeedTitle)99));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1000)]
    [InlineData(2000)]
    [InlineData(200_000)]
    public void ThresholdsScaleWithTheRedlineExceptWalk(double redline)
    {
        Assert.Equal(0.5, SpeedTitles.Threshold(SpeedTitle.Walk, redline));
        Assert.Equal(0.02 * redline, SpeedTitles.Threshold(SpeedTitle.Bicycle, redline), 9);
        Assert.Equal(0.12 * redline, SpeedTitles.Threshold(SpeedTitle.BulletTrain, redline), 9);
        Assert.Equal(redline, SpeedTitles.Threshold(SpeedTitle.Rocket, redline), 9);
        Assert.Equal(SpeedTitle.FighterJet, SpeedTitles.Raw(0.5 * redline, redline));
        for (var i = 1; i < SpeedTitles.Ladder.Count; i++)
        {
            Assert.True(SpeedTitles.Ladder[i].Threshold(redline) > SpeedTitles.Ladder[i - 1].Threshold(redline));
        }
    }

    [Theory]
    [InlineData(100)]
    [InlineData(400)]
    [InlineData(2000)]
    [InlineData(1_200_000)]
    public void SettingsTicksStayPutWhileTheRedlineMoves(double redline)
    {
        Assert.Equal(12, SpeedTitles.Scaled.Count);
        Assert.Equal(SpeedTitle.Bicycle, SpeedTitles.Scaled[0].Title);
        Assert.Equal(SpeedTitle.Rocket, SpeedTitles.Scaled[^1].Title);
        foreach (var step in SpeedTitles.Scaled)
        {
            Assert.Equal(Math.Pow(step.Fraction!.Value, 0.6), SpeedTitles.HeatAt(step.Title, redline), 9);
        }

        Assert.Equal(1, SpeedTitles.HeatAt(SpeedTitle.Rocket, redline), 9);
    }

    [Fact]
    public void RawTitleOfDegenerateValues()
    {
        Assert.Equal(SpeedTitle.Idle, SpeedTitles.Raw(0, R));
        Assert.Equal(SpeedTitle.Idle, SpeedTitles.Raw(0.49, R));
        Assert.Equal(SpeedTitle.Idle, SpeedTitles.Raw(double.NaN, R));
        Assert.Equal(SpeedTitle.Idle, SpeedTitles.Raw(double.PositiveInfinity, R));
        Assert.Equal(SpeedTitle.Rocket, SpeedTitles.Raw(10_000, R));
    }

    [Fact]
    public void TitleNeedsAnEightHundredMillisecondHold()
    {
        var tracker = new TitleTracker();
        Assert.False(tracker.Update(100, R, 0));
        Assert.Equal(SpeedTitle.Maglev, tracker.Candidate);
        Assert.Equal(SpeedTitle.Idle, tracker.Displayed);
        Assert.Equal(0.8, tracker.NextDeadline!.Value, 9);
        Assert.False(tracker.Update(100, R, 0.79));
        Assert.True(tracker.Update(100, R, 0.8));
        Assert.Equal(SpeedTitle.Maglev, tracker.Displayed);
        Assert.Null(tracker.NextDeadline);
    }

    [Fact]
    public void TitleIsEnteredAtItsThresholdAndLeftAtNinetyPercent()
    {
        var tracker = new TitleTracker();
        Hold(tracker, 48, 0, 2);
        Assert.Equal(SpeedTitle.BulletTrain, tracker.Displayed);

        // 90 % of 48 = 43.2: just above keeps the bullet train, just below falls to the sports car.
        Hold(tracker, 43.3, 2.05, 4);
        Assert.Equal(SpeedTitle.BulletTrain, tracker.Displayed);
        Hold(tracker, 43.1, 4.05, 6);
        Assert.Equal(SpeedTitle.SportsCar, tracker.Displayed);

        // Climbing back needs the full threshold again.
        Hold(tracker, 47.9, 6.05, 8);
        Assert.Equal(SpeedTitle.SportsCar, tracker.Displayed);
        Hold(tracker, 48, 8.05, 10);
        Assert.Equal(SpeedTitle.BulletTrain, tracker.Displayed);
    }

    [Fact]
    public void BriefDipsDoNotChangeTheTitle()
    {
        var tracker = new TitleTracker();
        Hold(tracker, 200, 0, 2);
        Assert.Equal(SpeedTitle.FighterJet, tracker.Displayed);

        Hold(tracker, 100, 2.05, 2.6);
        Hold(tracker, 200, 2.65, 4);
        Assert.Equal(SpeedTitle.FighterJet, tracker.Displayed);
    }

    [Fact]
    public void IdleNeedsThreeSecondsBelowWalking()
    {
        var tracker = new TitleTracker();
        Hold(tracker, 20, 0, 2);
        Assert.Equal(SpeedTitle.Motorcycle, tracker.Displayed);

        // Below walking speed the title first settles on walking (after the hold), then idles after 3 s.
        Assert.False(tracker.Update(0, R, 2.05));
        Assert.Equal(SpeedTitle.Walk, tracker.Candidate);
        Assert.Equal(2.05 + 0.8, tracker.NextDeadline!.Value, 9);
        Hold(tracker, 0, 2.1, 2.9);
        Assert.Equal(SpeedTitle.Walk, tracker.Displayed);
        Assert.Equal(2.05 + 3, tracker.NextDeadline!.Value, 9);
        Hold(tracker, 0, 2.95, 5.0);
        Assert.Equal(SpeedTitle.Walk, tracker.Displayed);
        Assert.True(tracker.Update(0, R, 5.05));
        Assert.Equal(SpeedTitle.Idle, tracker.Displayed);
    }

    [Fact]
    public void ResetJumpsWithoutAHold()
    {
        var tracker = new TitleTracker();
        tracker.Reset(SpeedTitle.Rocket);
        Assert.Equal(SpeedTitle.Rocket, tracker.Displayed);
        Assert.Equal(SpeedTitle.Rocket, tracker.Candidate);
        Assert.Null(tracker.NextDeadline);
    }

    [Fact]
    public void TitlesAndTiersMoveIndependently()
    {
        var presenter = new SpeedPresenter();
        presenter.Configure(SpeedMetric.Output, HeatMath.ReferenceRedline, 0);
        presenter.Update(SpeedSnapshot.Empty with { Live = new Rate(150, 150) }, 0);
        Assert.True(presenter.Tick(0.8));
        Assert.Equal(SpeedTier.Overdrive, presenter.Tier);
        Assert.Equal(SpeedTitle.Jetliner, presenter.Title);

        // 210 tok/s climbs a title (fighter jet) without leaving the overdrive effect tier.
        presenter.Update(SpeedSnapshot.Empty with { Live = new Rate(210, 210) }, 1);
        Assert.Equal(1.8, presenter.NextDeadline!.Value, 9);
        Assert.True(presenter.Tick(1.8));
        Assert.Equal(SpeedTier.Overdrive, presenter.Tier);
        Assert.Equal(SpeedTitle.FighterJet, presenter.Title);
    }

    [Fact]
    public void PresenterDeadlineIsTheEarlierOfTierAndTitle()
    {
        var presenter = new SpeedPresenter(new TierTracker(holdSeconds: 0.8), new TitleTracker(holdSeconds: 0.3));
        presenter.Configure(SpeedMetric.Output, HeatMath.ReferenceRedline, 0);
        presenter.Update(SpeedSnapshot.Empty with { Live = new Rate(100, 100) }, 0);
        Assert.Equal(0.3, presenter.NextDeadline!.Value, 9);
        Assert.True(presenter.Tick(0.3));
        Assert.Equal(SpeedTitle.Maglev, presenter.Title);
        Assert.Equal(SpeedTier.Idle, presenter.Tier);
        Assert.Equal(0.8, presenter.NextDeadline!.Value, 9);
    }

    [Fact]
    public void SwitchingTheMetricRescalesTheTitle()
    {
        var presenter = new SpeedPresenter();
        presenter.Configure(SpeedMetric.Output, HeatMath.ReferenceRedline, 0);
        var snapshot = SpeedSnapshot.Empty with { Live = new Rate(40, 120_000) };
        presenter.Update(snapshot, 0);
        presenter.Tick(1);
        Assert.Equal(SpeedTitle.SportsCar, presenter.Title);

        // Total throughput at its default 200k redline: 120k is 0.6 R, a fighter jet.
        presenter.Configure(SpeedMetric.Total, HeatMath.ReferenceRedlineTotal, 2);
        presenter.Tick(3);
        Assert.Equal(SpeedTitle.FighterJet, presenter.Title);
    }

    [Fact]
    public void LabelChordLeavesRoomInsideTheRing()
    {
        // The label baseline sits 0.239 S below the center for the orb's value layout.
        var width = OrbLabelFit.AvailableWidth(0.239);
        var bottom = 0.239 + OrbLabelFit.Size * OrbLabelFit.DescentRatio;
        var expected = 2 * (Math.Sqrt(GaugeLayout.InnerRadius * GaugeLayout.InnerRadius - bottom * bottom) - OrbLabelFit.RingMargin);
        Assert.Equal(expected, width, 9);
        Assert.InRange(width, 0.52, 0.54);
        Assert.Equal(0, OrbLabelFit.AvailableWidth(0.5));
        Assert.True(OrbLabelFit.AvailableWidth(0.1) > width);
    }

    [Theory]
    [InlineData(40, 50, 1)]
    [InlineData(50, 50, 1)]
    [InlineData(55, 50, 50.0 / 55)]
    [InlineData(62.5, 50, 0.8)]
    [InlineData(100, 50, 0.8)]
    [InlineData(0, 50, 1)]
    [InlineData(40, 0, 1)]
    [InlineData(double.NaN, 50, 1)]
    public void LabelShrinksToAtMostEightyPercentAndNeverTruncates(double natural, double available, double expected)
    {
        Assert.Equal(expected, OrbLabelFit.Scale(natural, available), 9);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(112)]
    public void LongestTitlesFitTheSmallestOrbsWithinTheShrinkLimit(double diameter)
    {
        // Advance widths per em of Segoe UI Variable Text Semibold (uppercase Latin) and CJK full-width glyphs.
        var available = OrbLabelFit.AvailableWidth(0.239) * diameter;
        var size = OrbLabelFit.Size * diameter;
        var tracking = 1.6 * diameter / 148;
        double Cjk(int glyphs) => glyphs * size;
        double Latin(double ems, int letters) => ems * size + (letters - 1) * tracking;

        foreach (var width in new[] { Cjk(5), Cjk(6), Latin(6.6, 12), Latin(5.8, 11), Latin(6.0, 10), Latin(6.5, 10) })
        {
            Assert.True(width * OrbLabelFit.MinimumScale <= available, $"{width:0.0} DIP does not fit {available:0.0} DIP at S={diameter}");
        }
    }
}
