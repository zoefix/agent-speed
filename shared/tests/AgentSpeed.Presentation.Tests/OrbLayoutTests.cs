using AgentSpeed.Core;
using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Settings.Orbs;

namespace AgentSpeed.Presentation.Tests;

public class OrbLayoutTests
{
    private const double R = 74;
    private const double Reach = R + 12 + R;
    private static readonly RectD Work = new(0, 0, 1920, 1040);
    private static readonly OrbDisplay A = new("A|1920x1080", Work, 1, true);
    private static readonly OrbDisplay B = new("B|2560x1440", new RectD(1920, 0, 2560, 1400), 1.5, false);

    private static OrbDisc Disc(double x, double y, double radius = R) => new(new PointD(x, y), radius);

    private static void AssertNear(PointD expected, PointD actual, double precision = 1e-6)
    {
        Assert.Equal(expected.X, actual.X, precision);
        Assert.Equal(expected.Y, actual.Y, precision);
    }

    private static void AssertPlaced(PointD center, double radius, IReadOnlyList<OrbDisc> others, RectD work, double scale = 1)
    {
        Assert.True(OrbGeometry.Fits(center, radius, work, scale), $"{center} is not inside {work}");
        Assert.True(OrbGeometry.IsFree(center, radius, others, scale), $"{center} overlaps another orb");
    }

    private static OrbsDocument Doc(params OrbDefinition[] orbs) => new(orbs);

    private static OrbsDocument At(OrbsDocument document, string arrangement, string id, OrbDisplay display, OrbAxis x, OrbAxis y) =>
        document.WithPosition(arrangement, id, new OrbPositionRecord(display.Key, x, y));

    // Position records

