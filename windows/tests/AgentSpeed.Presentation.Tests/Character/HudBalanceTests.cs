using AgentSpeed.Presentation.Character;

namespace AgentSpeed.Presentation.Tests.Character;

/// <summary>
/// The column split between her two sides so both weigh about the same (DESIGN 2a.6, <i>Balance</i>), with the owner's
/// cards at a 148 pt orb (D = 296, t = 8): 性能 with 思考 and 首字, network, 費用, today and 額度 against the models card.
/// </summary>
public class HudBalanceTests
{
    private const double D = 296;
    private const double Step = 1 / 60.0;

    /// <summary>The 額度 card with two sources (three windows) and with three (five windows).</summary>
    private const double TwoSources = 53.7;
    private const double ThreeSources = 82.7;

    /// <summary>The models card with one to four rows.</summary>
    private static readonly double[] ModelRows = [0, 16.2, 25.9, 35.7, 45.4];

    /// <summary>Card sizes indexed by <see cref="HudCard"/>: the speed, the model chip, the column, the models card, 額度.</summary>
    private static (double W, double H)[] Sizes(double models, double quota) =>
        [(84, 46), (90, 18), (72, 38.5), (70, 26.4), (74, 18), (86, 26.4), (130, models), (124, quota)];

    private static bool[] Shown(double models) => [true, false, true, true, true, true, models > 0, true];

    /// <summary>A slim body from y 80 to 400 around x 222, optionally with an arm held out to x over y0…y1 (far out at her right by default).</summary>
    private static HudFrame Frame((double Y0, double Y1)? arm = null, double armX = 400)
    {
        var s = new HudSilhouette(0, 444, 4);
        for (var y = 80.0; y <= 400; y += 2)
        {
            s.Add(190, y);
            s.Add(254, y);
        }

        if (arm is { } span)
        {
            for (var y = span.Y0; y <= span.Y1; y += 2)
            {
                s.Add(armX, y);
            }
        }

        return new HudFrame((0, 0, 444, 444), s, D, 222, 80, 170, 140);
    }

    private sealed class Run(HudWings wings)
    {
        public HudWings Wings { get; } = wings;

        public double Time { get; private set; }

        /// <summary>When the split changed (the first frame's choice is not a change).</summary>
        public List<double> Changes { get; } = [];

        public void For(double seconds, HudFrame f, (double W, double H)[] sizes, bool[] shown)
        {
            for (var n = 0; n < Math.Round(seconds * 60); n++, Time += Step)
            {
                var before = Wings.Split;
                Wings.Update(Step, Time, f, sizes, shown);
                if (Time > 0 && Wings.Split != before)
                {
                    Changes.Add(Time);
                }
            }
        }
    }

    private static Run Settled(double models, double quota, bool still = false, HudFrame? f = null)
    {
        var run = new Run(new HudWings { Still = still, ModelsNarrowest = 60, QuotaNarrowest = 100 });
        run.For(2, f ?? Frame(), Sizes(models, quota), Shown(models));
        return run;
    }

    /// <summary>The cards moved to her right, in column order.</summary>
    private static HudCard[] Moved(HudWings wings) => [.. HudCards.Column.Where(wings.IsAtHerRight)];

    private static (double Left, double Top, double Right, double Bottom) Box(HudWings wings, HudCard card)
    {
        var p = wings.Places[(int)card];
        var (w, h) = wings.Sizes[(int)card];
        return (p.X - w / 2, p.Y - h / 2, p.X + w / 2, p.Y + h / 2);
    }

    [Theory]
    [InlineData(1, TwoSources, 2)]
    [InlineData(2, TwoSources, 2)]
    [InlineData(4, TwoSources, 1)]
    [InlineData(1, ThreeSources, 1)]
    [InlineData(3, ThreeSources, 1)]
    [InlineData(4, ThreeSources, 1)]
    [InlineData(0, TwoSources, 2)]
    public void TheOwnersCardsSplitSoBothSidesWeighAboutTheSame(int rows, double quota, int moved)
    {
        HudCard[] expected = moved == 1 ? [HudCard.Quota] : [HudCard.Agents, HudCard.Quota];
        var floating = Settled(ModelRows[rows], quota).Wings;
        Assert.Equal(expected, Moved(floating));
        Assert.Equal(HudCards.Column.IndexOf(expected[0]), floating.Split);

        // Under the models card (or the title chip), left edges on one line, in column order, clear of her and of each other.
        var wings = Settled(ModelRows[rows], quota, still: true).Wings;
        Assert.Equal(expected, Moved(wings));
        var under = rows > 0 ? Box(wings, HudCard.Models).Bottom : Box(wings, HudCard.Speed).Bottom;
        var left = Box(wings, expected[0]).Left;
        foreach (var card in expected)
        {
            var box = Box(wings, card);
            Assert.Equal(under + HudWings.ColumnGap * D, box.Top, 6);
            Assert.Equal(left, box.Left, 6);
            Assert.True(box.Left > 254 && box.Right <= 444, card.ToString());
            Assert.Equal(1, wings.Places[(int)card].Alpha, 6);
            under = box.Bottom;
        }

        foreach (var card in HudCards.Column.Except(expected))
        {
            Assert.False(wings.IsAtHerRight(card));
            Assert.True(Box(wings, card).Right < 190, card.ToString());
        }

        Assert.Equal(HudWings.ModelsPlace.UnderSpeed, wings.ModelsAt);
    }

