using AgentSpeed.Presentation.Character;
using Xunit.Abstractions;

namespace AgentSpeed.Presentation.Tests.Character;

public class HudWingsTests(ITestOutputHelper output)
{
    private static readonly (double W, double H)[] Sizes = [(80, 40), (90, 18), (78, 34), (70, 30), (74, 16), (86, 16), (96, 36), (88, 40)];

    private static readonly bool[] AllShown = [true, true, true, true, true, true, false, true];

    private static readonly bool[] WithModels = [true, true, true, true, true, true, true, true];

    /// <summary>
    /// A frame like a 148 pt orb's (D = 296, window 444): her outline a slim body from y 80 to 400 around x 222, with an
    /// optional arm held out to one side at chest height.
    /// </summary>
    private static HudFrame Frame(double armOut = 0, double armSide = 1)
    {
        var s = new HudSilhouette(0, 444, 4);
        for (var y = 80.0; y <= 400; y += 2)
        {
            s.Add(190, y);
            s.Add(254, y);
        }

        if (armOut > 0)
        {
            for (var y = 150.0; y <= 190; y += 2)
            {
                s.Add(222 + armSide * armOut, y);
            }
        }

        return new HudFrame((0, 0, 444, 444), s, 296, 222, 80, 170, 140);
    }

    /// <summary>Her outline with wide hair at her left from her head to her knees: no room for the column there.</summary>
    private static HudFrame WideHairFrame()
    {
        var f = Frame();
        for (var y = 80.0; y <= 300; y += 2)
        {
            f.Silhouette.Add(60, y);
        }

        return f;
    }

    private static bool Overlaps(HudWings.Place p, (double W, double H) size, HudFrame f) =>
        f.Silhouette.Extent(p.Y - size.H / 2, p.Y + size.H / 2) is { } e && p.X + size.W / 2 > e.Left && p.X - size.W / 2 < e.Right;

    [Fact]
    public void CardsSitBesideHerNeverOverHer()
    {
        var f = Frame();
        var wings = new HudWings { Still = true, Balance = false };
        wings.Update(1 / 60.0, 0, f, Sizes, AllShown);
        foreach (var card in HudCards.Column.Prepend(HudCard.Speed))
        {
            var p = wings.Places[(int)card];
            Assert.False(Overlaps(p, Sizes[(int)card], f), card.ToString());
            Assert.True(p.X - Sizes[(int)card].W / 2 >= 0 && p.X + Sizes[(int)card].W / 2 <= 444);
        }

        Assert.True(wings.Places[(int)HudCard.Speed].X > 254);
        foreach (var card in HudCards.Column)
        {
            Assert.True(wings.Places[(int)card].X < 190);
        }

        var model = wings.Places[(int)HudCard.Model];
        Assert.True(Math.Abs(model.X - 222) < 1e-9 && model.Y + Sizes[1].H / 2 < 80);
        var ys = HudCards.Column.Select(c => wings.Places[(int)c].Y).ToArray();
        Assert.Equal(ys.Order(), ys);
    }

    [Fact]
    public void TheSpeedMovesOffAnArmHeldOutToItsSide()
    {
        var f = Frame(armOut: 150);
        var wings = new HudWings { Still = true };
        for (var n = 0; n < 240; n++)
        {
            wings.Update(1 / 60.0, n / 60.0, f, Sizes, AllShown);
        }

        Assert.False(Overlaps(wings.Places[(int)HudCard.Speed], Sizes[0], f));
    }

    [Fact]
    public void AHiddenCardFadesAndTheColumnClosesUp()
    {
        var f = Frame();
        var wings = new HudWings { Still = true, Balance = false };
        var shown = AllShown.ToArray();
        wings.Update(1 / 60.0, 0, f, Sizes, shown);
        var before = wings.Places[(int)HudCard.Cost].Y;
        shown[(int)HudCard.Network] = false;
        for (var n = 1; n <= 180; n++)
        {
            wings.Update(1 / 60.0, n / 60.0, f, Sizes, shown);
        }

        Assert.Equal(0, wings.Places[(int)HudCard.Network].Alpha);
        Assert.True(wings.Places[(int)HudCard.Cost].Y < before - 25);
    }

