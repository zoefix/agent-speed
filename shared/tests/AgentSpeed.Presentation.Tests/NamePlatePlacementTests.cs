using AgentSpeed.Presentation.Layout;
using AgentSpeed.Presentation.Orb.Names;

namespace AgentSpeed.Presentation.Tests;

public class NamePlatePlacementTests
{
    private const double Margin = 12;
    private const double Gap = 12;
    private static readonly RectD Screen = new(0, 0, 1920, 1080);

    /// <summary>
    /// Every plate wants <paramref name="natural"/> pt and shortens to its budget, never below 40 pt (one grapheme and the
    /// ellipsis).
    /// </summary>
    private static NamePlatePlaced?[] Resolve(IReadOnlyList<NamePlateSubject> subjects, double natural = 100) =>
        NamePlatePlacement.Resolve(subjects, (_, budget) => Math.Max(40, Math.Min(natural, budget)));

    private static NamePlateSubject Orb(double x, double y, double size = 112, bool plate = true, RectD? area = null) =>
        new(new PointD(x, y), size, area ?? Screen, plate);

    private static NamePlateSubject[] Row(int count, double size, double y, double left = Margin) =>
        [.. Enumerable.Range(0, count).Select(i => Orb(left + size / 2 + i * (size + Gap), y, size))];

    private static NamePlateSlot?[] Slots(NamePlatePlaced?[] placed) => [.. placed.Select(p => p?.Slot)];

    [Fact]
    public void ATopRowHasItsPlatesBelow()
    {
        var row = Row(4, 112, Margin + 56);
        Assert.All(Slots(Resolve(row)), slot => Assert.Equal(NamePlateSlot.Below, slot));
    }

    [Fact]
    public void ARowDockedAtTheBottomEdgeHasItsPlatesAbove()
    {
        var row = Row(3, 148, Screen.Bottom - Margin - 74);
        Assert.All(Slots(Resolve(row)), slot => Assert.Equal(NamePlateSlot.Above, slot));
    }

    [Fact]
    public void AColumnDockedAtTheRightEdgeListsItsNamesTowardTheScreenLikeALegend()
    {
        var x = Screen.Right - Margin - 56;
        NamePlateSubject[] column = [.. Enumerable.Range(0, 3).Select(i => Orb(x, Margin + 56 + i * (112 + Gap)))];
        var placed = Resolve(column);
        Assert.All(Slots(placed), slot => Assert.Equal(NamePlateSlot.Left, slot));
        Assert.All(placed, p => Assert.Equal(x - 56 - 4, p!.Value.Rect.Right, 9));
        Assert.All(placed.Zip(column), p => Assert.Equal(p.Second.Center.Y, p.First!.Value.Rect.Center.Y, 9));
    }

    [Fact]
    public void AColumnDockedAtTheLeftEdgeListsItsNamesToTheRight()
    {
        NamePlateSubject[] column = [.. Enumerable.Range(0, 3).Select(i => Orb(Margin + 56, 300 + i * (112 + Gap)))];
        Assert.All(Slots(Resolve(column)), slot => Assert.Equal(NamePlateSlot.Right, slot));
    }

    [Fact]
    public void ATwoByTwoClusterPutsTheTopPairAboveAndTheBottomPairBelow()
    {
        NamePlateSubject[] cluster =
        [
            Orb(800, 400), Orb(800 + 124, 400),
            Orb(800, 400 + 124), Orb(800 + 124, 400 + 124),
        ];
        Assert.Equal([NamePlateSlot.Above, NamePlateSlot.Above, NamePlateSlot.Below, NamePlateSlot.Below], Slots(Resolve(cluster)));
    }

    [Fact]
    public void APlateWithNoRoomHides()
    {
        // A crowd: the middle orb of a 3 x 3 block (one corner missing) is boxed in on every side.
        var crowd = new List<NamePlateSubject>();
        for (var row = 0; row < 3; row++)
        {
            for (var column = 0; column < 3; column++)
            {
                if (row == 0 && column == 0)
                {
                    continue;
                }

                crowd.Add(Orb(700 + column * 124, 300 + row * 124));
            }
        }

        var placed = Resolve(crowd);
        var middle = crowd.FindIndex(s => s.Center == new PointD(824, 424));
        Assert.Null(placed[middle]);
        Assert.Equal(7, placed.Count(p => p is not null));

        // An area barely larger than the disc leaves no slot at all.
        var tight = new RectD(0, 0, 130, 130);
        Assert.Null(Assert.Single(Resolve([Orb(65, 65, area: tight)])));
    }