    [Fact]
    public void AColumnNoHeavierThanTheRightStaysAtHerLeft()
    {
        // 費用 alone against two model rows.
        bool[] shown = [true, false, false, false, true, false, true, false];
        var run = new Run(new HudWings());
        run.For(2, Frame(), Sizes(ModelRows[2], 0), shown);
        Assert.Equal(HudCards.Column.Length, run.Wings.Split);
        Assert.Empty(Moved(run.Wings));
    }

    [Fact]
    public void ATieKeepsTheCardsAtHerLeft()
    {
        // Two cards of 20 against a models card of 20: |46.5 − 20| = |20 − 46.5|.
        bool[] shown = [true, false, false, false, true, true, true, false];
        (double W, double H)[] sizes = [(84, 46), (90, 18), (0, 0), (0, 0), (74, 20), (86, 20), (130, 20), (0, 0)];
        var run = new Run(new HudWings());
        run.For(2, Frame(), sizes, shown);
        Assert.Equal(HudCards.Column.Length, run.Wings.Split);
    }

    [Fact]
    public void ASplitThatDoesNotFitGivesWayToTheNextBest()
    {
        // Her arm far out over the rows 額度 takes when today moves too: only 額度 moves, above the arm.
        var arm = Frame(arm: (290, 330));
        var wings = Settled(ModelRows[1], TwoSources, f: arm).Wings;
        Assert.Equal([HudCard.Quota], Moved(wings));
        Assert.True(Box(wings, HudCard.Quota).Bottom < 290);
    }

    [Fact]
    public void NoSplitFittingLeavesItToTheSpillOver()
    {
        // No room at her right below the speed: every card stays at her left.
        var crowded = Frame(arm: (190, 420));
        var wings = Settled(ModelRows[1], TwoSources, f: crowded).Wings;
        Assert.Empty(Moved(wings));
    }

    [Fact]
    public void WithoutBalanceTheColumnStaysAtHerLeft()
    {
        var run = new Run(new HudWings { Balance = false, QuotaNarrowest = 100 });
        run.For(2, Frame(), Sizes(ModelRows[1], TwoSources), Shown(ModelRows[1]));
        Assert.Equal(HudCards.Column.Length, run.Wings.Split);
        Assert.Empty(Moved(run.Wings));
    }

    [Fact]
    public void SmallChangesKeepTheSplit()
    {
        // Three model rows weigh the two-source 額度 split almost evenly either way; a source's line coming and going
        // (less than 0.06 D) would tip it, but the split stays.
        var run = Settled(ModelRows[3], TwoSources);
        var split = run.Wings.Split;
        for (var i = 0; i < 20; i++)
        {
            run.For(0.5, Frame(), Sizes(ModelRows[3], TwoSources + (i % 2 == 0 ? 9.4 : -9.4)), Shown(ModelRows[3]));
            Assert.Equal(split, run.Wings.Split);
        }
    }

    [Fact]
    public void ASourceJoiningRebalancesAfterTheSplitHasHeld()
    {
        var run = Settled(ModelRows[1], TwoSources);
        Assert.Equal([HudCard.Agents, HudCard.Quota], Moved(run.Wings));
        var three = Sizes(ModelRows[1], ThreeSources);
        var shown = Shown(ModelRows[1]);
        run.For(0.5, Frame(), three, shown);
        Assert.Equal(HudCards.Column.IndexOf(HudCard.Agents), run.Wings.Split);
        run.For(0.2, Frame(), three, shown);
        Assert.Equal(HudCards.Column.IndexOf(HudCard.Quota), run.Wings.Split);

        // Today fades out at her right and back in at her left, its accent edge facing her on either side.
        Assert.True(run.Wings.IsAtHerRight(HudCard.Agents));
        Assert.True(run.Wings.Places[(int)HudCard.Agents].Alpha < 1);
        run.For(0.6, Frame(), three, shown);
        Assert.False(run.Wings.IsAtHerRight(HudCard.Agents));
        Assert.Equal(1, run.Wings.Places[(int)HudCard.Agents].Alpha, 6);
        Assert.True(Box(run.Wings, HudCard.Agents).Right < 190);
    }