    [Fact]
    public void CardsFloatUnlessStill()
    {
        var f = Frame();
        var moving = new HudWings();
        var still = new HudWings { Still = true };
        var spread = 0.0;
        var tilt = 0.0;
        double? first = null;
        for (var n = 0; n < 600; n++)
        {
            var t = n / 60.0;
            moving.Update(1 / 60.0, t, f, Sizes, AllShown);
            still.Update(1 / 60.0, t, f, Sizes, AllShown);
            var p = moving.Places[(int)HudCard.Network];
            first ??= p.Y;
            spread = Math.Max(spread, Math.Abs(p.Y - first.Value));
            tilt = Math.Max(tilt, Math.Abs(p.Spin));
            Assert.Equal(0, still.Places[(int)HudCard.Network].Spin);
        }

        Assert.InRange(spread, 4.0001, 14.9999);
        Assert.InRange(tilt, 0.02001, 0.05999);
    }

    [Fact]
    public void TheColumnContinuesAtHerRightWhenHerLeftIsFull()
    {
        var f = WideHairFrame();
        var wings = new HudWings();
        for (var n = 0; n < 240; n++)
        {
            wings.Update(1 / 60.0, n / 60.0, f, Sizes, AllShown);
        }

        Assert.True(wings.Split < HudCards.Column.Length);
        var speed = wings.Places[(int)HudCard.Speed];
        foreach (var card in HudCards.Column)
        {
            var p = wings.Places[(int)card];
            Assert.True(p.Alpha > 0.9, card.ToString());
            var (w, h) = Sizes[(int)card];
            if (f.Silhouette.Extent(p.Y - h / 2 + 3, p.Y + h / 2 - 3) is { } e)
            {
                Assert.True(p.X + w / 2 < e.Left + 6 || p.X - w / 2 > e.Right - 6, $"{card} overlaps her");
                Assert.True(p.X - w / 2 >= 0 && p.X + w / 2 <= 444);
            }
        }

        var moved = HudCards.Column.Where(c => wings.Places[(int)c].X > 222).ToArray();
        Assert.NotEmpty(moved);
        Assert.All(moved, c => Assert.True(wings.Places[(int)c].Y > speed.Y));
    }

    [Fact]
    public void CardsComeBackOnlyAfterHerLeftStaysClear()
    {
        var wings = new HudWings { Still = false, Balance = false };
        var t = 0.0;
        for (var i = 0; i < 120; i++)
        {
            wings.Update(1 / 60.0, t, WideHairFrame(), Sizes, AllShown);
            t += 1 / 60.0;
        }

        var spilled = wings.Split;
        Assert.True(spilled < HudCards.Column.Length);
        for (var i = 0; i < 30; i++)
        {
            wings.Update(1 / 60.0, t, Frame(), Sizes, AllShown);
            t += 1 / 60.0;
        }

        Assert.Equal(spilled, wings.Split);
        for (var i = 0; i < 600; i++)
        {
            wings.Update(1 / 60.0, t, Frame(), Sizes, AllShown);
            t += 1 / 60.0;
        }

        Assert.Equal(HudCards.Column.Length, wings.Split);
        foreach (var card in HudCards.Column)
        {
            Assert.True(wings.Places[(int)card].X < 190);
        }
    }

    [Fact]
    public void APassingGestureMovesNoCard()
    {
        var wings = new HudWings { Balance = false };
        var t = 0.0;
        for (var n = 0; n < 300; n++)
        {
            var f = n % 120 >= 60 && n % 120 < 72 ? WideHairFrame() : Frame();
            wings.Update(1 / 60.0, t, f, Sizes, AllShown);
            t += 1 / 60.0;
            Assert.Equal(HudCards.Column.Length, wings.Split);
        }
    }

    [Fact]
    public void CardsNeverChangeSidesTwiceWithinFourSeconds()
    {
        // Wide hair at her left and an arm far out at her right take turns every 0.7 s: each side overflows in turn.
        var crowdedRight = Frame();
        for (var y = 150.0; y <= 400; y += 2)
        {
            crowdedRight.Silhouette.Add(400, y);
        }

        var wings = new HudWings { Balance = false };
        var changes = new List<double>();
        var split = -1;
        for (var n = 0; n < 1200; n++)
        {
            var time = n / 60.0;
            wings.Update(1 / 60.0, time, n / 42 % 2 == 0 ? WideHairFrame() : crowdedRight, Sizes, AllShown);
            if (split >= 0 && wings.Split != split)
            {
                changes.Add(time);
            }

            split = wings.Split;
        }

        Assert.True(changes.Count >= 3, $"{changes.Count} changes");
        for (var i = 1; i < changes.Count; i++)
        {
            Assert.True(changes[i] - changes[i - 1] >= HudWings.ReturnAfter - 1e-6, $"changes at {changes[i - 1]:0.00} s and {changes[i]:0.00} s");
        }
    }