    [Fact]
    public void MixedSizesDivideTheRowInProportionAndStaySixPointsApart()
    {
        NamePlateSubject[] row = [Orb(Margin + 56, Margin + 98, 112), Orb(Margin + 112 + Gap + 98, Margin + 98, 196)];
        var placed = Resolve(row, natural: 400);
        Assert.Equal(NamePlateSlot.Below, placed[0]!.Value.Slot);
        Assert.Equal(NamePlateSlot.Below, placed[1]!.Value.Slot);
        var distance = row[1].Center.X - row[0].Center.X;
        Assert.Equal(2 * (distance * 112 / 308 - 3), placed[0]!.Value.Budget, 9);
        Assert.Equal(2 * (distance * 196 / 308 - 3), placed[1]!.Value.Budget, 9);
        Assert.Equal(6, placed[1]!.Value.Rect.X - placed[0]!.Value.Rect.Right, 9);
    }

    [Fact]
    public void ALonePlateMayUseOneAndAHalfSizes()
    {
        var placed = Resolve([Orb(900, 500, 148)], natural: 400);
        Assert.Equal(222, placed[0]!.Value.Budget);
        Assert.Equal(new RectD(900 - 111, 500 + 74 + 5, 222, 18), placed[0]!.Value.Rect);
    }

    [Fact]
    public void AnOrbWithoutAPlateIsStillAnObstacle()
    {
        NamePlateSubject[] stack = [Orb(900, 500), Orb(900, 500 + 124, plate: false)];
        var placed = Resolve(stack);
        Assert.Equal(NamePlateSlot.Above, placed[0]!.Value.Slot);
        Assert.Null(placed[1]);
    }

    [Fact]
    public void OrbsOnDifferentDisplaysNeverInteract()
    {
        var other = new RectD(0, 0, 1280, 720);
        NamePlateSubject[] orbs = [Orb(900, 500), Orb(900, 500 + 124, area: other), Orb(900 + 124, 500, area: other)];
        var placed = Resolve(orbs, natural: 160);
        Assert.Equal(NamePlateSlot.Below, placed[0]!.Value.Slot);
        Assert.Equal(168, placed[0]!.Value.Budget);
        Assert.Equal(NamePlateSlot.Below, placed[1]!.Value.Slot);
        Assert.Equal(NamePlateSlot.Below, placed[2]!.Value.Slot);
    }

    [Fact]
    public void ASidePlateIsLimitedByTheScreenEdge()
    {
        // A column docked at the left edge with its plates on the right, then one orb close to the right edge.
        var orb = Orb(Screen.Right - 56 - 110, 500);
        var budget = NamePlatePlacement.SideBudget(orb, NamePlateSlot.Right);
        Assert.Equal(110 - 4 - 3, budget, 9);
        Assert.Equal(168, NamePlatePlacement.SideBudget(orb, NamePlateSlot.Left), 9);
    }

    [Fact]
    public void PlatesKeepClearOfEachOtherAndOfOtherDiscs()
    {
        NamePlateSubject[] scene =
        [
            .. Row(4, 112, Margin + 56),
            .. Row(3, 148, Screen.Bottom - Margin - 74),
            Orb(800, 400), Orb(924, 400), Orb(800, 524), Orb(924, 524),
            Orb(1400, 700, 196),
        ];
        var placed = Resolve(scene, natural: 140);
        var rects = placed.Where(p => p is not null).Select(p => p!.Value.Rect).ToArray();
        for (var i = 0; i < rects.Length; i++)
        {
            Assert.True(rects[i].X >= 3 && rects[i].Y >= 3 && rects[i].Right <= Screen.Right - 3 && rects[i].Bottom <= Screen.Bottom - 3);
            for (var j = i + 1; j < rects.Length; j++)
            {
                var apart = rects[i].X >= rects[j].Right + 4 || rects[j].X >= rects[i].Right + 4
                    || rects[i].Y >= rects[j].Bottom + 4 || rects[j].Y >= rects[i].Bottom + 4;
                Assert.True(apart, $"plates {i} and {j} overlap");
            }
        }

        for (var i = 0; i < scene.Length; i++)
        {
            for (var j = 0; j < scene.Length; j++)
            {
                if (i != j && placed[i] is { } plate)
                {
                    var reach = scene[j].Size / 2 + 4;
                    Assert.True(plate.Rect.DistanceSquaredTo(scene[j].Center) >= reach * reach - 1e-6);
                }
            }
        }
    }

    [Fact]
    public void GroupsJoinDiscsWithinSixteenPoints()
    {
        NamePlateSubject[] orbs = [Orb(100, 100), Orb(100 + 112 + 16, 100), Orb(100 + 2 * 112 + 16 + 17, 100)];
        var groups = NamePlatePlacement.Groups(orbs, [0, 1, 2]);
        Assert.Equal([[0, 1], [2]], groups);
    }
}