    [Fact]
    public void CentersNearAnEdgeAreDockedOthersAreFractions()
    {
        var record = OrbPositionRecord.FromCenter(A.Key, new PointD(1920 - 12 - R, 520), R, Work, 1);
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.High(12), OrbAxis.Fraction(0.5)), record);

        var corner = OrbPositionRecord.FromCenter(A.Key, new PointD(12 + R, 12 + R), R, Work, 1);
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.Low(12), OrbAxis.Low(12)), corner);

        Assert.Equal(OrbAxis.High(12.4), OrbPositionRecord.FromCenter(A.Key, new PointD(1920 - 12.4 - R, 520), R, Work, 1).X);
        Assert.Equal(OrbAxisAnchor.Fraction, OrbPositionRecord.FromCenter(A.Key, new PointD(1920 - 12.6 - R, 520), R, Work, 1).X.Anchor);
        Assert.Equal(OrbAxis.Fraction(0.65), OrbPositionRecord.FromCenter(A.Key, new PointD(1248, 520), R, Work, 1).X);
    }

    [Fact]
    public void DockedDistancesAreStoredInPoints()
    {
        var work = B.Work;
        var radius = R * 1.5;
        var center = new PointD(work.X + 18 + radius, work.Bottom - 18 - radius);
        var record = OrbPositionRecord.FromCenter(B.Key, center, radius, work, 1.5);
        Assert.Equal(new OrbPositionRecord(B.Key, OrbAxis.Low(12), OrbAxis.High(12)), record);
        AssertNear(center, record.ToCenter(work, 1.5, radius));
        AssertNear(new PointD(work.X + 24 + 98 * 2, work.Bottom - 24 - 98 * 2), record.ToCenter(work, 2, 98 * 2));
    }

    [Fact]
    public void RecordsResolveLikeTodaysClampedFractions()
    {
        var record = new OrbPositionRecord(A.Key, OrbAxis.Fraction(0.98765), OrbAxis.Fraction(0.01234));
        var expected = PlacementMath.ClampDisc(PlacementMath.FromFraction(0.98765, 0.01234, Work), R, Work, 12);
        Assert.Equal(expected, record.ToCenter(Work, 1, R));
        Assert.Equal(new PointD(960, 520), new OrbPositionRecord(A.Key, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5)).ToCenter(Work, 1, R));
        Assert.Equal(new PointD(1920 - 12 - R, 12 + R), new OrbPositionRecord(A.Key, OrbAxis.High(0), OrbAxis.Low(0)).ToCenter(Work, 1, R));
    }

    [Fact]
    public void ADockedOrbKeepsItsEdgeDistanceWhenItsSizeChanges()
    {
        var center = new PointD(1920 - 12 - R, 0.3 * 1040);
        var record = OrbPositionRecord.FromCenter(A.Key, center, R, Work, 1);
        var bigger = record.ToCenter(Work, 1, 98);
        Assert.Equal(new PointD(1920 - 12 - 98, 0.3 * 1040), bigger);
        Assert.Equal(new PointD(1920 - 12 - 98, 0.3 * 1040), OrbGeometry.Resized(center, R, 98, Work, 1));

        var corner = new PointD(12 + R, 1040 - 12 - R);
        Assert.Equal(new PointD(12 + 32, 1040 - 12 - 32), OrbGeometry.Resized(corner, R, 32, Work, 1));
        Assert.Equal(new PointD(500, 400), OrbGeometry.Resized(new PointD(500, 400), R, 120, Work, 1));
        Assert.Equal(new PointD(1920 - 12 - 120, 400), OrbGeometry.Resized(new PointD(1900, 400), R, 120, Work, 1));
    }

    [Fact]
    public void AResizedOrbSpringsAroundItsDockedEdgesAfterResolve()
    {
        var doc = OrbArrangement.Commit(Doc(new OrbDefinition("x")), [A], "x", new PointD(1920 - 12 - R, 1040 - 12 - R));
        var bigger = OrbEditing.SetSize(doc, "x", 240);
        Assert.Equal(new PointD(1920 - 12 - 120, 1040 - 12 - 120), OrbArrangement.Resolve(bigger, [A]).Locations["x"].Center);

        var floating = OrbArrangement.Commit(Doc(new OrbDefinition("x")), [A], "x", new PointD(960, 520));
        Assert.Equal(new PointD(960, 520), OrbArrangement.Resolve(OrbEditing.SetSize(floating, "x", 240), [A]).Locations["x"].Center);
    }

    [Fact]
    public void NonFiniteCentersBecomeTheMiddle()
    {
        var record = OrbPositionRecord.FromCenter(A.Key, new PointD(double.NaN, double.PositiveInfinity), R, Work, double.NaN);
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5)), record);
    }

    [Fact]
    public void RecordValidationRejectsUnusableKeys()
    {
        Assert.Null(new OrbPositionRecord("", OrbAxis.Low(1), OrbAxis.Low(1)).Validated());
        Assert.Null(new OrbPositionRecord("a\u0001", OrbAxis.Low(1), OrbAxis.Low(1)).Validated());
        Assert.Null(new OrbPositionRecord("a", OrbAxis.Low(double.NaN), OrbAxis.Low(1)).Validated());
        Assert.Null(new OrbPositionRecord("a", new OrbAxis((OrbAxisAnchor)9, 1), OrbAxis.Low(1)).Validated());
        Assert.Equal(new OrbPositionRecord("a", OrbAxis.Low(24), OrbAxis.Fraction(1)), new OrbPositionRecord("a", OrbAxis.Low(99), OrbAxis.Fraction(4)).Validated());
    }

    // Arrangements

    [Fact]
    public void TheCurrentArrangementsLayoutWins()
    {
        var doc = Doc(new OrbDefinition("x"));
        doc = At(doc, "A|1920x1080", "x", A, OrbAxis.Fraction(0.25), OrbAxis.Fraction(0.25));
        doc = At(doc, "A|1920x1080+B|2560x1440", "x", B, OrbAxis.Low(12), OrbAxis.Low(12));

        var result = OrbArrangement.Resolve(doc, [A, B]);
        Assert.Equal("A|1920x1080+B|2560x1440", result.Arrangement);
        Assert.Equal(new OrbLocation(B.Key, new PointD(1920 + 18 + 111, 18 + 111), 111), result.Locations["x"]);

        var single = OrbArrangement.Resolve(doc, [A]);
        Assert.Equal(new PointD(480, 260), single.Locations["x"].Center);
        Assert.Equal("A|1920x1080", single.Document.Layouts[0].Arrangement);
    }

    [Fact]
    public void ANewArrangementKeepsRecordsOfStillConnectedDisplays()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y"));
        doc = At(doc, "B|2560x1440", "y", B, OrbAxis.High(12), OrbAxis.Fraction(0.5));
        doc = At(doc, "A|1920x1080", "x", A, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5));

        var result = OrbArrangement.Resolve(doc, [A, B]);
        Assert.Equal(new PointD(960, 520), result.Locations["x"].Center);
        Assert.Equal(new PointD(1920 + 2560 - 18 - 111, 700), result.Locations["y"].Center);
        Assert.Equal(B.Key, result.Locations["y"].Display);

        var layout = result.Document.Layouts[0];
        Assert.Equal("A|1920x1080+B|2560x1440", layout.Arrangement);
        Assert.Equal(new OrbPositionRecord(B.Key, OrbAxis.High(12), OrbAxis.Fraction(0.5)), layout.Positions["y"]);
        Assert.Equal(3, result.Document.Layouts.Length);
    }

    [Fact]
    public void TheMostRecentArrangementWithAConnectedDisplayIsUsed()
    {
        var doc = Doc(new OrbDefinition("x"));
        doc = At(doc, "A|1920x1080+C", "x", A, OrbAxis.Fraction(0.1), OrbAxis.Fraction(0.1));
        doc = At(doc, "A|1920x1080+D", "x", A, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5));
        doc = At(doc, "E", "x", new OrbDisplay("E", Work, 1, true), OrbAxis.Fraction(0.9), OrbAxis.Fraction(0.9));

        Assert.Equal(new PointD(960, 520), OrbArrangement.Resolve(doc, [A, B]).Locations["x"].Center);
        Assert.Equal(new PointD(192, 104), OrbArrangement.Resolve(doc.Touch("A|1920x1080+C"), [A, B]).Locations["x"].Center);
    }

    [Fact]
    public void OrbsOfDisconnectedDisplaysMoveToTheMainDisplayKeepingTheirAnchors()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y") { Size = 196 });
        doc = At(doc, "A|1920x1080+B|2560x1440", "x", B, OrbAxis.High(12), OrbAxis.Low(12));
        doc = At(doc, "A|1920x1080+B|2560x1440", "y", B, OrbAxis.Fraction(0.5), OrbAxis.High(12));

        var result = OrbArrangement.Resolve(doc, [A]);
        Assert.Equal(new OrbLocation(A.Key, new PointD(1920 - 12 - R, 12 + R), R), result.Locations["x"]);
        Assert.Equal(new OrbLocation(A.Key, new PointD(960, 1040 - 12 - 98), 98), result.Locations["y"]);
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.High(12), OrbAxis.Low(12)), result.Document.RecordFor("A|1920x1080", "x"));
        Assert.NotNull(result.Document.LayoutFor("A|1920x1080+B|2560x1440"));

        var mainIsB = OrbArrangement.Resolve(doc, [A with { IsMain = false }, B with { IsMain = true }, new OrbDisplay("C", Work, 1, false)]);
        Assert.Equal(B.Key, mainIsB.Locations["x"].Display);
    }

    [Fact]
    public void OrbsWithoutARecordUseTheDefaultPosition()
    {
        var result = OrbArrangement.Resolve(Doc(new OrbDefinition("x")), [B, A]);
        Assert.Equal(new OrbLocation(A.Key, PlacementMath.DefaultOrbCenter(Work, R, 24), R), result.Locations["x"]);
        Assert.Equal(new PointD(1822, 918), result.Locations["x"].Center);
        Assert.Empty(result.Document.Layouts);

        var noMain = OrbArrangement.Resolve(Doc(new OrbDefinition("x")), [B with { IsMain = false }, A with { IsMain = false }]);
        Assert.Equal(B.Key, noMain.Locations["x"].Display);
        Assert.Empty(OrbArrangement.Resolve(Doc(new OrbDefinition("x")), []).Locations);
    }

    [Fact]
    public void OverlapsAreResolvedInListOrder()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y"), new OrbDefinition("z"));
        foreach (var id in new[] { "x", "y", "z" })
        {
            doc = At(doc, "A|1920x1080", id, A, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5));
        }

        var result = OrbArrangement.Resolve(doc, [A]);
        Assert.Equal(new PointD(960, 520), result.Locations["x"].Center);
        AssertNear(new PointD(960 + Reach + 0.01, 520), result.Locations["y"].Center);
        var z = result.Locations["z"].Center;
        AssertPlaced(z, R, [Disc(960, 520), new OrbDisc(result.Locations["y"].Center, R)], Work);
        Assert.Equal(Reach + 0.01, OrbGeometry.Distance(z, new PointD(960, 520)), 6);

        Assert.Equal(OrbPositionRecord.FromCenter(A.Key, result.Locations["y"].Center, R, Work, 1), result.Document.RecordFor("A|1920x1080", "y"));
        var again = OrbArrangement.Resolve(result.Document, [A]);
        Assert.Equal(result.Document, again.Document);
        foreach (var id in new[] { "x", "y", "z" })
        {
            AssertNear(result.Locations[id].Center, again.Locations[id].Center, 0.05);
        }
    }

    [Fact]
    public void HiddenOrbsNeverPushVisibleOnes()
    {
        var doc = Doc(new OrbDefinition("hidden") { Visible = false }, new OrbDefinition("shown"));
        doc = At(doc, "A|1920x1080", "hidden", A, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5));
        doc = At(doc, "A|1920x1080", "shown", A, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5));

        var result = OrbArrangement.Resolve(doc, [A]);
        Assert.Equal(new PointD(960, 520), result.Locations["shown"].Center);
        Assert.NotEqual(new PointD(960, 520), result.Locations["hidden"].Center);
    }

    [Fact]
    public void ResolvedOrbsAreNeverOffScreen()
    {
        var doc = Doc(new OrbDefinition("x") { Size = 240 });
        doc = At(doc, "tiny", "x", A, OrbAxis.Fraction(1), OrbAxis.Fraction(0));
        var tiny = new OrbDisplay("tiny", new RectD(100, 100, 800, 600), 1, true);
        var location = OrbArrangement.Resolve(doc, [tiny]).Locations["x"];
        AssertPlaced(location.Center, 120, [], tiny.Work);
    }

    [Fact]
    public void CommitStoresTheReleasedCenterInTheCurrentArrangement()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y") { Size = 120 });
        var committed = OrbArrangement.Commit(doc, [A, B], "x", new PointD(1834, 300));
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.High(12), OrbAxis.Fraction(0.28846)), committed.RecordFor("A|1920x1080+B|2560x1440", "x"));

        var onB = OrbArrangement.Commit(committed, [A, B], "y", new PointD(B.Work.Right - 18 - 90, 700));
        Assert.Equal(new OrbPositionRecord(B.Key, OrbAxis.High(12), OrbAxis.Fraction(0.5)), onB.RecordFor("A|1920x1080+B|2560x1440", "y"));
        Assert.Same(onB, OrbArrangement.Commit(onB, [A, B], "missing", new PointD(1, 1)));
        Assert.Same(onB, OrbArrangement.Commit(onB, [], "x", new PointD(1, 1)));
    }

    // New orbs

    [Fact]
    public void NewOrbsTakeTheFirstFreeTouchingSlot()
    {
        Assert.Equal([new PointD(800, 520), new PointD(960, 680), new PointD(1120, 520), new PointD(960, 360)], OrbPlacement.TouchingSlots(Disc(960, 520), R, 1));
        Assert.Equal(new PointD(800, 520), OrbPlacement.PlaceNew(R, Disc(960, 520), [Disc(960, 520)], Work, 1));
        Assert.Equal(new PointD(86, 680), OrbPlacement.PlaceNew(R, Disc(86, 520), [], Work, 1));
        Assert.Equal(new PointD(246, 954), OrbPlacement.PlaceNew(R, Disc(86, 954), [], Work, 1));
        Assert.Equal(new PointD(86, 794), OrbPlacement.PlaceNew(R, Disc(86, 954), [Disc(246, 954)], Work, 1));
        Assert.Equal(new PointD(960, 520 + 74 + 18 + 111), OrbPlacement.PlaceNew(111, Disc(960, 520), [Disc(800, 520)], Work, 1.5));
    }

    [Fact]
    public void NewOrbsSearchAGridWhenEverySlotIsTaken()
    {
        var anchor = Disc(960, 520);
        OrbDisc[] others = [anchor, Disc(800, 520), Disc(960, 680), Disc(1120, 520), Disc(960, 360)];
        var placed = OrbPlacement.PlaceNew(R, anchor, others, Work, 1);
        AssertPlaced(placed, R, others, Work);
        var i = (placed.X - 960) / 24;
        var j = (placed.Y - 520) / 24;
        Assert.Equal(Math.Round(i), i, 9);
        Assert.Equal(Math.Round(j), j, 9);

        var best = OrbGeometry.Distance(placed, anchor.Center);
        for (var gx = -40; gx <= 40; gx++)
        {
            for (var gy = -22; gy <= 22; gy++)
            {
                var point = new PointD(960 + gx * 24, 520 + gy * 24);
                if (OrbGeometry.Distance(point, anchor.Center) < best - 1e-6)
                {
                    Assert.False(OrbGeometry.Fits(point, R, Work, 1) && OrbGeometry.IsFree(point, R, others, 1), $"{point} is closer and free");
                }
            }
        }
    }

    [Fact]
    public void NewOrbsCascadeWhenTheDisplayIsFull()
    {
        var work = new RectD(0, 0, 300, 300);
        Assert.Null(OrbPlacement.GridSearch(new PointD(150, 150), R, [Disc(150, 150)], work, 1));
        Assert.Equal(new PointD(174, 174), OrbPlacement.PlaceNew(R, Disc(150, 150), [], work, 1));
        Assert.Equal(new PointD(214, 214), OrbPlacement.Cascade(new PointD(200, 200), R, work, 1));
    }

    [Fact]
    public void NewOrbsWithoutAnAnchorStartAtTheDefaultCorner()
    {
        Assert.Equal(new PointD(1822, 918), OrbPlacement.PlaceNew(R, null, [], Work, 1));
        var placed = OrbPlacement.PlaceNew(R, null, [Disc(1822, 918)], Work, 1);
        AssertPlaced(placed, R, [Disc(1822, 918)], Work);
    }

    [Fact]
    public void ArrangementPlacesNewOrbsNextToTheirOrigin()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("new"));
        doc = At(doc, "A|1920x1080+B|2560x1440", "x", B, OrbAxis.Fraction(0.5), OrbAxis.Fraction(0.5));
        var result = OrbArrangement.PlaceNew(doc, [A, B], null, "new", "x");
        var location = result.Locations["new"];
        Assert.Equal(B.Key, location.Display);
        Assert.Equal(new PointD(1920 + 1280 - 111 - 18 - 111, 700), location.Center);
        Assert.Equal(OrbPositionRecord.FromCenter(B.Key, location.Center, 111, B.Work, 1.5), result.Document.RecordFor(result.Arrangement, "new"));

        var fromMain = OrbArrangement.PlaceNew(doc, [A, B], null, "new", null);
        Assert.Equal(new OrbLocation(A.Key, new PointD(1822, 918), R), fromMain.Locations["new"]);
        Assert.Empty(OrbArrangement.PlaceNew(doc, [A, B], null, "missing", "x").Locations);
    }

    // Overlaps

    [Fact]
    public void OverlapsSlideToTheNearestFreeTouchingPosition()
    {
        Assert.Equal(new PointD(500, 300), OrbPlacement.ResolveOverlap(new PointD(500, 300), R, [Disc(960, 520)], Work, 1));
        Assert.Equal(new PointD(86, 300), OrbPlacement.ResolveOverlap(new PointD(-50, 300), R, [], Work, 1));
        AssertNear(new PointD(960 + Reach + 0.01, 520), OrbPlacement.ResolveOverlap(new PointD(1000, 520), R, [Disc(960, 520)], Work, 1));
        AssertNear(new PointD(1834 - Reach - 0.01, 520), OrbPlacement.ResolveOverlap(new PointD(1800, 520), R, [Disc(1834, 520)], Work, 1));

        var edge = OrbPlacement.ResolveOverlap(new PointD(1900, 520), R, [Disc(1834, 520)], Work, 1);
        AssertPlaced(edge, R, [Disc(1834, 520)], Work);
        Assert.Equal(Reach + 0.01, OrbGeometry.Distance(edge, new PointD(1834, 520)), 6);
    }

    // Drag and release

    [Fact]
    public void DraggingNearAnotherOrbPullsTheDiscIn()
    {
        var other = Disc(960, 520);
        var result = OrbMagnet.Drag(new PointD(1125, 530), R, [other], Work, 1);
        Assert.Equal(0, result.AttachedTo);
        Assert.Empty(result.Guides);
        Assert.Equal(Reach, OrbGeometry.Distance(result.Target, other.Center), 9);
        Assert.Equal(OrbMagnet.Touching(new PointD(1125, 530), R, other, 1), result.Target);

        var far = OrbMagnet.Drag(new PointD(1180, 560), R, [other], Work, 1);
        Assert.Null(far.AttachedTo);
        Assert.Equal(new PointD(1180, 560), far.Target);
    }

    [Fact]
    public void TouchingAlsoAlignsWithThatOrb()
    {
        var other = Disc(960, 520);
        var result = OrbMagnet.Drag(new PointD(1125, 525), R, [other], Work, 1);
        Assert.Equal(new PointD(1120, 520), result.Target);
        Assert.Equal([new OrbGuide(new PointD(886, 520), new PointD(1194, 520))], result.Guides);
        Assert.False(result.Guides[0].IsVertical);

        var below = OrbMagnet.Drag(new PointD(955, 690), R, [other], Work, 1);
        Assert.Equal(new PointD(960, 680), below.Target);
        Assert.True(below.Guides[0].IsVertical);
    }

    [Fact]
    public void NearbyOrbsAlignTheDraggedCenter()
    {
        var other = Disc(960, 520);
        var aligned = OrbMagnet.Drag(new PointD(1150, 526), R, [other], Work, 1);
        Assert.Null(aligned.AttachedTo);
        Assert.Equal(new PointD(1150, 520), aligned.Target);
        Assert.Equal([new OrbGuide(new PointD(886, 520), new PointD(1224, 520))], aligned.Guides);

        var tooFar = OrbMagnet.Drag(new PointD(1300, 526), R, [other], Work, 1);
        Assert.Equal(new PointD(1300, 526), tooFar.Target);
        Assert.Empty(tooFar.Guides);

        var both = OrbMagnet.Drag(new PointD(1155, 365), R, [Disc(1150, 170), Disc(960, 360)], Work, 1);
        Assert.Equal(new PointD(1150, 360), both.Target);
        Assert.Equal(2, both.Guides.Count);

        var overlapping = OrbMagnet.Drag(new PointD(962, 523), R, [other], Work, 1);
        Assert.Equal(new PointD(962, 523), overlapping.Target);
        Assert.Empty(overlapping.Guides);
    }

    [Fact]
    public void AlignmentNeverPullsADiscOffAnEdge()
    {
        var docked = OrbMagnet.Drag(new PointD(95, 440), R, [Disc(91, 250)], Work, 1);
        Assert.Equal(new PointD(95, 440), docked.Target);
        Assert.Empty(docked.Guides);

        var free = OrbMagnet.Drag(new PointD(395, 440), R, [Disc(391, 250)], Work, 1);
        Assert.Equal(new PointD(391, 440), free.Target);
        Assert.True(free.Guides[0].IsVertical);
        Assert.Equal(new OrbGuide(new PointD(391, 176), new PointD(391, 514)), free.Guides[0]);
    }

    [Fact]
    public void AScreenEdgeOutranksOrbToOrbTouching()
    {
        var drag = OrbMagnet.Drag(new PointD(95, 400), R, [Disc(91, 240)], Work, 1);
        Assert.Equal(0, drag.AttachedTo);
        Assert.Equal(95, drag.Target.X);
        Assert.Equal(240 + Math.Sqrt(Reach * Reach - 16), drag.Target.Y, 9);

        var release = OrbMagnet.Release(new PointD(95, 470), R, [Disc(86, 300)], Work, 1);
        Assert.Equal(new PointD(86, 460), release);
    }

    [Fact]
    public void ReleaseSnapsToEdgesThenTouchesThenResolvesOverlaps()
    {
        Assert.Equal(new PointD(86, 600), OrbMagnet.Release(new PointD(95, 600), R, [], Work, 1));
        Assert.Equal(PlacementMath.Snap(new PointD(1900, 1030), R, Work, 24, 12), OrbMagnet.Release(new PointD(1900, 1030), R, [], Work, 1));
        Assert.Equal(new PointD(1120, 520), OrbMagnet.Release(new PointD(1125, 525), R, [Disc(960, 520)], Work, 1));
        AssertNear(new PointD(960 + Reach + 0.01, 520), OrbMagnet.Release(new PointD(1000, 520), R, [Disc(960, 520)], Work, 1));
        Assert.Equal(new PointD(500, 300), OrbMagnet.Release(new PointD(500, 300), R, [Disc(960, 520)], Work, 1));

        var scaled = OrbMagnet.Release(new PointD(1920 + 30 + 111, 700), 111, [], B.Work, 1.5);
        Assert.Equal(new PointD(1920 + 18 + 111, 700), scaled);
    }

    [Fact]
    public void ArrangementDragAndReleaseUseTheOrbsOnThatDisplay()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y"), new OrbDefinition("hidden") { Visible = false });
        var centers = new Dictionary<string, PointD>
        {
            ["x"] = new(960, 520),
            ["y"] = new(300, 300),
            ["hidden"] = new(1120, 520),
        };
        var drag = OrbArrangement.Drag(doc, [A, B], centers, "y", new PointD(1125, 525))!;
        Assert.Equal(new PointD(1120, 520), drag.Target);

        var release = OrbArrangement.Release(doc, [A, B], centers, "y", new PointD(1000, 520))!.Value;
        Assert.Equal(A.Key, release.Display);
        AssertNear(new PointD(960 + Reach + 0.01, 520), release.Center);

        var onB = OrbArrangement.Release(doc, [A, B], centers, "y", new PointD(2000, 300))!.Value;
        Assert.Equal(new OrbLocation(B.Key, new PointD(1920 + 18 + 111, 300), 111), onB);
        Assert.Null(OrbArrangement.Release(doc, [A, B], centers, "missing", new PointD(1, 1)));
    }

    // Split

    [Fact]
    public void SplitFormsARowTowardTheInterior()
    {
        var layout = OrbPlacement.SplitLayout(Disc(600, 520), [R, R, R], Work, 1, []);
        Assert.Equal([new PointD(760, 520), new PointD(920, 520), new PointD(1080, 520)], layout.Children);
        Assert.Empty(layout.Pushed);

        var fromRight = OrbPlacement.SplitLayout(Disc(1500, 300), [R, R], Work, 1, []);
        Assert.Equal([new PointD(1340, 300), new PointD(1180, 300)], fromRight.Children);
    }

    [Fact]
    public void SplitFromASideEdgeFormsAColumn()
    {
        var down = OrbPlacement.SplitLayout(Disc(1834, 300), [R, R, R], Work, 1, []);
        Assert.Equal([new PointD(1834, 460), new PointD(1834, 620), new PointD(1834, 780)], down.Children);

        var up = OrbPlacement.SplitLayout(Disc(86, 954), [R], Work, 1, []);
        Assert.Equal([new PointD(86, 794)], up.Children);

        var top = OrbPlacement.SplitLayout(Disc(400, 86), [R], Work, 1, []);
        Assert.Equal([new PointD(560, 86)], top.Children);
    }

    [Fact]
    public void SplitWrapsIntoTheNextColumnOrRow()
    {
        var column = OrbPlacement.SplitLayout(Disc(1834, 700), [R, R, R, R, R], Work, 1, []);
        Assert.Equal(
            [new PointD(1834, 540), new PointD(1834, 380), new PointD(1834, 220), new PointD(1674, 700), new PointD(1674, 540)],
            column.Children);

        var row = OrbPlacement.SplitLayout(Disc(1500, 900), [R, R, R, R, R, R, R, R, R, R], Work, 1, []);
        Assert.Equal(new PointD(1340, 900), row.Children[0]);
        Assert.Equal(new PointD(1500, 740), row.Children[8]);
        Assert.Equal(new PointD(1340, 740), row.Children[9]);
        Assert.All(row.Children, c => Assert.True(OrbGeometry.Fits(c, R, Work, 1)));
        var discs = row.Children.Select(c => Disc(c.X, c.Y)).Append(Disc(1500, 900)).ToArray();
        for (var i = 0; i < discs.Length; i++)
        {
            Assert.True(OrbGeometry.IsFree(discs[i].Center, R, discs.Where((_, k) => k != i).ToArray(), 1));
        }
    }

    [Fact]
    public void SplitPushesOrbsInTheWayAlongTheSameAxis()
    {
        var layout = OrbPlacement.SplitLayout(Disc(600, 520), [R, R], Work, 1, [Disc(900, 520), Disc(1200, 520), Disc(600, 900)]);
        Assert.Equal([new PointD(760, 520), new PointD(920, 520)], layout.Children);
        Assert.Equal(new PointD(1080, 520), layout.Pushed[0]);
        Assert.Equal(new PointD(1240, 520), layout.Pushed[1]);
        Assert.False(layout.Pushed.ContainsKey(2));
    }

    [Fact]
    public void ArrangementPlacesSplitChildrenAndStoresThem()
    {
        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("other"));
        doc = At(doc, "A|1920x1080", "x", A, OrbAxis.Fraction(0.3125), OrbAxis.Fraction(0.5));
        doc = At(doc, "A|1920x1080", "other", A, OrbAxis.Fraction(0.46875), OrbAxis.Fraction(0.5));
        var split = OrbEditing.Split(doc, "x", SourceSet.Create([SourceKind.Codex, SourceKind.ClaudeCode, SourceKind.Gemini]), Ids("c1", "c2"))!;
        Assert.Equal(["c1", "c2"], split.Created);

        var placed = OrbArrangement.PlaceSplit(split.Document, [A], null, "x", split.Created);
        Assert.Equal(new PointD(760, 520), placed.Locations["c1"].Center);
        Assert.Equal(new PointD(920, 520), placed.Locations["c2"].Center);
        Assert.Equal(new PointD(1080, 520), placed.Locations["other"].Center);
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.Fraction(0.5625), OrbAxis.Fraction(0.5)), placed.Document.RecordFor("A|1920x1080", "other"));

        var resolved = OrbArrangement.Resolve(placed.Document, [A]);
        Assert.Equal(new PointD(600, 520), resolved.Locations["x"].Center);
        AssertNear(new PointD(760, 520), resolved.Locations["c1"].Center, 0.05);
    }

    // Corners and Reset Position

    [Fact]
    public void CornersCycleClockwiseFromTopRight()
    {
        Assert.Equal([new PointD(1834, 86), new PointD(1834, 954), new PointD(86, 954), new PointD(86, 86)], OrbPlacement.Corners(R, Work, 1));
        Assert.Equal(new PointD(1834, 954), OrbPlacement.NextCorner(Disc(1834, 86), [], Work, 1));
        Assert.Equal(new PointD(86, 954), OrbPlacement.NextCorner(Disc(1834, 954), [], Work, 1));
        Assert.Equal(new PointD(86, 86), OrbPlacement.NextCorner(Disc(86, 954), [], Work, 1));
        Assert.Equal(new PointD(1834, 86), OrbPlacement.NextCorner(Disc(86, 86), [], Work, 1));
        Assert.Equal(new PointD(86, 954), OrbPlacement.NextCorner(Disc(1500, 800), [], Work, 1));
    }

    [Fact]
    public void CornerCyclingSkipsOccupiedCorners()
    {
        Assert.Equal(new PointD(86, 954), OrbPlacement.NextCorner(Disc(1834, 86), [Disc(1834, 954)], Work, 1));
        OrbDisc[] full = [Disc(1834, 954), Disc(86, 954), Disc(86, 86)];
        var squeezed = OrbPlacement.NextCorner(Disc(1834, 86), full, Work, 1);
        AssertPlaced(squeezed, R, full, Work);
        Assert.Equal(Reach + 0.01, OrbGeometry.Distance(squeezed, new PointD(1834, 954)), 6);

        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y"));
        var centers = new Dictionary<string, PointD> { ["x"] = new(1834, 86), ["y"] = new(1834, 954) };
        var moved = OrbArrangement.MoveToNextCorner(doc, [A], centers, "x");
        Assert.Equal(new PointD(86, 954), moved.Locations["x"].Center);
        Assert.Equal(new OrbPositionRecord(A.Key, OrbAxis.Low(12), OrbAxis.High(12)), moved.Document.RecordFor("A|1920x1080", "x"));
    }

    [Fact]
    public void ResetPositionReturnsToTheDefaultCornerOrNextToItsOccupant()
    {
        Assert.Equal(new PointD(1822, 918), OrbPlacement.ResetPosition(R, [], Work, 1));
        Assert.Equal(new PointD(1662, 918), OrbPlacement.ResetPosition(R, [Disc(1822, 918)], Work, 1));

        var doc = Doc(new OrbDefinition("x"), new OrbDefinition("y"));
        var centers = new Dictionary<string, PointD> { ["x"] = new(2500, 400), ["y"] = new(1822, 918) };
        var reset = OrbArrangement.ResetPosition(doc, [A, B], centers, "x");
        Assert.Equal(new OrbLocation(A.Key, new PointD(1662, 918), R), reset.Locations["x"]);
        Assert.NotNull(reset.Document.RecordFor("A|1920x1080+B|2560x1440", "x"));
    }

    [Fact]
    public void ScaleIsValidated()
    {
        Assert.Equal(1, OrbGeometry.ValidScale(double.NaN));
        Assert.Equal(1, OrbGeometry.ValidScale(0));
        Assert.Equal(1, OrbGeometry.ValidScale(-2));
        Assert.Equal(1.5, OrbGeometry.ValidScale(1.5));
        Assert.Equal(8, OrbGeometry.ValidScale(100));
        Assert.Equal(OrbEdge.Left, OrbGeometry.DockedEdge(Disc(86, 86), Work, 1));
        Assert.Equal(OrbEdge.Bottom, OrbGeometry.DockedEdge(Disc(500, 954), Work, 1));
        Assert.Null(OrbGeometry.DockedEdge(Disc(500, 500), Work, 1));
    }

    private static Func<string> Ids(params string[] ids)
    {
        var queue = new Queue<string>(ids);
        return () => queue.Count > 0 ? queue.Dequeue() : Guid.NewGuid().ToString();
    }
}