    [Fact]
    public void FloatsDifferPerCard()
    {
        var a = HudWings.Float(HudCard.Performance, 3);
        var b = HudWings.Float(HudCard.Network, 3);
        Assert.True(a.Dx != b.Dx && a.Dy != b.Dy);
    }

    /// <summary>
    /// <c>AGENT_SPEED_CHARACTER_PACKS</c> (a folder of unpacked packs): every pack dances through the bands for 60 s at a
    /// 148 pt orb while the cards follow her outline; checks no card stays over her.
    /// </summary>
    [Fact]
    public void CardsStayBesideEveryCharacter()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_CHARACTER_PACKS") is not { Length: > 0 } root)
        {
            return;
        }

        const double d = 296.0;
        const double side = 444.0;
        (double W, double H)[] sizes = [(84, 62), (90, 18), (80, 28), (64, 27), (96, 18), (62, 27), (100, 40)];
        int[] levels = [2, 5, 8, 11, 13, 9, 4, 1];
        foreach (var dir in Directory.GetDirectories(root).Where(p => File.Exists(Path.Combine(p, "manifest.json"))).Order(StringComparer.Ordinal))
        {
            var performer = new CharacterPerformer(CharacterPack.Load(dir), seed: 11);
            var wings = new HudWings();
            var silhouette = new HudSilhouette(0, side, 2);
            var scratch = Array.Empty<double>();
            var ppp = 0.86 * d / performer.IdleHeight;
            var covered = 0;
            for (var n = 0; n < 3600; n++)
            {
                performer.Step(1.0 / 60, levels[Math.Min(levels.Length - 1, n / 450)]);
                var anchors = performer.Trace(silhouette, (222, 222 + 0.45 * d), ppp, ref scratch);
                var f = new HudFrame((0, 0, side, side), silhouette, d, anchors);
                wings.Update(1.0 / 60, n / 60.0, f, sizes, WithModels);
                foreach (var card in HudCards.Column.Append(HudCard.Speed).Append(HudCard.Models))
                {
                    var p = wings.Places[(int)card];
                    var (w, h) = wings.Sizes[(int)card];
                    if (!(p.Alpha > 0.5) || silhouette.Extent(p.Y - h / 2 + 4, p.Y + h / 2 - 4) is not { } e)
                    {
                        continue;
                    }

                    var enters = p.X < anchors.CenterX ? p.X + w / 2 - e.Left : e.Right - (p.X - w / 2);
                    if (enters > 6)
                    {
                        covered++;
                    }
                }
            }

            Assert.True(covered < 3600, $"{Path.GetFileName(dir)}: {covered} covered card-frames");
        }
    }

    /// <summary>
    /// <c>AGENT_SPEED_CHARACTER_PACKS</c>: every pack dances through the bands for 120 s at 148 and 120 pt with the owner's
    /// cards (性能 with three rows, network, 費用, today, 額度 with two sources narrowing to its room as the renderer does,
    /// the models card with two rows), with the balance and without: no card changes sides twice within 4 s, and the
    /// balance leaves cards over her hardly more often than the column alone (1 % of the card-frames).
    /// </summary>
    [Fact]
    public void TheBalanceHoldsThroughEveryDance()
    {
        if (Environment.GetEnvironmentVariable("AGENT_SPEED_CHARACTER_PACKS") is not { Length: > 0 } root)
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(root).Where(p => File.Exists(Path.Combine(p, "manifest.json"))).Order(StringComparer.Ordinal))
        {
            foreach (var orb in new[] { 148.0, 120.0 })
            {
                var alone = Dance(dir, orb, balance: false);
                var balanced = Dance(dir, orb, balance: true);
                output.WriteLine($"{Path.GetFileName(dir)} {orb} pt: side changes {alone.Changes} → {balanced.Changes}, "
                    + $"covered card-frames {alone.Covered} → {balanced.Covered}, split {balanced.Split}");
                Assert.True(balanced.Covered <= alone.Covered + DanceFrames * HudCards.Column.Length / 100,
                    $"{Path.GetFileName(dir)} {orb} pt: {balanced.Covered} covered card-frames, {alone.Covered} without the balance");
            }
        }
    }

    private const int DanceFrames = 7200;

    private static (int Changes, int Covered, int Split) Dance(string dir, double orb, bool balance)
    {
        int[] levels = [2, 5, 8, 11, 13, 9, 4, 1];
        var d = 2 * orb;
        var side = 1.5 * d;
        var t = Math.Max(8, 0.027 * d);
        (double W, double H)[] sizes =
        [
            (0.3 * d, 0.16 * d), (90, 18), (9 * t, 4.8 * t), (8.8 * t, 3.3 * t), (9.2 * t, 2.25 * t), (10.8 * t, 3.3 * t),
            (16 * t, 3.24 * t), (15.5 * t, 6.7 * t),
        ];
        var performer = new CharacterPerformer(CharacterPack.Load(dir), seed: 11);
        var wings = new HudWings { ModelsNarrowest = 7 * t, QuotaNarrowest = 12 * t, Balance = balance };
        var silhouette = new HudSilhouette(0, side, Math.Max(2, Math.Round(d / 150)));
        var scratch = Array.Empty<double>();
        var ppp = 0.86 * d / performer.IdleHeight;
        var last = new double[HudCards.Count];
        Array.Fill(last, double.NegativeInfinity);
        var sides = new bool[HudCards.Count];
        var changes = 0;
        var covered = 0;
        for (var n = 0; n < DanceFrames; n++)
        {
            var time = n / 60.0;
            performer.Step(1.0 / 60, levels[n / 900 % levels.Length]);
            var anchors = performer.Trace(silhouette, (side / 2, side / 2 + 0.45 * d), ppp, ref scratch);
            sizes[(int)HudCard.Quota].W = Math.Min(15.5 * t, Math.Max(12 * t, wings.QuotaRoom));
            wings.Update(1.0 / 60, time, new HudFrame((0, 0, side, side), silhouette, d, anchors), sizes, WithModels);
            foreach (var card in HudCards.Column)
            {
                var i = (int)card;
                var right = wings.IsAtHerRight(card);
                if (n > 0 && right != sides[i])
                {
                    changes++;
                    Assert.True(time - last[i] >= HudWings.ReturnAfter - 1e-9,
                        $"{Path.GetFileName(dir)} {orb} pt, balance {balance}: {card} changed sides at {last[i]:0.00} s and again at {time:0.00} s");
                    last[i] = time;
                }

                sides[i] = right;
                var p = wings.Places[i];
                var (w, h) = wings.Sizes[i];
                if (p.Alpha > 0.5 && silhouette.Extent(p.Y - h / 2 + 4, p.Y + h / 2 - 4) is { } e
                    && (right ? e.Right - (p.X - w / 2) : p.X + w / 2 - e.Left) > 6)
                {
                    covered++;
                }
            }
        }

        return (changes, covered, wings.Split);
    }
}