    [Fact]
    public void CardsChangeSidesAtMostOnceInFourSecondsAndTwelveWhenRestless()
    {
        var run = Settled(ModelRows[1], TwoSources);
        var shown = Shown(ModelRows[1]);
        var two = Sizes(ModelRows[1], TwoSources);
        var three = Sizes(ModelRows[1], ThreeSources);
        var today = HudCards.Column.IndexOf(HudCard.Agents);
        var quota = HudCards.Column.IndexOf(HudCard.Quota);

        // 2 s: a third source; today moves back after the 0.6 s hold (2.6 s).
        run.For(0.8, Frame(), three, shown);
        Assert.Equal(quota, run.Wings.Split);

        // 2.8 s: it leaves again; today moves over 4 s after the last change (6.6 s), not sooner.
        run.For(3.6, Frame(), two, shown);
        Assert.Equal(quota, run.Wings.Split);
        run.For(0.4, Frame(), two, shown);
        Assert.Equal(today, run.Wings.Split);

        // 6.8 s: it joins once more; two changes 4 s apart, so the next waits 12 s (18.6 s).
        run.For(11.6, Frame(), three, shown);
        Assert.Equal(today, run.Wings.Split);
        run.For(0.4, Frame(), three, shown);
        Assert.Equal(quota, run.Wings.Split);
    }

    [Fact]
    public void SpillOverWaitsForTheChangeLimitToo()
    {
        // Her arm reaches out at her left right after the split changed: today has no room there, but the cards hug her
        // where they are until the split may change again.
        var arm = Frame(arm: (100, 420), armX: 100);
        var shown = Shown(ModelRows[2]);
        var two = Sizes(ModelRows[2], TwoSources);
        var three = Sizes(ModelRows[2], ThreeSources);
        var run = Settled(ModelRows[2], TwoSources);
        run.For(1, Frame(), three, shown);
        Assert.Single(run.Changes);
        run.For(2, arm, three, shown);
        Assert.Single(run.Changes);
        run.For(2, arm, three, shown);
        Assert.Equal(2, run.Changes.Count);
        Assert.True(run.Changes[1] - run.Changes[0] >= HudWings.ReturnAfter - 1e-9);
        Assert.True(run.Wings.IsAtHerRight(HudCard.Agents));

        // After two changes within 30 s, the spill-over waits 12 s.
        var burst = Settled(ModelRows[2], ThreeSources);
        burst.For(1, Frame(), two, shown);
        burst.For(4.5, Frame(), three, shown);
        Assert.Equal(2, burst.Changes.Count);
        burst.For(10, arm, three, shown);
        Assert.Equal(2, burst.Changes.Count);
        burst.For(2, arm, three, shown);
        Assert.Equal(3, burst.Changes.Count);
        Assert.True(burst.Changes[2] - burst.Changes[1] >= HudWings.SettledReturn - 1e-9);
        Assert.True(burst.Wings.IsAtHerRight(HudCard.Agents));
    }

    [Fact]
    public void MovedCardsShareTheModelsCardsLeftEdgeOrLieFurtherRight()
    {
        // The owner's cards: 額度 under the models card on its left edge.
        var wings = Settled(ModelRows[1], ThreeSources, still: true).Wings;
        Assert.Equal([HudCard.Quota], Moved(wings));
        Assert.Equal(Box(wings, HudCard.Models).Left, Box(wings, HudCard.Quota).Left, 6);

        // Her hand at her right beside 額度 only: 額度 steps right of her, the models card keeps its line.
        wings = Settled(ModelRows[1], ThreeSources, still: true, f: Frame(arm: (240, 300), armX: 300)).Wings;
        Assert.Equal([HudCard.Quota], Moved(wings));
        Assert.Equal(300 + HudWings.Gap * D, Box(wings, HudCard.Quota).Left, 6);
        Assert.True(Box(wings, HudCard.Models).Left < Box(wings, HudCard.Quota).Left - 1);

        // Her hand beside the models card only: 額度 follows it right instead of lying left of it.
        wings = Settled(ModelRows[1], ThreeSources, still: true, f: Frame(arm: (202, 212), armX: 290)).Wings;
        Assert.Equal([HudCard.Quota], Moved(wings));
        Assert.Equal(290 + HudWings.Gap * D, Box(wings, HudCard.Models).Left, 6);
        Assert.Equal(Box(wings, HudCard.Models).Left, Box(wings, HudCard.Quota).Left, 6);
    }

    [Fact]
    public void ACardComingOrGoingRebalances()
    {
        var run = Settled(ModelRows[4], TwoSources);
        Assert.Equal([HudCard.Quota], Moved(run.Wings));

        // The models card quiets down: today joins 額度 at her right to keep the balance.
        run.For(2, Frame(), Sizes(ModelRows[4], TwoSources), Shown(0));
        Assert.Equal([HudCard.Agents, HudCard.Quota], Moved(run.Wings));

        // 費用 quiets down too: still even enough.
        var shown = Shown(0);
        shown[(int)HudCard.Cost] = false;
        run.For(2, Frame(), Sizes(ModelRows[4], TwoSources), shown);
        Assert.Equal([HudCard.Agents, HudCard.Quota], Moved(run.Wings));

        // The four models come back: today goes back to her left.
        shown[(int)HudCard.Models] = true;
        run.For(4, Frame(), Sizes(ModelRows[4], TwoSources), shown);
        Assert.Equal([HudCard.Quota], Moved(run.Wings));
    }
}