public class HudTraceTests
{
    [Fact]
    public void TraceMarksEveryBandOfHerDrawnParts()
    {
        // The fixture rig: a 10 px square (y 20–30) over a 20 px face (y 40–60), feet at y 90, drawn at 2 pt per px.
        var performer = new CharacterPerformer(CharacterPack.Load(TinyPack.Make(TinyPack.Bands(("idle", TinyPack.RigJson())))));
        performer.Step(1.0 / 60, 0);
        var silhouette = new HudSilhouette(0, 800, 4);
        var scratch = Array.Empty<double>();
        var anchors = performer.Trace(silhouette, (300, 600), 2, ref scratch);
        bool Marked(double y0, double y1)
        {
            for (var y = y0; y < y1; y += 4)
            {
                if (silhouette.Extent(y, y + 1) is null)
                {
                    return false;
                }
            }

            return true;
        }

        Assert.True(Marked(464, 476) && Marked(504, 536));
        Assert.Null(silhouette.Extent(486, 494));
        var active = performer.ActiveRig;
        var k = 2 * performer.ActivePlacement.Scale;
        var skinned = Array.Empty<double>();
        for (var part = 0; part < active.Parts.Length; part++)
        {
            active.Skin(part, performer.World, ref skinned);
            for (var v = 0; v < skinned.Length; v += 2)
            {
                var x = 300 + (skinned[v] - performer.ActivePlacement.FeetX) * k;
                var y = 600 + (skinned[v + 1] - performer.ActivePlacement.FeetY) * k;
                var e = Assert.NotNull(silhouette.Extent(y, y));
                Assert.True(e.Left <= x + 1e-9 && x <= e.Right + 1e-9);
            }
        }

        Assert.True(Math.Abs(anchors.HeadTop - 500) < 6);
        Assert.True(anchors.Shoulders < anchors.Chest && anchors.Chest < 600);
        var warm = scratch.Length;
        _ = performer.Trace(silhouette, (300, 600), 2, ref scratch);
        Assert.Equal(warm, scratch.Length);
    }
}
